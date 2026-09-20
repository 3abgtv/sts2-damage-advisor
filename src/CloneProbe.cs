#if !WORKSHOP
using System.Diagnostics;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace DamageAdvisor;

/// <summary>
/// 第一阶段可行性探测（`engine/rewrite` 分支专用）：只做**捕获 + 克隆**，不做任何推进。
///
/// 回答四个问题（四项硬判据）：
///   ① 捕获零影响：抓住快照的前后，真机指纹必须逐字一致
///   ② 克隆体隔离：改克隆体（关键词/计数器/变量）→ 真机必须无变化；万一泄漏立刻还原并报警
///   ③ RNG 不推进：取 RNG 快照前后，序列化结果必须一致（取快照本身不消耗随机数）
///   ④ 开销可接受：捕获与克隆各耗时多少（目标：帧内完成）
///
/// 纪律（与 CombatSolver 的硬约束一致）：捕获必须在主线程；本类**只读真机**，
/// 唯一会写的是克隆体，且写坏真机时立即还原。
/// </summary>
internal static class CloneProbe
{
    /// <summary>最近一次预测的"照这个顺序打"，给面板显示用（空串＝没有待验证的预测）。</summary>
    public static string LastSequence { get; private set; } = "";

    /// <summary>
    /// 第三步（与面板对接）第一刀：把**主模型的推荐计划**喂给新引擎重放一遍，
    /// 两边算出来的数字并排显示 —— 不一致的地方就是其中一边的 bug。
    /// 按 Id 在影子的手牌里找牌；找不到的（例如计划里生成的小刀）计入"未重放"。
    /// </summary>
    public static string ComparePlanText { get; private set; } = "";

    public static void ComparePlan(Player me, CombatState state, TurnAdvice advice)
    {
        try
        {
            if (advice.Plan.Actions.Count == 0)
            {
                ComparePlanText = "";
                return;
            }

            SimState sim = SimState.Capture(me, state, advice);
            int hpBefore = sim.Foes.Sum(f => f.Hp);
            int blockBefore = sim.PlayerBlock;
            int skipped = 0;
            foreach (PlannedAction action in advice.Plan.Actions)
            {
                int idx = -1;
                for (int i = 0; i < sim.Hand.Count; i++)
                {
                    if (sim.Hand[i].Id == action.Card.Id)
                    {
                        idx = i;
                        break;
                    }
                }
                if (idx < 0)
                {
                    skipped++;
                    continue;
                }
                SimCommands.PlayCard(sim, idx, action.TargetIndex);
            }

            int damage = hpBefore - sim.Foes.Sum(f => f.Hp);
            int block = sim.PlayerBlock - blockBefore;
            int hpLoss = Math.Max(0, advice.IncomingDamage - sim.PlayerBlock);
            int energyLeft = sim.PlayerEnergy;

            ComparePlanText = $"新引擎重放推荐：伤害 {damage} / 格挡 +{block} / 掉血 {hpLoss} / 剩 {energyLeft} 能量"
                            + $"　（主模型：伤害 {advice.Plan.Damage:0.#} / 格挡 +{advice.Plan.Block} / 掉血 {advice.Plan.HpLoss}）"
                            + (skipped > 0 ? $"　[{skipped} 张未重放]" : "");
        }
        catch (Exception ex)
        {
            ComparePlanText = "新引擎重放异常：" + ex.GetType().Name;
        }
    }

    /// <summary>按下探测键时调用；不做任何推进、不修改真机状态。</summary>
    public static void Run(Player me, CombatState state)
    {
        try
        {
            RunInner(me, state);
        }
        catch (Exception ex)
        {
            Entry.Log("探测异常：" + ex);
        }
    }

