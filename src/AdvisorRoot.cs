using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace DamageAdvisor;

/// <summary>
/// 只读覆盖层：本回合打法建议（保命优先，其次最大伤害）+ 斩杀提示。
///
/// 注意：动态加载的 Mod 程序集里 Godot 的 _Ready/_Process 虚函数不会被派发，
/// 所以 UI 由 Entry 显式调用 EnsureStarted() 构建，刷新由 SceneTree.ProcessFrame 信号驱动。
/// </summary>
public partial class AdvisorRoot : CanvasLayer
{
    private PanelContainer? _panel;
    private Label? _header;
    private Label? _status;
    private Label? _enemyLine;
    private Label? _handLines;
    private Label? _planLines;
    private Label? _nextTurnLine;
    private Label? _killLine;
    private Label? _footer;
    private Label? _hintLine;
#if !WORKSHOP
    /// <summary>开发版：显示最近一次 F5 预测的"照这个顺序打"。</summary>
    private Label? _probeLine;
#endif

    private bool _started;
    private bool _loggedReady;
    private bool _loggedProcess;
    private string _lastLoggedHand = "";
    private int _handDumpCount;
    private bool _showPanel = true;
    private bool _previousToggleKey;
#if !WORKSHOP
    private bool _previousInjectKey;
#endif
    private bool _previousBudgetKey;
    private bool _previousCollapseKey;

    private bool _previousPriorityKey;
    private bool _dragging;
    private bool _previousMouseDown;
    private Vector2 _lastMouse;

    private double _settingsTimer;

    /// <summary>按键提示里的"F9 注入"只在开发版出现（工坊版编译期就没有这个功能）。</summary>
#if WORKSHOP
    private const string InjectHint = "";
#else
    private const string InjectHint = " · F9 注入 · F5 克隆探测";
#endif

    private DateTime _lastCfgWrite;
#if !WORKSHOP
    private int _injectIndex;
#endif

#if !WORKSHOP
    /// <summary>F9 依次注入的测试牌（用来验证猎人牌效，不用等抽牌）。</summary>
    private static readonly string[] TestCards =
    {
        "BladeDance", "Acrobatics", "CloakAndDagger", "DeadlyPoison", "Tactician",
        "Reflex", "HiddenDaggers", "Footwork", "Assassinate", "PiercingWail",
        "Finisher", "Mirage", "StormOfSteel", "GrandFinale",
        // v0.9.7 修复过、需要实机核对的牌
        "Sidestep", "DaggerSpray", "Expose", "FanOfKnives", "EscapePlan", "Afterimage",
        // v0.9.9 新建模、待实机验证的牌
        "Tracking", "WraithForm", "Strangle", "Pounce", "BubbleBubble", "EchoingSlash",
        // 覆盖率补齐（刀刃陷阱/爆发/手上技法/触媒）与费用修正（精密瞄准）
        "KnifeTrap", "Burst", "HandTrick", "Accelerant", "Pinpoint",
    };
#endif
    private string _signature = "";
    private double _timer;
    private DateTime _lastErrorLog = DateTime.MinValue;

    public void EnsureStarted()
    {
        if (_started)
            return;
        _started = true;

        try
        {            Layer = 100;
            ProcessMode = ProcessModeEnum.Always;
            AdvisorSettings.Load();
            BuildUi();
            SetProcess(true);
            Entry.Log("UI 已构建，面板节点数=" + _panel?.GetChildCount());
        }
        catch (Exception ex)
        {
            Entry.Log("UI 构建失败：" + ex);
        }
    }

    public override void _Ready()
    {
        if (!_loggedReady)
        {
            _loggedReady = true;
            Entry.Log("_Ready 被调用");
        }
        EnsureStarted();
    }

    public override void _Process(double delta)
    {
        if (!_loggedProcess)
        {
            _loggedProcess = true;
            Entry.Log("_Process 被调用");
        }
        Tick(delta);
    }

    public void TickFromSignal() => Tick(0.25);

