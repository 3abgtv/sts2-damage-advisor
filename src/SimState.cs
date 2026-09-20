#if !WORKSHOP
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace DamageAdvisor;

/// <summary>影子状态里的一只敌人（纯值，不持有游戏对象）。</summary>
internal sealed class SimFoe
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    public int Hp { get; set; }
    public int Block { get; set; }
    // ---- 状态镜像 ----
    public int Poison { get; set; }
    public int Weak { get; set; }
    public int Vulnerable { get; set; }
    public int Strength { get; set; }
    /// <summary>本回合**新上**的易伤：后续攻击 ×1.5（卡面预览里已含"场上已有的易伤"，所以只认新上的，避免重复计算）。</summary>
    public bool VulnerableNew { get; set; }

    public bool Alive => Hp > 0;
}

/// <summary>
/// 第二阶段（命令镜像）的影子状态：**只装捕获期抄出来的纯值**，不持有 CardModel / Creature 等游戏对象。
/// 这样后台搜索就永远不会"读到会随实机推进而变化的 live 值"（CombatSolver 的硬约束 ③）。
/// </summary>
internal sealed class SimState
{
    // ---- 我方 ----
    public int PlayerHp { get; set; }
    public int PlayerBlock { get; set; }
    public int PlayerEnergy { get; set; }
    public int PlayerMaxEnergy { get; init; }
    public int PlayerStrength { get; init; }
    public int PlayerDexterity { get; init; }
    /// <summary>第几回合（+ 敌人名）用来给差分验证做"身份校验"，避免跨战斗误判成通过。</summary>
    public int RoundNumber { get; init; }

    // ---- 牌堆 ----
    /// <summary>手牌：捕获期用同一套语义（SilentLogic/Analyze）解析出来的纯值，Source 已置空。</summary>
    public List<CardEffect> Hand { get; init; } = new();
    /// <summary>抽牌堆（按抽牌顺序）。</summary>
    public Queue<CardEffect> Draw { get; init; } = new();
    public int DiscardCount { get; init; }
    public int ExhaustCount { get; init; }

    public List<SimFoe> Foes { get; init; } = new();
    /// <summary>RNG 状态的序列化快照（不透明令牌，后续"分支独占 RNG"要用它）。</summary>
    public string RngToken { get; init; } = "";

    /// <summary>从 live 状态捕获一份影子状态（必须在主线程调用）。</summary>
    public static SimState Capture(Player me, CombatState state, TurnAdvice advice)
    {
        PlayerCombatState? pcs = me.PlayerCombatState;
        Creature self = me.Creature;
        var sim = new SimState
        {
            PlayerHp = self.CurrentHp,
            PlayerBlock = self.Block,
            PlayerEnergy = pcs?.Energy ?? 0,
            PlayerMaxEnergy = pcs?.MaxEnergy ?? 0,
            PlayerStrength = DamageModel.ReadPowerAmount<StrengthPower>(self),
            PlayerDexterity = DamageModel.ReadPowerAmount<DexterityPower>(self),
            RoundNumber = state.RoundNumber,
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
            sim.Foes.Add(new SimFoe
            {
                Index = index,
                Name = c.Name,
                Hp = c.CurrentHp,
                Block = c.Block,
                Poison = DamageModel.ReadPowerAmount<PoisonPower>(c),
                Weak = DamageModel.ReadPowerAmount<WeakPower>(c),
                Vulnerable = DamageModel.ReadPowerAmount<VulnerablePower>(c),
                Strength = DamageModel.ReadPowerAmount<StrengthPower>(c),
            });
        }

        // 只抄值，不留游戏对象引用（CardEffect 是 record，可以 with 出副本）
        foreach (CardEffect e in advice.HandEffects)
            sim.Hand.Add(e with { Source = null });
        foreach (CardEffect e in advice.DrawEffects)
            sim.Draw.Enqueue(e with { Source = null });
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
/// 命令镜像（第二阶段）：原版规则里最基础的那几条 —— 能量、格挡、伤害（**先扣格挡再扣血**）、
/// 上状态（中毒/虚弱/易伤）、抽牌（含"抽牌堆空则洗弃牌堆"）、打出一张牌、结束回合（中毒结算）。
/// 还没做：怪物 AI、卡牌间的复杂交互、跨回合的持续性效果。
/// 每条命令都把算式写进返回的说明里，便于人工核对。
/// </summary>
internal static class SimCommands
{
    /// <summary>造成伤害：先扣格挡，剩下的才进血量（与原版一致）。</summary>
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
        return $"伤害 {amount:0.#}：格挡吸收 {absorbed}、掉血 {dealt}" + (foe.Hp <= 0 ? "（击杀）" : "");
    }

    public static string GainBlock(SimState sim, int amount)
    {
        sim.PlayerBlock += amount;
        return $"格挡 +{amount}（现在 {sim.PlayerBlock}）";
    }

    public static string GainEnergy(SimState sim, int amount)
    {
        sim.PlayerEnergy += amount;
        return $"能量 +{amount}（现在 {sim.PlayerEnergy}）";
    }

    /// <summary>上中毒（层数叠加）。</summary>
    public static string ApplyPoison(SimFoe foe, int amount)
    {
        foe.Poison += amount;
        return $"{foe.Index}号中毒 +{amount}（现在 {foe.Poison}）";
    }

    public static string ApplyWeak(SimFoe foe, int amount)
    {
        foe.Weak += amount;
        return $"{foe.Index}号虚弱 +{amount}（现在 {foe.Weak}）";
    }

    /// <summary>上易伤（本回合新上的记进 VulnerableNew，后续攻击 ×1.5）。</summary>
    public static string ApplyVulnerable(SimFoe foe, int amount)
    {
        foe.Vulnerable += amount;
        foe.VulnerableNew = true;
        return $"{foe.Index}号易伤 +{amount}（现在 {foe.Vulnerable}）";
    }

    /// <summary>抽牌。抽牌堆不够时**洗弃牌堆**——但弃牌堆内容没抄进来，所以只能如实说明边界。</summary>
    public static string Draw(SimState sim, int count)
    {
        var parts = new List<string>();
        int drawn = 0;
        for (int i = 0; i < count; i++)
        {
            if (sim.Draw.Count == 0)
            {
                parts.Add($"抽牌堆已空，还差 {count - drawn} 张（洗弃牌堆：内容未镜像，本轮不模拟）");
                break;
            }
            sim.Hand.Add(sim.Draw.Dequeue());
            drawn++;
        }
        parts.Insert(0, $"抽 {drawn} 张（手牌 {sim.Hand.Count}，抽牌堆剩 {sim.Draw.Count}）");
        return string.Join("；", parts);
    }

    /// <summary>
    /// 打出一张牌：扣能量 → 移出手牌 → 结算伤害 / 格挡 / 状态。
    /// ⚠️ 伤害直接用卡面预览值（它已含力量/虚弱/易伤），只对本回合**新上的**易伤再 ×1.5。
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
            foreach (SimFoe foe in card.HitsAll ? sim.Foes.Where(f => f.Alive).ToList() : Targ(sim, targetIndex))
            {
                decimal dmg = card.Damage * (foe.VulnerableNew ? 1.5m : 1m);
                parts.Add($"{foe.Index}号 {DealDamage(foe, dmg)}");
            }
        }