    private static void RunInner(Player me, CombatState state)
    {
        PlayerCombatState? pcs = me.PlayerCombatState;
        if (pcs is null)
        {
            Entry.Log("探测：拿不到 PlayerCombatState");
            return;
        }

        var sw = Stopwatch.StartNew();

        // ① 捕获：把 live 值抄进指纹（前后各取一次做比对）
        string before = Fingerprint(me, state);
        long captureMs = sw.ElapsedMilliseconds;
        if (string.IsNullOrEmpty(before))
        {
            Entry.Log("探测：指纹为空，放弃");
            return;
        }

        // ③ RNG 快照（取之前先记一次序列化结果，用于验证"取快照不推进"）
        string rngBefore = RngState(state);
        sw.Restart();
        string rngSnapshot = RngState(state);
        long rngMs = sw.ElapsedMilliseconds;

        // ② 克隆：手牌里第一张牌 + 第一个遗物
        sw.Restart();
        string cloneReport = CloneAndCheck(me, pcs);
        long cloneMs = sw.ElapsedMilliseconds;

        // 收尾：再取一次指纹与 RNG，三者都比对
        string after = Fingerprint(me, state);
        string rngAfter = RngState(state);

        bool captureClean = before == after;
        bool rngClean = rngBefore == rngSnapshot && rngBefore == rngAfter;

        Entry.Log($"[探测] ①捕获 {captureMs}ms ②克隆 {cloneMs}ms ③RNG {rngMs}ms");
        Entry.Log($"[探测] ① 捕获零影响：{(captureClean ? "✓ 前后指纹一致" : "✗ 真机状态变了！")}");
        Entry.Log($"[探测] ③ RNG 不推进：{(rngClean ? "✓ 取快照前后序列化一致" : "✗ RNG 被推进了！")}");
        Entry.Log($"[探测] ② 克隆体隔离：{cloneReport}");

        // ④ 第二阶段第一刀：影子状态 + 命令镜像，给一个"可在游戏里实际验证"的预测
        Entry.Log($"[探测] ④ 模拟自检：{SimSelfCheck(me, state)}");
        if (!captureClean)
            Entry.Log($"[探测] 前：{before}");
        if (!captureClean)
            Entry.Log($"[探测] 后：{after}");
    }

    /// <summary>
    /// 克隆一张手牌与一个遗物，然后检查"克隆体与真机是否共享可变状态"。
    /// 用只读方式判定（比较引用），**不去改克隆体** —— 万一它是浅拷贝，改了就会污染真机。
    /// 卡牌额外做一次无害的关键词写入测试，写完立刻比对真机；真机被带上就还原。
    /// </summary>
    private static string CloneAndCheck(Player me, PlayerCombatState pcs)
    {
        var parts = new List<string>();

        // ---- 卡牌 ----
        IReadOnlyList<CardModel> hand = pcs.Hand.Cards;
        if (hand.Count == 0)
        {
            parts.Add("卡牌：手牌为空，跳过");
        }
        else
        {
            CardModel live = hand[0];
            try
            {
                CardModel clone = live.CreateCloneForPlayer(me);
                bool sharesVars = ReferenceEquals(clone.DynamicVars, live.DynamicVars);
                bool sharesCost = ReferenceEquals(clone.EnergyCost, live.EnergyCost);
                parts.Add($"卡牌 {live.Title}：克隆成功，共享 DynamicVars={sharesVars} 共享 EnergyCost={sharesCost}"
                          + (sharesVars || sharesCost ? " ← 浅拷贝，改克隆体会污染真机" : " ← 深拷贝，可安全改"));

                // 无害写入测试：给克隆体加"保留"关键词，真机不能被带上
                bool liveHadRetain = live.Keywords.Contains(CardKeyword.Retain);
                clone.AddKeyword(CardKeyword.Retain);
                bool leaked = live.Keywords.Contains(CardKeyword.Retain) != liveHadRetain;
                if (leaked)
                {
                    live.RemoveKeyword(CardKeyword.Retain);   // 立刻还原
                    parts.Add("关键词写入：✗ 泄漏（已还原）");
                }
                else
                {
                    parts.Add("关键词写入：✓ 隔离（改克隆体，真机未变）");
                }
            }
            catch (Exception ex)
            {
                parts.Add("卡牌克隆失败：" + ex.GetType().Name + " " + ex.Message);
            }
        }

        // ---- 遗物 ----
        IReadOnlyList<RelicModel> relics = me.Relics;
        if (relics.Count == 0)
        {
            parts.Add("遗物：没有遗物，跳过");
        }
        else
        {
            RelicModel live = relics[0];
            try
            {
                RelicModel? clone = DeepCloneModel(live);
                if (clone is null)
                {
                    parts.Add($"遗物 {RelicName(live)}：深克隆失败（找不到克隆钩子）");
                }
                else
                {
                    // 先把"克隆成功 + 是否共享引用"落下来 —— 万一后面的写入测试抛异常，这半句也不会丢
                    bool sharesVars = ReferenceEquals(clone.DynamicVars, live.DynamicVars);
                    parts.Add($"遗物 {RelicName(live)}：深克隆成功，共享 DynamicVars={sharesVars}"
                              + (sharesVars ? " ← 仍共享（需要更深的拷贝）" : " ← 不共享"));

                    // 写入测试要挑一个**可叠加**的遗物（不可叠加的不能改计数）
                    RelicModel? stackable = relics.FirstOrDefault(r => r.IsStackable);
                    if (stackable is null)
                    {
                        parts.Add("写入测试：手上没有可叠加遗物，未覆盖（只看共享引用）");
                    }
                    else
                    {
                        RelicModel? stackClone = DeepCloneModel(stackable);
                        int before = stackable.DisplayAmount;
                        stackClone?.IncrementStackCount();
                        parts.Add($"写入测试（{RelicName(stackable)}）："
                                  + (stackable.DisplayAmount != before ? "✗ 泄漏！真机被改了" : "✓ 隔离（真机未变）"));
                    }
                }
            }
            catch (Exception ex)
            {
                parts.Add("遗物克隆失败：" + ex.GetType().Name + " " + ex.Message);
            }
        }

        return string.Join(" | ", parts);
    }