    private void Tick(double delta)
    {
        try
        {
            TickCore(delta);
        }
        catch (Exception ex)
        {
            // 节点失效/场景切换导致的异常不能让 ProcessFrame 信号把它抛回引擎，
            // 也不能每帧都写一次日志文件，所以这里限流后只记一笔。
            try
            {
                SetSimple("刷新异常：" + ex.Message);
            }
            catch
            {
                // 面板节点已经不可用了
            }
            if ((DateTime.UtcNow - _lastErrorLog).TotalSeconds > 5)
            {
                _lastErrorLog = DateTime.UtcNow;
                Entry.Log("Tick 异常：" + ex);
            }
        }
    }

    private void TickCore(double delta)
    {
        // 场景切换后这个节点可能已经被释放，但信号连接还指向它（旧连接在 Entry.Attach 里已解绑，
        // 这里再兜一层，避免对已释放对象调方法）。
        if (!GodotObject.IsInstanceValid(this))
            return;

        EnsureStarted();        TickToggle();
#if !WORKSHOP
        TickInject();
        TickProbe();
#endif
        TickBudget();
        TickCollapse();

        TickPriority();
        TickDrag();

        TickSettingsFile();

        _timer += delta;
        if (_timer < 0.25)
            return;
        _timer = 0;

        Refresh();
    }

    private void BuildUi()
    {
        _panel = new PanelContainer
        {
            Name = "Panel",
            Position = new Vector2(28, 120),
            CustomMinimumSize = new Vector2(360, 0),
        };

        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.06f, 0.09f, 0.86f),
            BorderColor = new Color(0.45f, 0.85f, 1f, 0.55f),
            ContentMarginLeft = 10,
            ContentMarginRight = 10,
            ContentMarginTop = 8,
            ContentMarginBottom = 8,
        };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(6);
        _panel.AddThemeStyleboxOverride("panel", style);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 3);
        _panel.AddChild(box);

        _header = MakeLabel($"伤害顾问 v{Entry.DisplayVersion}", 15, new Color(0.70f, 0.92f, 1f));
        _status = MakeLabel("-", 13, new Color(0.95f, 0.85f, 0.55f));
        _enemyLine = MakeLabel("-", 12, new Color(0.95f, 0.75f, 0.75f));
        _handLines = MakeLabel("-", 12, new Color(0.9f, 0.9f, 0.95f));
        _planLines = MakeLabel("-", 14, new Color(0.65f, 1f, 0.7f));
        _nextTurnLine = MakeLabel("-", 11, new Color(0.62f, 0.86f, 0.86f));
        _killLine = MakeLabel("-", 13, new Color(1f, 0.85f, 0.4f));        _footer = MakeLabel("-", 11, new Color(0.65f, 0.65f, 0.72f));
        _hintLine = MakeLabel($"F6 模式 · F7 掉血上限 · F8 隐藏{InjectHint} · F10 折叠 · 拖动移动", 11, new Color(0.55f, 0.75f, 0.95f));

        foreach (Label label in new[] { _header, _status, _enemyLine, _handLines, _planLines, _nextTurnLine, _killLine, _footer, _hintLine })
            box.AddChild(label);
#if !WORKSHOP
        _probeLine = MakeLabel("", 12, new Color(1f, 0.78f, 0.45f));
        _probeLine.Visible = false;
        box.AddChild(_probeLine);
