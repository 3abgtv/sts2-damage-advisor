#if !WORKSHOP
using System.Diagnostics;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

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
                RelicModel clone = live.ToMutable();
                bool sharesVars = ReferenceEquals(clone.DynamicVars, live.DynamicVars);
                parts.Add($"遗物 {live.Title}：ToMutable 成功（计数 {live.DisplayAmount}），共享 DynamicVars={sharesVars}"
                          + (sharesVars ? " ← 浅拷贝，改克隆体会污染真机（后续要自己深克隆）" : " ← 深拷贝，可安全改"));
            }
            catch (Exception ex)
            {
                parts.Add("遗物克隆失败：" + ex.GetType().Name + " " + ex.Message);
            }
        }

        return string.Join(" | ", parts);
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