    /// <summary>
    /// 用"浅拷贝 + 受保护的深拷贝钩子"克隆模型 —— 等价于 CombatSolver 的 CloneModelForSimulation：
    /// MemberwiseClone → DeepCloneFields → AfterCloned。
    ///
    /// ⚠️ 不用 `ToMutable()`：那是"可变模型"，只能用在特定位置，读它就会抛
    /// `MutableModelException: ... used in incorrect place`（第一阶段实测确认）。
    /// 这三个方法都是 protected，所以只能走反射。
    /// </summary>
    private static T? DeepCloneModel<T>(T source) where T : class
    {
        object? clone = InvokeNonPublic(source, typeof(object), "MemberwiseClone");
        if (clone is null)
            return null;
        InvokeNonPublic(clone, source.GetType(), "DeepCloneFields");
        InvokeNonPublic(clone, source.GetType(), "AfterCloned");
        return clone as T;
    }

    /// <summary>捕获到的我方能力（只显示非 0 的项）—— 便于一眼核对是否与游戏面板一致。</summary>
    private static string PowerText(SimState sim)
    {
        var parts = new List<string>();
        if (sim.BlockPerCard > 0) parts.Add($"余像{sim.BlockPerCard}");
        if (sim.ShivBonus > 0) parts.Add($"精准{sim.ShivBonus}");
        if (sim.Envenom > 0) parts.Add($"涂毒{sim.Envenom}");
        if (sim.PoisonPerDraw > 0) parts.Add($"腐蚀波{sim.PoisonPerDraw}");
        if (sim.DamagePerCardPlayed > 0) parts.Add($"群蛇形态{sim.DamagePerCardPlayed}");
        if (sim.DamagePerDraw > 0) parts.Add($"速行者{sim.DamagePerDraw}");
        if (sim.PlayerStrength != 0) parts.Add($"力量{sim.PlayerStrength}");
        if (sim.PlayerDexterity != 0) parts.Add($"敏捷{sim.PlayerDexterity}");
        return parts.Count == 0 ? "" : "（能力：" + string.Join(" ", parts) + "）";
    }

    /// <summary>从一条命令说明里抠出"打出「X」"的牌名，用于拼面板上的顺序提示。</summary>
    private static string RegexCard(string step)
    {
        int start = step.IndexOf('「');
        int end = step.IndexOf('」');
        return start >= 0 && end > start ? step.Substring(start + 1, end - start - 1) : step;
    }

    /// <summary>敌人状态的紧凑显示（只显示非 0 的项）。</summary>
    private static string StatusText(SimFoe foe)
    {
        var parts = new List<string>();
        if (foe.Poison > 0) parts.Add($"中毒{foe.Poison}");
        if (foe.Weak > 0) parts.Add($"虚弱{foe.Weak}");
        if (foe.Vulnerable > 0) parts.Add($"易伤{foe.Vulnerable}");
        if (foe.Strength > 0) parts.Add($"力量{foe.Strength}");
        return parts.Count == 0 ? "" : "，状态[" + string.Join(" ", parts) + "]";
    }