#endif        AddChild(_panel);
        ApplyCollapse();
    }

    private static Label MakeLabel(string text, int fontSize, Color color)
    {
        var label = new Label { Text = text };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", color);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        return label;
    }

    /// <summary>F7：循环"允许掉血上限"（0/1/2/3/5）。</summary>
    /// <summary>每 5 秒检查一次 cfg 是否被外部修改（F7/F10 之外的改法也能生效）。</summary>
    private void TickSettingsFile()
    {
        _settingsTimer += 0.25;
        if (_settingsTimer < 5)
            return;
        _settingsTimer = 0;

        try
        {
            string path = AdvisorSettings.ConfigPathPublic;
            if (!System.IO.File.Exists(path))
                return;
            DateTime written = System.IO.File.GetLastWriteTimeUtc(path);
            if (written == _lastCfgWrite)
                return;
            _lastCfgWrite = written;
            AdvisorSettings.Load();
            ApplyCollapse();
            Entry.Log($"配置已重新加载：掉血上限={AdvisorSettings.HpLossBudget} 折叠={AdvisorSettings.Collapsed}");
        }
        catch
        {
            // 忽略
        }
    }

    private void TickBudget()
    {
        bool pressed = Input.IsKeyPressed(Key.F7);
        if (pressed && !_previousBudgetKey)
        {
            AdvisorSettings.CycleBudget();
            ApplyCollapse();
            Entry.Log($"允许掉血上限 = {AdvisorSettings.HpLossBudget}");
        }
        _previousBudgetKey = pressed;
    }

    /// <summary>F10：折叠/展开（折叠后只留推荐与斩杀两行）。</summary>
    /// <summary>F6：优先保命 &lt;-&gt; 优先打伤害。</summary>
    private void TickPriority()
    {
        bool pressed = Input.IsKeyPressed(Key.F6);
        if (pressed && !_previousPriorityKey)
        {
            AdvisorSettings.TogglePriority();
            ApplyCollapse();
            Entry.Log("优先级模式 = " + (AdvisorSettings.DamageFirst ? "damage" : "survival"));
        }
        _previousPriorityKey = pressed;
    }

    private void TickCollapse()
    {
        bool pressed = Input.IsKeyPressed(Key.F10);
        if (pressed && !_previousCollapseKey)
        {
            AdvisorSettings.ToggleCollapsed();
            ApplyCollapse();
            Entry.Log("面板折叠 = " + AdvisorSettings.Collapsed);
        }
        _previousCollapseKey = pressed;
    }

    private void ApplyCollapse()
    {
        bool show = !AdvisorSettings.Collapsed;
        if (_header is not null) _header.Visible = show;
        if (_status is not null) _status.Visible = show;
        if (_enemyLine is not null) _enemyLine.Visible = show;
        if (_handLines is not null) _handLines.Visible = show;        if (_footer is not null) _footer.Visible = show;
        if (_nextTurnLine is not null) _nextTurnLine.Visible = show;
        if (_hintLine is not null)
        {
            _hintLine.Visible = true;
            _hintLine.Text = $"F6 {(AdvisorSettings.DamageFirst ? "输出" : "保命")} · F7 掉血上限({AdvisorSettings.HpLossBudget}) · F8 隐藏{InjectHint} · F10 折叠 · 拖动移动";
        }
    }

    /// <summary>拖动面板：按住面板内任意位置拖动即可移动（不依赖 Godot 的输入回调）。</summary>
    private void TickDrag()
    {
        if (_panel is null)
            return;

        bool down = Input.IsMouseButtonPressed(MouseButton.Left);
        Vector2 mouse = GetViewport()?.GetMousePosition() ?? Vector2.Zero;

        if (down && !_previousMouseDown)
        {
            Rect2 rect = _panel.GetRect();
            rect.Position = _panel.GlobalPosition;
            if (rect.HasPoint(mouse))
            {
                _dragging = true;
                _lastMouse = mouse;
            }
        }
        else if (!down)
        {
            _dragging = false;
        }

        if (_dragging && down)
        {
            _panel.GlobalPosition += mouse - _lastMouse;
            _lastMouse = mouse;
        }

        _previousMouseDown = down;
    }

