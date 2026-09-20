#if !WORKSHOP
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace DamageAdvisor;

/// <summary>影子状态里的一只敌人（纯值，不持有游戏对象）。</summary>
internal sealed class SimFoe
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    public int Hp { get; set; }
    public int Block { get; set; }
    public bool Alive => Hp > 0;
}

/// <summary>
/// 第二阶段（命令镜像）的影子状态：**只装捕获期抄出来的纯值**，不持有 CardModel / Creature 等游戏对象。
/// 这样后台搜索就永远不会"读到会随实机推进而变化的 live 值"（CombatSolver 的硬约束 ③）。
/// </summary>
internal sealed class SimState
{
    public int PlayerHp { get; set; }
    public int PlayerBlock { get; set; }
    public int PlayerEnergy { get; set; }
    public int PlayerMaxEnergy { get; init; }
    /// <summary>第几回合（+ 敌人名）用来给差分验证做"身份校验"，避免跨战斗误判成通过。</summary>
    public int RoundNumber { get; init; }
    public List<SimFoe> Foes { get; init; } = new();
    /// <summary>手牌：捕获期用同一套语义（SilentLogic/Analyze）解析出来的纯值，Source 已置空。</summary>
    public List<CardEffect> Hand { get; init; } = new();
    public int DrawCount { get; init; }
    public int DiscardCount { get; init; }
    public int ExhaustCount { get; init; }
    /// <summary>RNG 状态的序列化快照（不透明令牌，后续"分支独占 RNG"要用它）。</summary>
    public string RngToken { get; init; } = "";

    /// <summary>从 live 状态捕获一份影子状态（必须在主线程调用）。</summary>
    public static SimState Capture(Player me, CombatState state, IReadOnlyList<CardEffect> handEffects)
    {
        PlayerCombatState? pcs = me.PlayerCombatState;
        var sim = new SimState
        {
            PlayerHp = me.Creature.CurrentHp,
            PlayerBlock = me.Creature.Block,
            PlayerEnergy = pcs?.Energy ?? 0,
            PlayerMaxEnergy = pcs?.MaxEnergy ?? 0,
            RoundNumber = state.RoundNumber,
            DrawCount = pcs?.DrawPile.Cards.Count ?? 0,
            DiscardCount = pcs?.DiscardPile.Cards.Count ?? 0,
            ExhaustCount = pcs?.ExhaustPile.Cards.Count ?? 0,
            RngToken = RngTokenOf(state),
        };
        int index = 0;
        foreach (Creature c in state.Enemies)
        {
            index++;
            if (!c.IsAlive)
                continue;
            sim.Foes.Add(new SimFoe { Index = index, Name = c.Name, Hp = c.CurrentHp, Block = c.Block });
        }
        // 只抄值，不留游戏对象引用（CardEffect 是 record，可以 with 出副本）
        foreach (CardEffect e in handEffects)
            sim.Hand.Add(e with { Source = null });
        return sim;
    }

    private static string RngTokenOf(CombatState state)
    {
        try
        {
            return state.RunState?.Rng?.ToSerializable()?.ToString() ?? "";
        }
        catch
        {
            return "";
        }
    }
}

/// <summary>
/// 命令镜像（第二阶段第一刀）：只做最基础的四条原版规则 —— 能量、格挡、伤害（**先扣格挡再扣血**）、
/// 打出一张牌。跨回合、怪物 AI、卡牌间的复杂交互都还没做。
/// 每条命令都把算式写进返回的说明里，便于人工核对。
/// </summary>
internal static class SimCommands
{
    /// <summary>造成伤害：先扣格挡，剩下的才进血量（与原版一致）。返回算式说明。</summary>
    public static string DealDamage(SimFoe foe, decimal amount)
    {
        int remaining = (int)amount;
        if (remaining <= 0)
            return "伤害 0，无操作";
        int absorbed = 0;
        if (foe.Block > 0)
        {
            absorbed = Math.Min(foe.Block, remaining);
            foe.Block -= absorbed;
            remaining -= absorbed;
        }
        int dealt = Math.Min(foe.Hp, Math.Max(0, remaining));
        foe.Hp -= dealt;
        return $"伤害 {amount:0.#}：格挡吸收 {absorbed}、掉血 {dealt}"
             + (foe.Hp <= 0 ? "（击杀）" : "");
    }

    /// <summary>获得格挡。</summary>
    public static string GainBlock(SimState sim, int amount)
    {
        sim.PlayerBlock += amount;
        return $"格挡 +{amount}（现在 {sim.PlayerBlock}）";
    }

    /// <summary>获得能量。</summary>
    public static string GainEnergy(SimState sim, int amount)
    {
        sim.PlayerEnergy += amount;
        return $"能量 +{amount}（现在 {sim.PlayerEnergy}）";
    }

    /// <summary>
    /// 打出一张牌：扣能量 → 移出手牌 → 结算伤害与格挡。
    /// 还**没有**做的：抽牌/弃牌/中毒/易伤等状态、能力牌、跨回合、牌之间的顺序敏感部分。
    /// </summary>
    public static string PlayCard(SimState sim, int handIndex, int targetIndex)
    {
        if (handIndex < 0 || handIndex >= sim.Hand.Count)
            return "手牌索引越界";
        CardEffect card = sim.Hand[handIndex];
        int cost = card.IsXCost ? sim.PlayerEnergy : card.Cost;
        if (cost > sim.PlayerEnergy)
            return $"能量不足（需要 {cost}，只有 {sim.PlayerEnergy}）";

        sim.PlayerEnergy -= cost;
        sim.Hand.RemoveAt(handIndex);

        var parts = new List<string> { $"打出「{card.Name}」花 {cost} 能量" };

        if (card.Damage > 0)
        {
            if (card.HitsAll)
            {
                foreach (SimFoe foe in sim.Foes.Where(f => f.Alive).ToList())
                    parts.Add($"{foe.Index}号 {DealDamage(foe, card.Damage)}");
            }
            else
            {
                SimFoe? foe = sim.Foes.FirstOrDefault(f => f.Index == targetIndex && f.Alive);
                if (foe is null)
                    parts.Add($"目标 {targetIndex} 号不在场，伤害无目标");
                else
                    parts.Add($"{foe.Index}号 {DealDamage(foe, card.Damage)}");
            }
        }

        if (card.Block > 0)
            parts.Add(GainBlock(sim, card.Block));

        return string.Join("；", parts);
    }
}
#endif
