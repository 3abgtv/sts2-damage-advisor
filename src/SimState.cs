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

    // ---- 本回合已生效的能力（捕获时读玩家身上的常驻能力，打出的能力牌再往上累加）----
    /// <summary>余像：每打出一张牌 +N 格挡（"每打出一张牌"类触发，不随爆发翻倍）。</summary>
    public int BlockPerCard { get; set; }
    /// <summary>精准：小刀 +N 伤害。</summary>
    public int ShivBonus { get; set; }
    /// <summary>涂毒：攻击造成未被格挡伤害时给目标 +N 中毒。</summary>
    public int Envenom { get; set; }
    /// <summary>腐蚀波：每抽一张牌 → 全体敌人 +N 中毒。</summary>
    public int PoisonPerDraw { get; set; }
    /// <summary>群蛇形态：每打出一张牌 → 随机一名敌人 N 伤害（模拟里近似为残血最少的）。</summary>
    public int DamagePerCardPlayed { get; set; }
    /// <summary>速行者：每抽一张牌 → 全体敌人 N 伤害。</summary>
    public int DamagePerDraw { get; set; }
    /// <summary>融入暗影：本回合获得的格挡翻倍（含余像给的格挡）。</summary>
    public bool DoubleBlock { get; set; }
    /// <summary>子弹时间：本回合手牌免费打出。</summary>
    public bool HandFree { get; set; }
    /// <summary>子弹时间：本回合不能再抽牌。</summary>
    public bool NoDraw { get; set; }
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
    /// <summary>
    /// 生成小刀用的模板（取手牌/抽牌堆里真实小刀的卡面值；场上没有小刀时为空，
    /// 此时"生成小刀"的牌会如实标注未生成，而不是瞎编伤害）。
    /// </summary>
    public CardEffect? ShivTemplate { get; set; }
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
            BlockPerCard = DamageModel.ReadPowerAmount<AfterimagePower>(self),
            ShivBonus = DamageModel.ReadPowerAmount<AccuracyPower>(self),
            Envenom = DamageModel.ReadPowerAmount<EnvenomPower>(self),
            PoisonPerDraw = DamageModel.ReadPowerAmount<CorrosiveWavePower>(self),
            DamagePerCardPlayed = DamageModel.ReadPowerAmount<SerpentFormPower>(self),
            DamagePerDraw = DamageModel.ReadPowerAmount<SpeedsterPower>(self),
            // 融入暗影若已被打出（本回合内），捕获时也要带上它的翻倍标记
            DoubleBlock = DamageModel.ReadPowerAmount<ShadowmeldPower>(self) > 0,
            // 子弹时间：游戏侧存的是 NoDrawPower（"本回合不能再抽牌"）；
            // "手牌免费"没有独立状态 —— 它体现在卡面费用本身变成 0（费用读的是 GetResolved），
            // 所以这里只要认 NoDraw 就够了，费用不用再特殊处理
            NoDraw = DamageModel.ReadPowerAmount<NoDrawPower>(self) > 0,
            HandFree = DamageModel.ReadPowerAmount<NoDrawPower>(self) > 0,
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

        // 小刀模板：优先手牌/抽牌堆里已有的小刀（卡面值含精准/力量等修正）
        foreach (CardEffect e in sim.Hand.Concat(sim.Draw))
        {
            if (SilentLogic.IsShivCard(e.ClassName))
            {
                sim.ShivTemplate = e;
                break;
            }
        }
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

    /// <summary>获得格挡（融入暗影生效时翻倍 —— 连余像给的格挡也翻，与主模型同一约定）。</summary>
    public static string GainBlock(SimState sim, int amount)
    {
        int applied = sim.DoubleBlock ? amount * 2 : amount;
        sim.PlayerBlock += applied;
        return $"格挡 +{applied}" + (sim.DoubleBlock ? "（融入暗影×2）" : "") + $"（现在 {sim.PlayerBlock}）";
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

    /// <summary>抽牌。抽牌堆不够时**洗弃牌堆**——但弃牌堆内容没抄进来，所以只能如实说明边界。
    /// 每抽到一张牌会触发速行者（全体伤害）与腐蚀波（全体中毒）。</summary>
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

            // 速行者 / 腐蚀波：每抽到一张牌就触发一次
            foreach (SimFoe foe in sim.Foes.Where(f => f.Alive).ToList())
            {
                if (sim.DamagePerDraw > 0)
                {
                    int before = foe.Hp;
                    DealDamage(foe, sim.DamagePerDraw);
                    if (foe.Hp != before)
                        parts.Add($"速行者 → {foe.Index}号 {before - foe.Hp} 伤");
                    if (sim.Envenom > 0 && foe.Hp < before)
                        ApplyPoison(foe, sim.Envenom);
                }
                if (sim.PoisonPerDraw > 0)
                    foe.Poison += sim.PoisonPerDraw;
            }
        }
        parts.Insert(0, $"抽 {drawn} 张（手牌 {sim.Hand.Count}，抽牌堆剩 {sim.Draw.Count}）");
        return string.Join("；", parts);
    }

    /// <summary>
    /// 打出一张牌：扣能量 → 移出手牌 → 结算伤害（含精准加成、本回合新上的易伤）→ 涂毒 →
    /// 卡面格挡/能量/抽牌/状态 → 能力牌自身的持续效果 → "每打出一张牌"类触发。
    /// ⚠️ 伤害用卡面预览值（它已含力量/虚弱/易伤）；"每打出一张牌"类触发不随爆发翻倍（与主模型同一约定）。
    /// </summary>
    public static string PlayCard(SimState sim, int handIndex, int targetIndex)
    {
        if (handIndex < 0 || handIndex >= sim.Hand.Count)
            return "手牌索引越界";
        CardEffect card = sim.Hand[handIndex];
        // 子弹时间：手牌免费（X 费仍按剩余能量算）。注意用**打出这张牌之前**的 HandFree ——
        // 打出子弹时间自己仍要付它的费用（与主模型一致）
        int cost = card.IsXCost ? sim.PlayerEnergy : (sim.HandFree ? 0 : card.Cost);
        if (cost > sim.PlayerEnergy)
            return $"能量不足（需要 {cost}，只有 {sim.PlayerEnergy}）";

        sim.PlayerEnergy -= cost;
        sim.Hand.RemoveAt(handIndex);

        // 融入暗影：先置位再结算，这样"打出它自己"也翻倍（与主模型一致）
        if (card.DoubleBlock)
            sim.DoubleBlock = true;
        // 子弹时间：置位后本回合剩下的牌免费、且不能再抽牌
        if (card.HandFree)
        {
            sim.HandFree = true;
            sim.NoDraw = true;
        }

        var parts = new List<string> { $"打出「{card.Name}」花 {cost} 能量" };
        List<SimFoe> targets = card.HitsAll ? AliveFoes(sim) : Targ(sim, targetIndex);

        // ① 小刀的精准加成 + X 费按投入能量放大
        decimal baseDamage = card.IsXCost ? card.Damage * cost : card.Damage;
        if (baseDamage > 0 && SilentLogic.IsShivCard(card.ClassName) && sim.ShivBonus > 0)
        {
            baseDamage += sim.ShivBonus;
            parts.Add($"精准 +{sim.ShivBonus}");
        }
        if (card.IsXCost)
            parts.Add($"X 费：投入 {cost} 点能量 → 伤害 {baseDamage:0.#}");

        // ② 伤害（先扣格挡再扣血；只对本回合新上的易伤 ×1.5）+ 涂毒
        if (baseDamage > 0)
        {
            foreach (SimFoe foe in targets)
            {
                int hpBefore = foe.Hp;
                parts.Add($"{foe.Index}号 {DealDamage(foe, baseDamage * (foe.VulnerableNew ? 1.5m : 1m))}");
                if (sim.Envenom > 0 && foe.Hp < hpBefore)
                    parts.Add($"涂毒 → {ApplyPoison(foe, sim.Envenom)}");
            }
        }

        // ③ 卡面格挡 / 能量 / 状态（抽牌与弃牌按主模型的顺序放到后面）
        if (card.Block > 0)
            parts.Add(GainBlock(sim, card.Block));
        if (card.EnergyGain > 0)
            parts.Add(GainEnergy(sim, card.EnergyGain));
        foreach (SimFoe foe in targets)
        {
            if (card.Poison > 0) parts.Add(ApplyPoison(foe, card.Poison));
            // X 费的状态（萎靡）：按投入能量给层数
            int weak = card.IsXCost && card.WeakPerX > 0 ? card.WeakPerX * cost : card.Weak;
            if (weak > 0) parts.Add(ApplyWeak(foe, weak));
            if (card.Vulnerable > 0) parts.Add(ApplyVulnerable(foe, card.Vulnerable));
        }

        // ④ 弃掉整手牌：钢铁风暴（每张换小刀）/ 计算下注（抽等量）/ 暗影步（弃整手不补充）
        if (card.DiscardHandForShivs || card.DiscardHandForDraw || card.DiscardsEntireHand)
        {
            var discardedAll = new List<CardEffect>(sim.Hand);
            sim.Hand.Clear();
            foreach (CardEffect dc in discardedAll)
                AutoPlayIfSly(sim, dc, parts);
            if (card.DiscardHandForShivs)
            {
                int made = AddShivs(sim, discardedAll.Count, parts);
                if (made == 0 && discardedAll.Count > 0)
                    parts.Add($"应换 {discardedAll.Count} 把小刀但无模板（未生成）");
            }
            else if (card.DiscardHandForDraw)
            {
                parts.Add(Draw(sim, discardedAll.Count));
            }
            parts.Add($"弃整手 {discardedAll.Count} 张");
        }

        // ⑤ 普通弃牌
        if (card.Discard > 0)
            DiscardCards(sim, card.Discard, parts);

        // ⑥ 抽牌（子弹时间之后本回合不能再抽）
        if (card.Draw > 0)
        {
            if (sim.NoDraw)
                parts.Add($"牌面要求抽 {card.Draw} 张，但本回合已不能再抽（子弹时间）");
            else
                parts.Add(Draw(sim, card.Draw));
        }

        // ⑦ 生成小刀
        if (card.Shivs > 0)
            AddShivs(sim, card.Shivs, parts);

        // ⑧ "每打出一张牌"类触发（**用打这张牌之前的层数** —— 实测：打出余像自己
        //    不触发余像，所以必须先触发再累加。见差分对照：预测 8 格挡 / 实机 7）
        if (sim.BlockPerCard > 0)
            parts.Add($"余像 → {GainBlock(sim, sim.BlockPerCard)}");
        if (sim.DamagePerCardPlayed > 0)
        {
            SimFoe? victim = sim.Foes.Where(f => f.Alive).OrderBy(f => f.Hp).FirstOrDefault();
            if (victim is not null)
                parts.Add($"群蛇形态 → {victim.Index}号 {DealDamage(victim, sim.DamagePerCardPlayed)}");
        }

        // ⑨ 能力牌自身的持续效果（放在触发之后：这张牌自己不吃自己的加成）
        var gained = new List<string>();
        if (card.BlockPerCard > 0) { sim.BlockPerCard += card.BlockPerCard; gained.Add($"余像 {card.BlockPerCard}"); }
        if (card.ShivBonus > 0) { sim.ShivBonus += card.ShivBonus; gained.Add($"精准 {card.ShivBonus}"); }
        if (card.Envenom > 0) { sim.Envenom += card.Envenom; gained.Add($"涂毒 {card.Envenom}"); }
        if (card.PoisonPerDraw > 0) { sim.PoisonPerDraw += card.PoisonPerDraw; gained.Add($"腐蚀波 {card.PoisonPerDraw}"); }
        if (card.DamagePerCardPlayed > 0) { sim.DamagePerCardPlayed += card.DamagePerCardPlayed; gained.Add($"群蛇形态 {card.DamagePerCardPlayed}"); }
        if (card.DamagePerDraw > 0) { sim.DamagePerDraw += card.DamagePerDraw; gained.Add($"速行者 {card.DamagePerDraw}"); }
        if (gained.Count > 0)
            parts.Add("获得能力：" + string.Join("、", gained));

        return string.Join("；", parts);
    }

    /// <summary>生成 N 张小刀（按模板），返回实际生成数。没有模板时返回 0（调用方负责说明）。</summary>
    private static int AddShivs(SimState sim, int count, List<string> parts)
    {
        if (sim.ShivTemplate is null || count <= 0)
            return 0;
        for (int s = 0; s < count; s++)
            sim.Hand.Add(sim.ShivTemplate with { Source = null });
        parts.Add($"生成 {count} 张小刀（手牌 {sim.Hand.Count}）");
        return count;
    }

    /// <summary>普通弃牌：挑 N 张弃掉（被弃的奇巧牌会自动打出）。</summary>
    private static void DiscardCards(SimState sim, int count, List<string> parts)
    {
        for (int d = 0; d < count && sim.Hand.Count > 0; d++)
        {
            int pick = ChooseDiscardIndex(sim.Hand);
            CardEffect discarded = sim.Hand[pick];
            sim.Hand.RemoveAt(pick);
            parts.Add($"弃「{discarded.Name}」");
            AutoPlayIfSly(sim, discarded, parts);
        }
    }

    /// <summary>
    /// 被弃触发的奇巧（Sly）牌会**自动打出**（不花能量）—— 与主模型的 ApplyDiscardTrigger 对齐：
    /// 目标取残血最少的敌人；只结算这张牌自己的效果，不触发"每打出一张牌"类能力。
    /// </summary>
    private static void AutoPlayIfSly(SimState sim, CardEffect discarded, List<string> parts)
    {
        if (!discarded.IsSly)
            return;
        SimFoe? target = sim.Foes.Where(f => f.Alive).OrderBy(f => f.Hp).FirstOrDefault();
        var sub = new List<string> { $"奇巧自动打出「{discarded.Name}」" };

        if (discarded.Damage > 0)
        {
            foreach (SimFoe foe in discarded.HitsAll ? AliveFoes(sim) : (target is null ? new List<SimFoe>() : new List<SimFoe> { target }))
            {
                int hpBefore = foe.Hp;
                sub.Add(DealDamage(foe, discarded.Damage * (foe.VulnerableNew ? 1.5m : 1m)));
                if (sim.Envenom > 0 && foe.Hp < hpBefore)
                    sub.Add(ApplyPoison(foe, sim.Envenom));
            }
        }
        if (discarded.Block > 0)
            sub.Add(GainBlock(sim, discarded.Block));
        if (discarded.EnergyGain > 0)
            sub.Add(GainEnergy(sim, discarded.EnergyGain));
        if (discarded.Draw > 0 && !sim.NoDraw)
            sub.Add(Draw(sim, discarded.Draw));
        if (discarded.Shivs > 0)
            AddShivs(sim, discarded.Shivs, sub);
        if (target is not null)
        {
            if (discarded.Poison > 0) sub.Add(ApplyPoison(target, discarded.Poison));
            if (discarded.Weak > 0) sub.Add(ApplyWeak(target, discarded.Weak));
            if (discarded.Vulnerable > 0) sub.Add(ApplyVulnerable(target, discarded.Vulnerable));
        }
        parts.Add(string.Join("、", sub));
    }

    /// <summary>
    /// 弃牌优先级 —— **与主模型 DamageModel 的 ChooseDiscard 保持同一策略**（两处公式需同步）：
    /// 先弃奇巧牌（挑自动打出价值最高的），否则弃价值最低的那张。
    /// </summary>
    private static int ChooseDiscardIndex(List<CardEffect> hand)
    {
        int bestSly = -1;
        decimal bestSlyScore = -1m;
        for (int i = 0; i < hand.Count; i++)
        {
            CardEffect c = hand[i];
            if (!c.IsSly)
                continue;
            decimal score = c.Damage + c.Block * 0.8m + c.Poison * 2m + c.Shivs * 4m
                            + c.EnergyGain * 10m + c.Draw * 3m + c.Dexterity * 2m;
            if (score > bestSlyScore)
            {
                bestSlyScore = score;
                bestSly = i;
            }
        }
        if (bestSly >= 0)
            return bestSly;

        int worst = 0;
        decimal worstScore = decimal.MaxValue;
        for (int i = 0; i < hand.Count; i++)
        {
            CardEffect c = hand[i];
            decimal score = c.Damage + c.Block + c.Poison * 2m + c.Shivs * 4m;
            if (score < worstScore)
            {
                worstScore = score;
                worst = i;
            }
        }
        return worst;
    }

    private static List<SimFoe> AliveFoes(SimState sim) => sim.Foes.Where(f => f.Alive).ToList();

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