#if !WORKSHOP
    private bool _previousProbeKey;

    /// <summary>F5：跑一次"捕获 + 克隆"可行性探测（engine/rewrite 第一阶段），只读真机。</summary>
    private void TickProbe()
    {
        bool pressed = Input.IsKeyPressed(Key.F5);
        if (pressed && !_previousProbeKey)
        {
            CombatState? state = CombatManager.Instance?.DebugOnlyGetState();
            Player? me = state is null ? null : LocalContext.GetMe(state);
            if (state is null || me is null)
                Entry.Log("探测：不在战斗中或找不到自己");
            else
                CloneProbe.Run(me, state);
        }
        _previousProbeKey = pressed;
    }

    private void TickInject()
    {
        bool pressed = Input.IsKeyPressed(Key.F9);
        if (pressed && !_previousInjectKey)
            InjectNextTestCard();
        _previousInjectKey = pressed;
    }

    /// <summary>
    /// F9 优先补齐的"探测牌"：能力 / 格挡 / 状态 / 攻击 各一张。
    /// 差分验证要靠这几类牌（能力牌验持续效果、状态牌验上状态、攻击牌验伤害、格挡牌验余像），
    /// 缺哪类补哪类，省得等抽牌。
    /// </summary>
    private static readonly (string ClassName, string Role)[] ProbeKit =
    {
        ("Afterimage", "能力牌·余像（每打出一张牌 +1 格挡）"),
        ("DefendSilent", "格挡牌·防御"),
        ("DeadlyPoison", "状态牌·致命毒药（上毒）"),
        ("StrikeSilent", "攻击牌·打击"),
    };

    /// <summary>F9：先补齐探测需要的牌，都齐了再按 TestCards 循环注入。</summary>
    private void InjectNextTestCard()
    {
        try
        {
            CombatManager? manager = CombatManager.Instance;
            CombatState? state = manager?.DebugOnlyGetState();
            if (manager is null || !manager.IsInProgress || state is null)
            {
                Entry.Log("注入失败：不在战斗中");
                return;
            }            // 联机时禁止注入：会在本地真实改变战斗状态，可能造成不同步
            if (state.Players.Count > 1)
            {
                Entry.Log($"注入被拒绝：当前是联机（{state.Players.Count} 人），注入测试牌可能造成不同步");
                return;
            }

            Player? me = LocalContext.GetMe(state);
            if (me is null)
            {
                Entry.Log("注入失败：找不到自己");
                return;
            }

            // ① 先补齐"探测需要的牌"：能力 / 格挡 / 状态 / 攻击 各一张，缺哪类补哪类
            //    （这样不用等抽到牌就能跑 F5 的差分验证；一次补一张，连按 F9 即可）
            IReadOnlyList<CardModel> hand = me.PlayerCombatState?.Hand.Cards ?? Array.Empty<CardModel>();
            foreach ((string want, string role) in ProbeKit)
            {
                bool alreadyHave = false;
                foreach (CardModel c in hand)
                {
                    if (c.GetType().Name == want)
                    {
                        alreadyHave = true;
                        break;
                    }
                }
                if (alreadyHave)
                    continue;

                CardModel? kitModel = FindCardModel(want);
                if (kitModel is null)
                {
                    Entry.Log($"补齐失败：卡池里没有 {want}（跳过，走循环注入）");
                    break;
                }
                Entry.Log($"F9 补齐探测牌：{role}（手里没有这一类）");
                InjectAsync(state, me, state.CreateCard(kitModel, me), want);
                return;
            }

            // ② 探测牌齐了 → 按 TestCards 循环注入
            string className = TestCards[_injectIndex % TestCards.Length];
            _injectIndex++;

            CardModel? model = FindCardModel(className);
            if (model is null)
            {
                Entry.Log($"注入失败：卡池里没有 {className}");
                return;
            }

            CardModel instance = state.CreateCard(model, me);
            InjectAsync(state, me, instance, className);
        }
        catch (Exception ex)
        {
            Entry.Log("注入异常：" + ex);
        }
    }

    private static CardModel? FindCardModel(string className)
    {
        foreach (CardModel candidate in ModelDb.AllCards)
        {
            if (candidate.GetType().Name == className)
                return candidate;
        }
        return null;
    }

    /// <summary>异步注入：手牌满时先弃掉最后一张（手牌上限 10）。</summary>
    private async void InjectAsync(CombatState state, Player me, CardModel instance, string className)
    {
        try
        {
            IReadOnlyList<CardModel> hand = me.PlayerCombatState?.Hand.Cards ?? Array.Empty<CardModel>();
            if (hand.Count >= 10 && hand.Count > 0)
            {
                CardModel last = hand[hand.Count - 1];
                Entry.Log($"手牌已满（{hand.Count}），先弃掉 {DamageModel.SafeName(last)}");
                await CardPileCmd.Add(last, PileType.Discard, CardPilePosition.Top, null, false);
            }

            await CardPileCmd.AddGeneratedCardToCombat(instance, PileType.Hand, me, CardPilePosition.Top);
            Entry.Log($"注入测试牌 #{_injectIndex} {className} → {DamageModel.SafeName(instance)} vars=[{DamageModel.DescribeVars(instance)}]");
        }
        catch (Exception ex)
        {
            Entry.Log("注入异常：" + ex);
        }
    }