    /// <summary>遗物的本地化名（RelicModel.Title 是 LocString，直接 ToString 会得到原始键）。</summary>
    private static string RelicName(RelicModel relic)
    {
        try
        {
            string text = relic.Title.GetFormattedText();
            return string.IsNullOrWhiteSpace(text) ? relic.Id.ToString() ?? "?" : text.Trim();
        }
        catch
        {
            return "?";
        }
    }

    /// <summary>调用非公开方法（沿继承链找），返回其返回值。</summary>
    private static object? InvokeNonPublic(object target, Type declaring, string name)
    {
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Public;

        System.Reflection.MethodInfo? method = declaring.GetMethod(name, flags);
        for (Type? t = declaring; method is null && t is not null; t = t.BaseType)
            method = t.GetMethod(name, flags);

        return method?.Invoke(target, null);
    }

    // ---- 差分验证闭环：上次预测 → 之后每次面板刷新都用实机状态核对 ----
    private static string _pendingCard = "";
    private static int _pendingFoeIndex;
    private static int _pendingFoeHp;
    private static int _pendingFoePoison;
    private static int _pendingFoeVulnerable;
    private static int _pendingFoeWeak;
    private static int _pendingEnergy;
    private static int _pendingPlayerBlock;
    private static string _pendingFoeName = "";
    private static int _pendingRound;
    private static string _lastMismatchLog = "";
    private static int _pendingMismatchLogs;
    private static bool _hasPending;

    /// <summary>
    /// 用**当前实机状态**核对上一次预测。面板每次刷新都会调用 ——
    /// 这样"照预测打完之后"的那一刻必然被抓到，不需要玩家掐时机按 F5。
    ///
    /// 判定：身份不符（换回合/换怪）→ 作废；六项全等 → ✓ 通过；否则**保留待核对**（等下一次刷新）。
    /// </summary>
    public static void CheckPendingLive(Player me, CombatState state)
    {
        if (!_hasPending)
            return;
        try
        {
            PlayerCombatState? pcs = me.PlayerCombatState;
            if (pcs is null)
                return;

            int index = _pendingFoeIndex - 1;
            if (index < 0 || index >= state.Enemies.Count)
            {
                Entry.Log($"[差分] 上一次预测（打「{_pendingCard}」）无法验证：目标已不在场");
                _hasPending = false;
                return;
            }

            Creature foe = state.Enemies[index];
            if (state.RoundNumber != _pendingRound || foe.Name != _pendingFoeName)
            {
                Entry.Log($"[差分] 上一次预测无法验证：状态已推进（预测时第 {_pendingRound} 回合的 {_pendingFoeName}，"
                          + $"现在是第 {state.RoundNumber} 回合的 {foe.Name}）");
                _hasPending = false;
                LastSequence = "";
                return;
            }

            int poison = DamageModel.ReadPowerAmount<PoisonPower>(foe);
            int vulnerable = DamageModel.ReadPowerAmount<VulnerablePower>(foe);
            int weak = DamageModel.ReadPowerAmount<WeakPower>(foe);
            if (foe.CurrentHp == _pendingFoeHp && poison == _pendingFoePoison && vulnerable == _pendingFoeVulnerable
                && weak == _pendingFoeWeak && pcs.Energy == _pendingEnergy && me.Creature.Block == _pendingPlayerBlock)
            {
                Entry.Log($"[差分] ✓ 通过：实机与预测一致（第 {state.RoundNumber} 回合打「{_pendingCard}」→ {foe.Name} "
                          + $"{foe.CurrentHp}血/中毒{poison}/易伤{vulnerable}/虚弱{weak}、我 {pcs.Energy}能量/{me.Creature.Block}格挡）"
                          + " —— 差分验证链闭环");
                _hasPending = false;
                LastSequence = "";
            }
            else
            {
                // 还没全等：把两边打出来。**按值去重**（同一组实机值只记一次）而不是按次数限流 ——
                // 否则"出牌前"那几次刷新会把额度用光，真正有价值的"出牌后"反而看不到。
                string actual = $"{foe.CurrentHp}血/中毒{poison}/易伤{vulnerable}/虚弱{weak}/我 {pcs.Energy}能量/{me.Creature.Block}格挡";
                if (actual != _lastMismatchLog && _pendingMismatchLogs < 12)
                {
                    _lastMismatchLog = actual;
                    _pendingMismatchLogs++;
                    Entry.Log($"[差分] 对照：预测「{_pendingCard}」→ {_pendingFoeHp}血/中毒{_pendingFoePoison}"
                              + $"/易伤{_pendingFoeVulnerable}/虚弱{_pendingFoeWeak}/我 {_pendingEnergy}能量/{_pendingPlayerBlock}格挡；"
                              + $"实机 → {actual}");
                }
            }
            // 其余情况保留待核对：打完牌后的下一次刷新就会命中
        }
        catch (Exception ex)
        {
            Entry.Log("[差分] 核对异常：" + ex.GetType().Name);
            _hasPending = false;
        }
    }