        if (card.Block > 0)
            parts.Add(GainBlock(sim, card.Block));
        if (card.EnergyGain > 0)
            parts.Add(GainEnergy(sim, card.EnergyGain));
        if (card.Draw > 0)
            parts.Add(Draw(sim, card.Draw));

        foreach (SimFoe foe in card.HitsAll ? sim.Foes.Where(f => f.Alive).ToList() : Targ(sim, targetIndex))
        {
            if (card.Poison > 0) parts.Add(ApplyPoison(foe, card.Poison));
            if (card.Weak > 0) parts.Add(ApplyWeak(foe, card.Weak));
            if (card.Vulnerable > 0) parts.Add(ApplyVulnerable(foe, card.Vulnerable));
        }

        return string.Join("；", parts);
    }

    private static List<SimFoe> Targ(SimState sim, int targetIndex)
    {
        SimFoe? foe = sim.Foes.FirstOrDefault(f => f.Index == targetIndex && f.Alive);
        return foe is null ? new List<SimFoe>() : new List<SimFoe> { foe };
    }

    /// <summary>
    /// 结束回合（简化）：敌人身上的中毒结算（**无视格挡**、每次触发后层数 -1）→ 格挡清零 →
    /// 能量重置 → 下回合抽 5 张。
    /// ⚠️ 还没做：怪物自己的行动（它这一轮打多少伤害、上什么 debuff）。
    /// </summary>
    public static string EndTurn(SimState sim)
    {
        var parts = new List<string>();
        foreach (SimFoe foe in sim.Foes.Where(f => f.Alive).ToList())
        {
            if (foe.Poison <= 0)
                continue;
            int before = foe.Hp;
            foe.Hp = Math.Max(0, foe.Hp - foe.Poison);
            parts.Add($"{foe.Index}号中毒结算 {foe.Poison} 点（{before}→{foe.Hp}）");
            foe.Poison = Math.Max(0, foe.Poison - 1);
        }
        int clearedBlock = sim.PlayerBlock;
        sim.PlayerBlock = 0;
        sim.PlayerEnergy = sim.PlayerMaxEnergy;
        parts.Add($"我的格挡清零（{clearedBlock}）、能量重置为 {sim.PlayerEnergy}");
        parts.Add(Draw(sim, 5));
        parts.Add("（怪物行动未镜像）");
        return string.Join("；", parts);
    }
}
#endif