#endif

    private void TickToggle()
    {
        bool pressed = Input.IsKeyPressed(Key.F8);
        if (pressed && !_previousToggleKey)
        {
            _showPanel = !_showPanel;
            if (_panel is not null)
                _panel.Visible = _showPanel;
            Entry.Log("面板显示=" + _showPanel);
        }
        _previousToggleKey = pressed;
    }

    private void Refresh()
    {
        CombatManager? manager = CombatManager.Instance;
        if (manager is null || !manager.IsInProgress)
        {
            _lastLoggedHand = "";
            _handDumpCount = 0;
            SetSimple("未在战斗中");
            return;
        }

        CombatState? state = manager.DebugOnlyGetState();
        if (state is null)
        {
            _lastLoggedHand = "";
            _handDumpCount = 0;
            SetSimple("未在战斗中");
            return;
        }

        if (state.CurrentSide != CombatSide.Player)
        {
            SetSimple("敌方回合");
            return;
        }

        Player? me = LocalContext.GetMe(state);
        PlayerCombatState? pcs = me?.PlayerCombatState;
        Creature? myCreature = me?.Creature;
        if (me is null || pcs is null || myCreature is null)
        {
            SetSimple("读取不到自己的战斗状态");
            return;
        }

        List<Creature> enemies = state.Enemies.Where(e => e.IsAlive).ToList();
        if (enemies.Count == 0)
        {
            SetSimple("没有存活的敌人");
            return;
        }

#if !WORKSHOP
        // 差分验证：面板每次刷新都用实机状态核对上一次预测 ——
        // 这样"照预测打完之后"的那一刻必然被抓到，不需要玩家掐时机按 F5。
        CloneProbe.CheckPendingLive(me, state);

        // 顺序提示也在这里刷新：它要在签名比对之前更新，否则按 F5 后（签名没变）面板不会重画
        if (_probeLine is not null)
        {
            string seq = CloneProbe.LastSequence;
            _probeLine.Visible = !AdvisorSettings.Collapsed && seq.Length > 0;
            _probeLine.Text = seq;
        }
#endif

        if (pcs.Phase != PlayerTurnPhase.Play)
        {
            SetSimple($"等待出牌阶段（当前 {pcs.Phase}）");
            return;
        }

        IReadOnlyList<CardModel> hand = pcs.Hand.Cards;
        string signature = BuildSignature(state, pcs, myCreature, hand);
        if (signature == _signature)
            return;
        _signature = signature;

        List<Creature> allies = state.PlayerCreatures.ToList();
        TurnAdvice advice = DamageModel.Solve(
            hand,
            pcs.DrawPile.Cards,
            pcs.ExhaustPile.Cards,
            pcs.Energy,
            pcs.MaxEnergy,
            pcs.DiscardPile.Cards.Count,
            state.Enemies,
            allies,
            myCreature,
            myCreature.CurrentHp,
            myCreature.Block);

        TurnPlan plan = advice.Plan;

        string handKey = string.Join(",", hand.Select(SafeId));
        if (handKey != _lastLoggedHand && _handDumpCount < 25)
        {
            _lastLoggedHand = handKey;

#if !WORKSHOP
            Entry.DumpSilentPoolOnce();
#endif
            _handDumpCount++;
            Entry.Log($"手牌快照#{_handDumpCount} turn={pcs.TurnNumber} energy={pcs.Energy} hand={hand.Count} draw={pcs.DrawPile.Cards.Count} enemies={enemies.Count} hp={myCreature.CurrentHp} block={myCreature.Block} incoming={advice.IncomingDamage} nodes={advice.NodesExplored}");
            foreach (CardEffect e in advice.HandEffects)
                Entry.Log($"  牌 {e.Name} cost={e.Cost} dmg={e.Damage} all={e.HitsAll} block={e.Block} poison={e.Poison} draw={e.Draw} shivs={e.Shivs} ok={e.Supported} note={e.Note} vars=[{DamageModel.DescribeVars(e.Source!)}]");
            foreach (SimEnemy se in advice.EnemiesBefore)
            {
                // ⚠️ 按屏幕编号取怪，不能按名字：同名怪（史莱姆群等）会取错，取不到还会把 null 传下去
                Creature? foe = se.Index >= 1 && se.Index <= state.Enemies.Count ? state.Enemies[se.Index - 1] : null;
                string intent = foe is null ? "?" : DamageModel.DescribeIntent(foe, allies);
                string byMe = foe is null ? "?" : DamageModel.IncomingOf(foe, myCreature).Total.ToString();
                string byTeam = foe is null ? "?" : DamageModel.IncomingOfAllies(foe, allies).Total.ToString();
                string buffs = foe is null ? "?" : DamageModel.DescribePowers(foe);
                Entry.Log($"  敌 {se.Index}.{se.Name} hp={se.Hp} blk={se.Block} incoming={se.IncomingWith(advice.PlayerIntangible)} | {intent}"
                          + $" | 按我算={byMe} 按全队算={byTeam} | buff[{buffs}]");
            }
            Entry.Log($"  计划 {string.Join(" -> ", plan.Actions.Select(a => a.TargetIndex > 0 ? $"{a.Card.Name}->{a.TargetIndex}号" : a.Card.Name))} 伤害={plan.Damage} 格挡+={plan.Block} 掉血={plan.HpLoss} 致命={plan.Lethal} 耗能={plan.EnergySpent} 回能={plan.EnergyGained}");
            Entry.Log($"  下回合 现在结束=[{advice.NextTurnBaseline}] 照推荐打=[{plan.NextTurn}]");
        }

        if (_header is not null)
            _header.Text = $"伤害顾问 v{Entry.DisplayVersion}  ·  第 {pcs.TurnNumber} 回合  ·  能量 {pcs.Energy}/{pcs.MaxEnergy}  ·  {(AdvisorSettings.DamageFirst ? "输出优先" : "保命优先")}  ·  掉血 ≤{AdvisorSettings.HpLossBudget}";

        if (_status is not null)
        {
            string mode = state.Players.Count > 1 ? $"联机({state.Players.Count}人)" : "单人";
            _status.Text = $"{mode}  ·  HP {myCreature.CurrentHp}/{myCreature.MaxHp}  ·  格挡 {myCreature.Block}";
        }

        if (_enemyLine is not null)
        {
            // 玩家有无实体时，来袭伤害要按"每段 1 点"折算（模型内部也是这么算的）
            var enemyLines = advice.EnemiesBefore.Select(e => $"{e.Index}.{e.Name} {e.Hp}/{e.MaxHp}(来袭{e.IncomingWith(advice.PlayerIntangible)})" + (e.PowersText.Length > 0 && e.PowersText != "无" ? $" [{e.PowersText}]" : ""));
            string intangibleNote = advice.PlayerIntangible > 0 ? "（你身上有无实体，已按每段 1 点折算）" : "";
            _enemyLine.Text = "敌人：" + string.Join("  ", enemyLines) + $"\n合计来袭 {advice.IncomingDamage} 伤害（打死怪会减少）{intangibleNote}";
        }

        if (_handLines is not null)
        {
            var lines = new List<string> { "手牌：" };
            foreach (CardEffect e in advice.HandEffects)
            {
                var parts = new List<string> { $"{e.Cost}费" };
                if (e.Damage > 0)
                    parts.Add(e.HitsAll ? $"{e.Damage:0.#}伤/敌" : $"{e.Damage:0.#}伤");
                if (e.Block > 0) parts.Add($"{e.Block}格挡");
                if (e.Poison > 0) parts.Add($"中毒{e.Poison}");
                if (e.Draw > 0) parts.Add($"抽{e.Draw}");
                if (e.Shivs > 0) parts.Add($"小刀×{e.Shivs}");
                if (e.EnergyGain > 0) parts.Add($"+{e.EnergyGain}能量");
                if (e.Dexterity > 0) parts.Add($"+{e.Dexterity}敏捷");
                if (e.Vulnerable > 0) parts.Add($"易伤{e.Vulnerable}");
                if (e.StrengthLoss > 0) parts.Add($"敌力量-{e.StrengthLoss}");
                if (e.RemovesBlock) parts.Add("清格挡");
                if (e.MakesShivsHitAll) parts.Add("小刀→全体");
                if (e.Discard > 0) parts.Add($"弃{e.Discard}");
                if (e.KeywordsText.Length > 0) parts.Add($"[{e.KeywordsText}]");

                if (e.Note.Length > 0) parts.Add($"[{e.Note}]");
                lines.Add($"  {e.Name}  {string.Join(" ", parts)}");
            }
            _handLines.Text = string.Join("\n", lines);
        }

        if (_planLines is not null)
        {
            if (plan.Actions.Count == 0)
            {
                _planLines.Text = "推荐：本回合没有可打的伤害/格挡牌";
            }
            else
            {
                string order = string.Join(" → ", plan.Actions.Select(a => a.TargetIndex > 0 ? $"{a.Card.Name}[{a.TargetIndex}号]" : a.Card.Name));
                int energyLeft = pcs.Energy + plan.EnergyGained - plan.EnergySpent;
                string hurt = plan.HpLoss == 0 ? "预计无伤" : $"预计掉血 {plan.HpLoss}";
                int weakApplied = plan.Actions.Sum(a => a.Card.Weak);
                _planLines.Text = $"推荐：{order}\n伤害 {plan.Damage:0.#} · 格挡 +{plan.Block} · {hurt} · 剩 {energyLeft} 能量" + (plan.EnergyGained > 0 ? $"（耗 {plan.EnergySpent}·回 {plan.EnergyGained}）" : "") + (weakApplied > 0 ? $" · 虚弱{weakApplied}" : "");
            }
        }

        if (_nextTurnLine is not null)
        {
            // 跨回合资源账：只算"确定"的部分（抽牌堆顺序、保留效果、下回合生效的牌）
            _nextTurnLine.Text = $"下回合：现在结束 → {advice.NextTurnBaseline}\n"
                               + $"          照推荐打 → {plan.NextTurn}";
        }

        if (_killLine is not null)
        {
            var kill = new List<string>();
            foreach (SimEnemy before in advice.EnemiesBefore)
            {
                SimEnemy? after = plan.EnemiesAfter.FirstOrDefault(x => x.Index == before.Index);
                if (after is null)
                    continue;
                if (!after.Alive)
                    kill.Add($"✅ 击杀 {before.Index}号 {before.Name}");
                else if (after.DiesToPoisonWith(advice.PlayerAccelerant))
                    kill.Add($"☠ 中毒先手击杀 {before.Index}号 {before.Name}（中毒{after.Poison}，它本回合不会出手）");
                else if (after.Hp < before.Hp)
                    kill.Add($"{before.Index}号 {before.Name} {before.Hp}→{after.Hp}");
            }
            if (kill.Count == 0)
                kill.Add("本回合无击杀");
            if (plan.Lethal)
                kill.Add("⚠️ 这套打法会被打死");
            _killLine.Text = string.Join("  ·  ", kill);
        }

        if (_footer is not null)
        {
            // 伤害数字取自游戏卡面预览值，而预览值是针对"当前主目标"的，这里显式说明按哪只怪算的
            string sampleNote = advice.EnemiesBefore.Count > 0
                ? $"伤害按 {advice.EnemiesBefore[0].Index} 号怪的面板值"
                : "";
            _footer.Text = $"v{Entry.DisplayVersion} 保命优先→最大伤害 · 抽牌按牌堆顺序(不洗弃牌堆)"
                         + (sampleNote.Length > 0 ? $" · {sampleNote}" : "")
                         + $"（搜索 {advice.NodesExplored} 节点）";
        }
    }

    private static string BuildSignature(CombatState state, PlayerCombatState pcs, Creature me, IReadOnlyList<CardModel> hand)
    {
        string cards = string.Join(",", hand.Select(SafeId));
        // 敌人格挡与 debuff 都要进指纹：格挡会让整轮输出被吃光；中毒这类只变 debuff 的情况
        // （比如打完一张上毒牌之后）如果不在指纹里，面板会停在过期的建议上
        string foes = string.Join(",", state.Enemies.Select(e => $"{e.CurrentHp}/{e.Block}/{DamageModel.DebuffKey(e)}"));
        return $"{state.RoundNumber}|{pcs.TurnNumber}|{pcs.Energy}|{pcs.DrawPile.Cards.Count}"
             + $"|{me.CurrentHp}|{me.Block}|{cards}|{foes}";
    }

    private static string SafeId(CardModel card)
    {
        try
        {
            return card.Id.ToString() ?? "?";
        }
        catch
        {
            return "?";
        }
    }

    private void SetSimple(string message)
    {
        _signature = "";
        if (_status is not null)
            _status.Text = message;
        if (_handLines is not null)
            _handLines.Text = "-";
        if (_planLines is not null)
            _planLines.Text = "-";
        if (_enemyLine is not null)
            _enemyLine.Text = "-";
        if (_killLine is not null)
            _killLine.Text = "-";
        if (_nextTurnLine is not null)
            _nextTurnLine.Text = "-";
    }
}