        private static void SavePending(CardEffect card, SimFoe foe, SimState sim)
    {
        _pendingCard = card.Name;
        _pendingFoeIndex = foe.Index;
        _pendingFoeHp = foe.Hp;
        _pendingFoePoison = foe.Poison;
        _pendingFoeVulnerable = foe.Vulnerable;
        _pendingFoeWeak = foe.Weak;
        _pendingEnergy = sim.PlayerEnergy;
        _pendingPlayerBlock = sim.PlayerBlock;
        _pendingFoeName = foe.Name;
        _pendingRound = sim.RoundNumber;
        _pendingMismatchLogs = 0;
        _lastMismatchLog = "";
        _hasPending = true;
    }

    /// <summary>
    /// 第二阶段第一刀：把 live 状态捕获成影子状态，在影子上打一张单体攻击牌，
    /// 输出一条"**你可以在游戏里实际打一张来核对**"的预测，并在下次按 F5 时自动核对。
    ///
    /// 这是差分验证链 —— 模拟结果 vs 实机结果，后面每接一张牌都走这个方式。
    /// </summary>
    private static string SimSelfCheck(Player me, CombatState state)
    {
        try
        {
            PlayerCombatState? pcs = me.PlayerCombatState;
            if (pcs is null)
                return "拿不到战斗状态";

            // 复用求解器已经算好的手牌解析结果：同一套语义，不重复实现
            TurnAdvice advice = DamageModel.Solve(
                pcs.Hand.Cards,
                pcs.DrawPile.Cards,
                pcs.ExhaustPile.Cards,
                pcs.Energy,
                pcs.MaxEnergy,
                pcs.DiscardPile.Cards.Count,
                state.Enemies,
                state.PlayerCreatures.ToList(),
                me.Creature,
                me.Creature.CurrentHp,
                me.Creature.Block);

            SimState sim = SimState.Capture(me, state, advice);
            if (sim.Foes.Count == 0)
                return "影子状态：没有存活敌人，跳过";

            // 优先挑**能力牌**来测（打完之后再补打一张，持续效果才看得见）；没有就测状态牌，再没有测攻击牌
            int idx = -1;
            string kind = "";
            for (int i = 0; i < sim.Hand.Count; i++)
            {
                CardEffect c = sim.Hand[i];
                bool grantsPower = c.BlockPerCard > 0 || c.ShivBonus > 0 || c.Envenom > 0
                                   || c.PoisonPerDraw > 0 || c.DamagePerCardPlayed > 0 || c.DamagePerDraw > 0;
                if (c.Supported && grantsPower && c.Cost <= sim.PlayerEnergy)
                {
                    idx = i;
                    kind = "能力牌";
                    break;
                }
            }
            if (idx < 0)
            {
                for (int i = 0; i < sim.Hand.Count; i++)
                {
                    CardEffect c = sim.Hand[i];
                    if (c.Supported && !c.HitsAll && (c.Poison > 0 || c.Weak > 0 || c.Vulnerable > 0) && c.Cost <= sim.PlayerEnergy)
                    {
                        idx = i;
                        kind = "状态牌";
                        break;
                    }
                }
            }
            if (idx < 0)
            {
                for (int i = 0; i < sim.Hand.Count; i++)
                {
                    CardEffect c = sim.Hand[i];
                    if (c.Supported && c.IsAttack && c.Damage > 0 && !c.HitsAll && c.Cost <= sim.PlayerEnergy)
                    {
                        idx = i;
                        kind = "攻击牌";
                        break;
                    }
                }
            }
            if (idx < 0)
                return $"影子状态已捕获（我 {sim.PlayerHp}血/{sim.PlayerBlock}格挡/{sim.PlayerEnergy}能量，敌 {sim.Foes.Count} 只，手牌 {sim.Hand.Count} 张），但手里没有可测的牌";

            SimFoe foe = sim.Foes[0];
            CardEffect predicted = sim.Hand[idx];   // 记下要预测的这张牌（PlayCard 会把它移出手牌）
            string before = $"我 {sim.PlayerHp}血/{sim.PlayerBlock}格挡/{sim.PlayerEnergy}能量{PowerText(sim)}；"
                          + $"敌 {foe.Index}号 {foe.Hp}血/{foe.Block}格挡{StatusText(foe)}";
            var steps = new List<string> { SimCommands.PlayCard(sim, idx, foe.Index) };

            // 能力牌：再补打一张格挡牌，让"余像这类每张牌 +N"的持续效果体现在数字上
            if (kind == "能力牌")
            {
                int second = -1;
                for (int i = 0; i < sim.Hand.Count; i++)
                {
                    if (sim.Hand[i].Block > 0 && sim.Hand[i].Cost <= sim.PlayerEnergy)
                    {
                        second = i;
                        break;
                    }
                }
                if (second >= 0)
                    steps.Add(SimCommands.PlayCard(sim, second, foe.Index));
            }

            SavePending(predicted, foe, sim);
            // 面板上直接显示"照这个顺序打"，省得去翻日志
            LastSequence = "探测：照这个顺序打 → " + string.Join(" → ", steps.Select(s => RegexCard(s)))
                         + $"（预期 我 {sim.PlayerEnergy}能量/{sim.PlayerBlock}格挡）";
            return $"影子状态已捕获（{before}）→ {string.Join(" → ", steps)} → 打完：敌 {foe.Index}号 {foe.Hp}血/{foe.Block}格挡{StatusText(foe)}，"
                 + $"我 {sim.PlayerEnergy}能量/{sim.PlayerBlock}格挡（{kind}，手牌{sim.Hand.Count}/抽牌堆{sim.Draw.Count}）"
                 + " ← 实际照这个顺序打（打完不用按 F5，面板会自动核对）";
        }
        catch (Exception ex)
        {
            return "模拟自检异常：" + ex.GetType().Name + " " + ex.Message;
        }
    }

