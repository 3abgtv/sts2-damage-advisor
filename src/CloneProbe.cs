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

    /// <summary>
    /// 联机降级：`Players.Count > 1` 时新引擎**完全不介入**（连快照都不捕获），继续用主模型。
    ///
    /// 理由是这套引擎的立足点：跨回合推演要求"未来唯一"，而联机的队友行动让这个前提不成立；
    /// 而且影子 `Capture` 只读到了**自己**的状态。这条决策写在 `docs/engine-rewrite-roadmap.md`
    /// 与 `STATUS.md` 里，但 2026-09-25 才发现一直没有对应的实现 ——
    /// `ComparePlan`/`Run` 是无条件跑的，文档与代码不一致。
    ///
    /// 每个入口只在第一次提示时记一条日志，免得每次面板刷新都刷屏。
    /// </summary>
    private static bool DegradedToMainModel(CombatState state, string who)
    {
        try
        {
            int players = state.Players.Count;
            if (players <= 1)
                return false;
            if (_mpLogged.Add(who))
                Entry.Log($"{who}：联机（{players} 人）→ 新引擎不介入，继续用主模型（多人降级）");
            return true;
        }
        catch
        {
            // 读不到玩家数就别拦着：宁可多跑一遍，也不要因为这里抛异常把面板搞坏
            return false;
        }
    }

    /// <summary>已经就"联机降级"提示过的入口（每个入口只说一次）。</summary>
    private static readonly HashSet<string> _mpLogged = new(StringComparer.Ordinal);

    public static void ComparePlan(Player me, CombatState state, TurnAdvice advice)
    {
        try
        {
            if (DegradedToMainModel(state, "对照重放"))
            {
                ComparePlanText = "";
                return;
            }

            if (advice.Plan.Actions.Count == 0)
            {
                ComparePlanText = "";
                return;
            }

            SimState sim = SimState.Capture(me, state, advice);
            int hpBefore = sim.Foes.Sum(f => f.Hp);
            int blockBefore = sim.PlayerBlock;
            int skipped = ReplayPlan(sim, advice);

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
            if (DegradedToMainModel(state, "F5 探测"))
            {
                LastSequence = "联机降级：新引擎不介入（多人下「未来唯一」不成立，继续用主模型）";
                _hasPending = false;
                return;
            }
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

    // ---- 差分验证闭环：上次预测（整套推荐计划的终态指纹）→ 之后每次面板刷新都用实机状态核对 ----
    private static string _pendingLabel = "";
    private static string _pendingSignature = "";
    private static string _beforeSignature = "";
    private static int _pendingRound;
    private static string _lastMismatchLog = "";
    private static int _pendingMismatchLogs;
    private static bool _hasPending;

    /// <summary>把主模型的推荐计划喂给影子状态重放；返回没能重放的张数（计划里生成的小刀等）。</summary>
    private static int ReplayPlan(SimState sim, TurnAdvice advice)
    {
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
        return skipped;
    }

    /// <summary>
    /// 终态指纹（影子）：回合 + 每只存活敌人（名字/血/中毒/易伤/虚弱）+ 我的（能量/格挡）。
    /// ⚠️ 敌人全灭时写「全灭」而不是留空：留空得到的 `r5||m:1/0` 根本看不出它是
    /// "**预测全灭**"还是"没读到敌人"，而这恰恰是玩家最需要一眼看懂的那种预测
    /// （2026-09-25 实机踩到）。实机那边用同一个词，才能继续逐字比对。
    /// </summary>
    private static string BuildSignature(SimState sim)
    {
        string foes = string.Join(",", sim.Foes.Where(f => f.Alive)
            .Select(f => $"{f.Name}:{f.Hp}/{f.Poison}/{f.Vulnerable}/{f.Weak}"));
        return "r" + sim.RoundNumber + "|" + (foes.Length == 0 ? AllDead : foes)
             + "|m:" + sim.PlayerEnergy + "/" + sim.PlayerBlock;
    }

    /// <summary>终态指纹（实机）—— 与影子版同一个格式，才能直接比对。</summary>
    private static string BuildSignature(Player me, CombatState state)
    {
        string foes = string.Join(",", state.Enemies.Where(e => e.IsAlive).Select(e =>
            $"{e.Name}:{e.CurrentHp}/" + DamageModel.ReadPowerAmount<PoisonPower>(e) + "/"
            + DamageModel.ReadPowerAmount<VulnerablePower>(e) + "/" + DamageModel.ReadPowerAmount<WeakPower>(e)));
        return "r" + state.RoundNumber + "|" + (foes.Length == 0 ? AllDead : foes)
             + "|m:" + (me.PlayerCombatState?.Energy ?? 0) + "/" + me.Creature.Block;
    }

    /// <summary>敌人全灭时指纹里写这个 —— 两个重载必须用同一个词，否则比不出来。</summary>
    private const string AllDead = "全灭";

    /// <summary>
    /// 用**当前实机状态**核对上一次预测（整套推荐计划的终态）。面板每次刷新都会调用 ——
    /// 这样"照计划打完最后一刻"必然被抓到，不需要玩家掐时机按 F5。
    ///
    /// 判定：回合变了（说明已经结束回合）→ 作废；签名逐字相等 → ✓ 通过；否则**保留待核对**（等下一次刷新）。
    /// </summary>
    public static void CheckPendingLive(Player me, CombatState state)
    {
        if (!_hasPending)
            return;
        if (DegradedToMainModel(state, "终态核对"))
        {
            _hasPending = false;
            return;
        }
        try
        {
            if (me.PlayerCombatState is null)
                return;

            if (state.RoundNumber != _pendingRound)
            {
                Entry.Log($"[差分] 上一次预测无法验证：已推进到第 {state.RoundNumber} 回合"
                          + $"（预测时第 {_pendingRound} 回合　{_pendingLabel}）");
                _hasPending = false;
                LastSequence = "";
                return;
            }

            string actual = BuildSignature(me, state);
            if (actual == _pendingSignature)
            {
                Entry.Log($"[差分] ✓ 通过：实机终态与预测逐字一致 —— {actual}　（{_pendingLabel}）");
                _hasPending = false;
                // 面板上直接把结论顶掉顺序提示：玩家不用去翻日志就知道这次对不对
                LastSequence = $"✓ 已核对一致（{_pendingLabel}）　{actual}";
            }
            else if (actual != _beforeSignature && actual != _lastMismatchLog && _pendingMismatchLogs < 12)
            {
                // 还没全等：把两边打出来。**按值去重**（同一组实机值只记一次）而不是按次数限流 ——
                // 否则"出牌前"那几次刷新会把额度用光，真正有价值的"出牌后"反而看不到。
                _lastMismatchLog = actual;
                _pendingMismatchLogs++;
                Entry.Log($"[差分] 对照：预测 {_pendingSignature}；实机 {actual}　（{_pendingLabel}）");
                // 注意：**不**在这里改面板 —— 打到一半（3 张里打完第 1 张）本来就不等于终态，
                // 那不是错误。面板保持显示顺序提示，只在真正核对通过时才顶掉它。
            }
            // 其余情况保留待核对：打完牌后的下一次刷新就会命中
        }
        catch (Exception ex)
        {
            Entry.Log("[差分] 核对异常：" + ex.GetType().Name);
            _hasPending = false;
        }
    }

    /// <summary>记下一次待核对的预测（整套推荐计划的终态）。</summary>
    private static void SavePending(string label, string before, SimState sim)
    {
        _pendingLabel = label;
        _beforeSignature = before;
        _pendingSignature = BuildSignature(sim);
        _pendingRound = sim.RoundNumber;
        _pendingMismatchLogs = 0;
        _lastMismatchLog = "";
        _hasPending = true;
    }

    /// <summary>
    /// F5：让新引擎**自己搜一遍**，把它的计划和主模型并排列出来，
    /// 并把**搜索自己走到的终态**登记为待核对预测 —— 之后每次面板刷新自动比对。
    ///
    /// 与"重放主模型推荐"那条路径的区别：终态是搜索走出来的那一步本身，不是照别人的计划
    /// 复现出来的。所以计划里出现生成牌（小刀等）照样能核对 —— 影子自己生成了小刀，
    /// 不需要去活体手牌里找，旧路径那条"生成牌无法重放"的限制在这条路上不存在。
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

            SimState root = SimState.Capture(me, state, advice);
            SimSearchResult search = SimSearch.Solve(root, advice.PlayerIntangible);

            string engineOrder = Show(Order(search.Plan));
            string modelOrder = Show(Order(advice.Plan));
            bool samePlan = engineOrder == modelOrder;

            // ---- 面板：只留**一套**建议 ----
            // 两边一样就只显示一次；不一样就只显示**新引擎**那套 —— 它是这次要验的、也是登记预测的那套。
            // 主模型的计划在快照日志的「计划」行里另有记录，所以这里不留它并没有丢掉诊断线索。
            string verdict = search.PlanGaps.Count > 0
                ? $"　[计划含未镜像语义：{string.Join("、", search.PlanGaps)} → 本回合不核对]"
                : search.HandGaps.Count > 0
                    ? $"　[手牌含未镜像语义：{string.Join("、", search.HandGaps)} → 数字可能偏小]"
                    : "";

            LastSequence = $"{engineOrder}　{Score(search.Plan)}（{search.Nodes} 状态）"
                         + (samePlan ? "" : "　→ 照这个打（与主模型方案不同）")
                         + verdict;

            // ---- 登记预测：含未镜像语义、或两边都不打牌的计划不登记 ----
            // （前者比出来必然是错的，后者是空过 —— 报了只会误导）
            if (search.PlanGaps.Count > 0 || search.Plan.Actions.Count == 0)
            {
                _hasPending = false;
            }
            else
            {
                SavePending(engineOrder, BuildSignature(me, state), search.Terminal);
            }

            // 日志与面板一致：只写**新引擎**那一套，但**保留**"与主模型一致/不同"这个判据。
            // 主模型的计划在快照日志的「计划」行里另有记录（含伤害/格挡/掉血/耗能），
            // 需要两边对照时去那儿看 —— 所以这里省掉它不会丢掉线索，只是日志更干净。
            // 单行不换行：日志是一行一条记录，`grep 模拟自检` 要能一次捞到完整的那次。
            string foes = string.Join("、", search.Terminal.Foes.Where(f => f.Alive)
                .Select(f => $"{f.Index}号 {f.Hp}血/{f.Block}格挡{StatusText(f)}"));
            return $"新引擎自算：{engineOrder}　{Score(search.Plan)}（{search.Nodes} 状态）"
                 + $"　→ 打完 敌 {foes}；我 {search.Terminal.PlayerEnergy}能量/{search.Terminal.PlayerBlock}格挡{PowerText(search.Terminal)}"
                 + (samePlan ? "　（与主模型一致）" : "　⚠ 与主模型方案不同")
                 + (search.PlanGaps.Count > 0 ? "　[含未镜像语义，未登记核对]" : "")
                 + " ← 照这个顺序打，面板会自动核对终态";
        }
        catch (Exception ex)
        {
            return "模拟自检异常：" + ex.GetType().Name + " " + ex.Message;
        }
    }

    /// <summary>
    /// 把一套计划写成"牌名[目标号] → …"。**必须带目标编号** ——
    /// 不带就复现不了同一终态，多怪时"照这个顺序打"等于没给。
    /// 渲染走 DamageModel.DescribeAction（唯一实现，面板与快照日志共用同一份）。
    /// </summary>
    private static string Order(TurnPlan plan)
        => string.Join(" → ", plan.Actions.Select(a => DamageModel.DescribeAction(a, false)));

    private static string Show(string order) => order.Length == 0 ? "不打牌" : order;

    private static string Score(TurnPlan plan)
        => $"伤害 {plan.Damage:0.#} / 格挡 +{plan.Block} / 掉血 {plan.HpLoss}";

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