    /// <summary>真机指纹：把会影响推算的 live 值拼成一个字符串，用于前后逐字比对。</summary>
    private static string Fingerprint(Player me, CombatState state)
    {
        try
        {
            PlayerCombatState? pcs = me.PlayerCombatState;
            if (pcs is null)
                return "";
            var sb = new System.Text.StringBuilder(256);
            sb.Append("hp=").Append(me.Creature.CurrentHp).Append("/").Append(me.Creature.Block)
              .Append(" e=").Append(pcs.Energy).Append("/").Append(pcs.MaxEnergy)
              .Append(" piles=").Append(pcs.Hand.Cards.Count).Append("/").Append(pcs.DrawPile.Cards.Count)
              .Append("/").Append(pcs.DiscardPile.Cards.Count).Append("/").Append(pcs.ExhaustPile.Cards.Count)
              .Append(" turn=").Append(pcs.TurnNumber).Append(" hand=[");
            foreach (CardModel c in pcs.Hand.Cards)
                sb.Append(c.Id).Append("(").Append(c.EnergyCost?.Canonical ?? -1).Append("),");
            sb.Append("] foes=[");
            foreach (Creature e in state.Enemies)
                sb.Append(e.Name).Append(":").Append(e.CurrentHp).Append(":").Append(e.Block).Append(",");
            sb.Append("] relics=").Append(me.Relics.Count).Append(" rng=").Append(RngState(state));
            return sb.ToString();
        }
        catch (Exception ex)
        {
            Entry.Log("指纹读取失败：" + ex.Message);
            return "";
        }
    }

    /// <summary>RNG 状态的序列化摘要（取它本身不应该推进序列）。</summary>
    private static string RngState(CombatState state)
    {
        try
        {
            object? serializable = state.RunState?.Rng?.ToSerializable();
            return serializable?.ToString() ?? "(空)";
        }
        catch (Exception ex)
        {
            return "(读取失败 " + ex.GetType().Name + ")";
        }
    }
}
#endif
