using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;

namespace DamageAdvisor;

/// <summary>伤害/格挡随"当前搜索状态"变化的牌（必须出牌时才算，不能提前算死）。</summary>
internal enum ScalingKind
{
    None,
    /// <summary>终结技：本回合每打出过一张攻击牌，就造成一次伤害（含自身）。</summary>
    AttacksPlayed,
    /// <summary>飞镖：手牌中每有一张技能牌，就造成一次伤害。</summary>
    SkillsInHand,
    /// <summary>蜃景：格挡 = 所有敌人中毒层数总和。</summary>
    EnemyPoisonTotal,
}

/// <summary>一张牌在本回合的可量化效果。</summary>
/// <remarks>
/// 是 record 而不是 class：手上技法要"给手牌里某一张技能牌加奇巧"，
/// 得复制一份带标记的实例（`effect with { IsSly = true }`），不能就地改（手牌对象在各搜索状态间共享）。
/// </remarks>
internal sealed record CardEffect
{
    public required string Name { get; init; }
    public string Id { get; init; } = "";
    /// <summary>模型类名（语义分发用：BladeDance / Acrobatics / Shiv …）。</summary>
    public string ClassName { get; init; } = "";
    public required int Cost { get; init; }
    public decimal Damage { get; init; }
    public bool HitsAll { get; init; }
    public int Hits { get; init; } = 1;
    public int Block { get; init; }
    public int Poison { get; init; }
    public int Weak { get; init; }
    public int Vulnerable { get; init; }
    public int StrengthLoss { get; init; }
    public int Draw { get; init; }
    public int Shivs { get; init; }
    public int EnergyGain { get; init; }
    public int Dexterity { get; init; }
    public int Discard { get; init; }
    // ---- v0.6 新增：本回合可建模的猎人机制 ----
    public bool IsXCost { get; init; }
    public decimal DamagePerX { get; init; }
    public int WeakPerX { get; init; }
    public int StrengthLossPerX { get; init; }
    public bool HandFree { get; init; }
    public bool NoDraw { get; init; }
    public bool DiscardHandForShivs { get; init; }
    public bool DiscardHandForDraw { get; init; }
    /// <summary>弃掉整手牌且不补充（暗影步）。</summary>
    public bool DiscardsEntireHand { get; init; }
    public int ShivBonus { get; init; }
    public int BlockPerCard { get; init; }
    /// <summary>涂毒：每点未被格挡的攻击伤害附加的中毒层数。</summary>
    public int Envenom { get; init; }
    /// <summary>腐蚀波：本回合每抽一张牌给全体敌人的中毒层数。</summary>
    public int PoisonPerDraw { get; init; }
    /// <summary>群蛇形态：每打出一张牌对随机敌人造成的伤害。</summary>
    public int DamagePerCardPlayed { get; init; }
    /// <summary>速行者：本回合每抽一张牌对全体敌人造成的伤害。</summary>
    public int DamagePerDraw { get; init; }
    /// <summary>融入暗影：本回合获得的格挡翻倍。</summary>
    public bool DoubleBlock { get; init; }
    /// <summary>幻影之刃：本回合第一张小刀额外伤害。</summary>
    public int FirstShivBonus { get; init; }

    /// <summary>铭记死亡：本回合每弃一张牌额外增加的伤害。</summary>

    public int DamagePerDiscard { get; init; }

    /// <summary>毒性爆发：打出后立即触发一次中毒伤害。</summary>

    public bool TriggersPoisonNow { get; init; }


    /// <summary>奇巧（Sly）：被丢弃时会自动打出该牌（不花能量）。</summary>


    public bool IsSly { get; init; }



    public bool IsAttack { get; init; }



    public bool IsSkill { get; init; }



    public ScalingKind Scaling { get; init; }

    /// <summary>
    /// Scaling=AttacksPlayed 时，"游戏预览里已经算进去的次数"（含这张牌自己）。
    /// 终结技的卡面会显示"（攻击 N 次）"，N 就是它，所以本回合之前已经出过的攻击牌不会被漏掉。
    /// </summary>
    public int ScalingBaseHits { get; init; } = 1;

    /// <summary>打出时清除目标身上全部格挡（暴露）。</summary>
    public bool RemovesBlock { get; init; }

    /// <summary>打出后本回合所有小刀改为攻击全体（刀扇）。</summary>
    public bool MakesShivsHitAll { get; init; }

    // ---- 静默低估补齐：这些牌以前"看着有支持、实际没算" ----
    /// <summary>跟踪：本回合对虚弱状态的敌人 +50% 攻击伤害。</summary>
    public bool GrantsTracking { get; init; }
    /// <summary>获得 N 点无实体：每段来袭伤害只造成 1 点（幽魂形态）。</summary>
    public int GrantsIntangible { get; init; }
    /// <summary>紧勒：本回合之后每打出一张牌，该敌人失去 N 点生命（无视格挡）。</summary>
    public int Strangle { get; init; }
    /// <summary>猛扑：下一张技能牌耗能变为 0。</summary>
    public bool MakesNextSkillFree { get; init; }
    /// <summary>咕嘟冒泡：目标身上没有中毒时不给毒。</summary>
    public bool NeedsPoisonedTarget { get; init; }
    /// <summary>回响斩击：每有一名敌人被击杀就重复一次全体伤害。</summary>
    public bool RepeatsOnKill { get; init; }
    /// <summary>刀刃陷阱：把消耗牌堆里的 N 张小刀对同一目标打出（N 来自卡面 CalculatedShivs）。</summary>
    public int ExhaustShivs { get; init; }
    /// <summary>手上技法：给手牌中的一张技能牌添加奇巧。</summary>
    public bool GrantsSlyToSkill { get; init; }
    /// <summary>触媒：中毒额外触发 N 次（影响"中毒先手击杀"判定）。</summary>
    public int GrantsAccelerant { get; init; }
    /// <summary>爆发：本回合接下来 N 张技能牌额外打出一次。</summary>
    public int DoublesNextSkills { get; init; }



    /// <summary>词条显示文本（消耗/保留/固有/奇巧/虚无/不可打出）。</summary>



    public string KeywordsText { get; init; } = "";
    public bool Supported { get; init; } = true;
    public string Note { get; init; } = "";
    public CardModel? Source { get; init; }
}

/// <summary>战斗中一只怪的简化状态（带屏幕编号）。</summary>
internal sealed class SimEnemy
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    public required int Hp { get; set; }
    public required int MaxHp { get; init; }

    /// <summary>敌人当前格挡（先扣格挡再扣血）。</summary>

    public int Block { get; set; }


    /// <summary>身上关键 debuff 的显示文本（含队友施加的）。</summary>


    public string PowersText { get; set; } = "";
    /// <summary>意图原始总伤害。</summary>
    public required int BaseIncoming { get; init; }
    /// <summary>意图攻击段数（用于力量削减）。</summary>
    public required int Hits { get; init; }
    public int Weak { get; set; }

    /// <summary>
    /// 本回合才被我们上的虚弱。⚠️ 意图伤害（GetSingleDamage）本身已经算进了敌人"当前"的虚弱，
    /// 所以只有"原本不虚弱、本回合被我们打上虚弱"时才在这里再乘一次，否则会重复减伤。
    /// </summary>
    public int WeakThisTurn { get; set; }
    public int Vulnerable { get; set; }
    public int StrengthLoss { get; set; }
    public int Poison { get; set; }
    /// <summary>本回合才被我们上的易伤（用于后续伤害 ×1.5）。</summary>
    public int VulnerableThisTurn { get; set; }

    public bool Alive => Hp > 0;
    public bool DiesToPoison => DiesToPoisonWith(0);

    /// <summary>
    /// 中毒先手击杀（算上触媒）：中毒触发 1+N 次，每次结算后层数 -1，
    /// 所以总量 = stack + (stack-1) + … 。⚠️ "每次触发后减 1"是假设，待实机验证
    /// （验证：9 层毒打 10 血怪，有触媒时应正好毒死）。
    /// </summary>
    public bool DiesToPoisonWith(int accelerant)
    {
        if (!Alive || Poison <= 0)
            return false;
        int ticks = 1 + Math.Max(0, accelerant);
        int total = 0;
        int stack = Poison;
        for (int i = 0; i < ticks && stack > 0; i++)
        {
            total += stack;
            stack--;
        }
        return total >= Hp;
    }

    public int Incoming
    {
        get
        {
            int damage = Math.Max(0, BaseIncoming - StrengthLoss * Math.Max(1, Hits));
            if (WeakThisTurn > 0)
                damage = damage * 3 / 4;
            return damage;
        }
    }

    /// <summary>
    /// 算上"无实体"的来袭伤害：无实体时每段攻击只造成 1 点（不看力量/虚弱等修正）。
    /// 只有本来就有攻击意图（BaseIncoming &gt; 0）的敌人才按段数计 1，纯 buff/debuff 的敌人仍是 0。
    /// </summary>
    public int IncomingWith(int playerIntangible)
        => playerIntangible > 0 ? (BaseIncoming > 0 ? Math.Max(1, Hits) : 0) : Incoming;

    public SimEnemy Clone() => new()
    {
        Index = Index,
        Name = Name,
        Hp = Hp,
        MaxHp = MaxHp,

        Block = Block,

        BaseIncoming = BaseIncoming,
        Hits = Hits,
        Weak = Weak,
        WeakThisTurn = WeakThisTurn,
        Vulnerable = Vulnerable,
        StrengthLoss = StrengthLoss,
        Poison = Poison,

        PowersText = PowersText,
        VulnerableThisTurn = VulnerableThisTurn,
    };
}

internal sealed class PlannedAction
{
    public required CardEffect Card { get; init; }
    public required int TargetIndex { get; init; }
}

internal sealed class TurnPlan
{
    public List<PlannedAction> Actions { get; init; } = new();
    public decimal Damage { get; set; }
    public int Block { get; set; }


    public int EnergySpent { get; set; }
    public int EnergyGained { get; set; }
    public int HpLoss { get; set; }
    public bool Lethal { get; set; }

    /// <summary>本回合能打死的怪数量（含中毒先手击杀）。</summary>

    public int Kills { get; set; }
    public List<SimEnemy> EnemiesAfter { get; set; } = new();
}

internal sealed class TurnAdvice
{
    public required TurnPlan Plan { get; init; }
    public required IReadOnlyList<SimEnemy> EnemiesBefore { get; init; }
    public required int IncomingDamage { get; init; }
    public required int CurrentBlock { get; init; }
    public required int CurrentHp { get; init; }
    public required int NodesExplored { get; init; }
    public required IReadOnlyList<CardEffect> HandEffects { get; init; }
    /// <summary>解算时玩家身上已有的无实体层数（面板显示"来袭"时要按它折算）。</summary>
    public required int PlayerIntangible { get; init; }
    /// <summary>解算时玩家身上的触媒层数（面板判断"中毒先手击杀"时要按它折算）。</summary>
    public required int PlayerAccelerant { get; init; }
}

/// <summary>
/// v0.5 模型：猎人机制 + 本回合小状态机搜索。
/// 目标：先保命（不死/少掉血），再在保命前提下打最大伤害。
/// </summary>
internal static class DamageModel
{
    // 记忆化后 Nodes 计的是"唯一状态数"（重复状态提前返回），所以上限可以低很多：
// 既覆盖足够的搜索空间，又避免主线程卡帧。
    private const int MaxNodes = 25000;

    public static TurnAdvice Solve(
        IReadOnlyList<CardModel> hand,
        IReadOnlyList<CardModel> drawPile,
        int energy,
        IReadOnlyList<Creature> enemiesInOrder,
        IReadOnlyList<Creature> allies,
        Creature me,
        int currentHp,
        int currentBlock)
    {
        var enemies = new List<SimEnemy>();
        for (int i = 0; i < enemiesInOrder.Count; i++)
        {
            Creature c = enemiesInOrder[i];
            if (!c.IsAlive)
                continue;
            (int total, int hits) = IncomingOf(c, me);
            enemies.Add(new SimEnemy
            {
                Index = i + 1,
                Name = c.Name,
                Hp = c.CurrentHp,
                MaxHp = c.MaxHp,

                Block = c.Block,
                BaseIncoming = total,

                Hits = hits,

                // 中毒从活体状态起算：队友/上回合留下的毒也会在敌方行动前结算


                Poison = ReadPower<PoisonPower>(c),


                // 虚弱只用于显示/去重：意图伤害里已经算过"当前虚弱"，本回合新上的虚弱走 WeakThisTurn


                Weak = ReadPower<WeakPower>(c),


                PowersText = DescribePowers(c),
            });
        }

        Creature? sample = enemiesInOrder.FirstOrDefault(e => e.IsAlive);
        int strength = ReadPower<StrengthPower>(me);
        int accuracy = ReadPower<AccuracyPower>(me);
        // 无实体是"整场战斗有效"的能力：面板每次解算都要把玩家身上已有的读进来
        int playerIntangible = ReadPower<IntangiblePower>(me);
        // 触媒同理（影响"中毒先手击杀"判定）
        int playerAccelerant = ReadPower<AccelerantPower>(me);

        // 小刀伤害：优先取"场上真实小刀的游戏预览值"（含力量/精准/虚弱/缩小等修正），
        // 都没有小刀时退回手算。必须在 Analyze 之前算出来——刀刃陷阱的伤害 = 张数 × 小刀伤害。
        decimal shivDamage = FindShivDamage(hand, drawPile, sample)
            ?? BuildShivDamage(sample, strength, accuracy, ReadPower<WeakPower>(me) > 0);

        var analyzeContext = new AnalyzeContext
        {
            Target = sample,
            Enemies = enemies,
            DrawPileCount = drawPile.Count,
            ShivDamage = shivDamage,
        };

        List<CardEffect> handEffects = hand.Select(c => Analyze(c, analyzeContext)).ToList();
        List<CardEffect> drawEffects = drawPile.Select(c => Analyze(c, analyzeContext)).ToList();

        var context = new SearchContext
        {
            Shiv = BuildShivEffect(shivDamage, false),
            ShivAll = BuildShivEffect(shivDamage, true),
            CurrentBlock = currentBlock,
            CurrentHp = currentHp,
            EnemiesBefore = enemies,
        };

        context.Dfs(new SearchState
        {
            Energy = energy,
            Hand = new List<CardEffect>(handEffects),
            Draw = new Queue<CardEffect>(drawEffects),
            Enemies = enemies.Select(e => e.Clone()).ToList(),
            Intangible = playerIntangible,
            Accelerant = playerAccelerant,
        }, 0);

        return new TurnAdvice
        {
            Plan = context.Best,
            EnemiesBefore = enemies,
            IncomingDamage = enemies.Sum(e => e.IncomingWith(playerIntangible)),
            CurrentBlock = currentBlock,
            CurrentHp = currentHp,
            NodesExplored = context.Nodes,
            HandEffects = handEffects,
            PlayerIntangible = playerIntangible,
            PlayerAccelerant = playerAccelerant,
        };
    }

    /// <summary>手牌/抽牌堆里第一张真实小刀的伤害（游戏预览值）；都没有小刀时返回 null。</summary>
    private static decimal? FindShivDamage(IReadOnlyList<CardModel> hand, IReadOnlyList<CardModel> drawPile, Creature? target)
    {
        if (target is null)
            return null;
        foreach (CardModel card in hand.Concat(drawPile))
        {
            if (!SilentLogic.IsShivCard(card.GetType().Name))
                continue;
            decimal damage = EstimateDamage(card, target);
            if (damage > 0m)
                return damage;
        }
        return null;
    }

    private sealed class AnalyzeContext
    {
        public Creature? Target { get; init; }
        public required IReadOnlyList<SimEnemy> Enemies { get; init; }
        public required int DrawPileCount { get; init; }
        /// <summary>小刀伤害（刀刃陷阱要按张数乘以它）。</summary>
        public required decimal ShivDamage { get; init; }
    }

    private sealed class SearchState
    {
        public int Energy { get; set; }
        public int Dex { get; set; }
        public int AttacksPlayed { get; set; }

        public int DiscardedThisTurn { get; set; }
        public bool HandFree { get; set; }
        public bool NoDraw { get; set; }
        public int ShivBonus { get; set; }
        public int BlockPerCard { get; set; }
        public int Envenom { get; set; }
        public int PoisonPerDraw { get; set; }
        public int DamagePerCardPlayed { get; set; }
        public int DamagePerDraw { get; set; }
        public bool DoubleBlock { get; set; }
        public int FirstShivBonus { get; set; }
        public bool FirstShivBonusUsed { get; set; }
        /// <summary>本回合打出过刀扇后，小刀改为攻击全体。</summary>
        public bool ShivsHitAll { get; set; }
        /// <summary>
        /// 跟踪：只在"本回合计划中新打出跟踪"之后加 ×1.5。
        /// ⚠️ 不能按"玩家身上有无跟踪"来加——卡面预览值本身已含力量/虚弱/易伤这类全局修正，
        /// 本场早就有的跟踪大概率也已经算进预览里了，再加一次就是重复计算。
        /// （与 VulnerableThisTurn 同一个思路；待实机验证）
        /// </summary>
        public bool TrackingNew { get; set; }
        /// <summary>无实体层数：&gt;0 时每段来袭伤害只造成 1 点。</summary>
        public int Intangible { get; set; }
        /// <summary>紧勒：本回合之后每张牌让该敌人失去的生命值。</summary>
        public int StrangleAmount { get; set; }
        /// <summary>紧勒的目标编号（0 = 未指定）。</summary>
        public int StrangleTarget { get; set; }
        /// <summary>猛扑：下一张技能牌免费。</summary>
        public bool NextSkillFree { get; set; }
        /// <summary>触媒：中毒额外触发次数（影响中毒先手击杀判定）。</summary>
        public int Accelerant { get; set; }
        /// <summary>爆发：接下来还有几张技能牌会被额外打出一次。</summary>
        public int DoubleSkillCount { get; set; }
        public List<CardEffect> Hand { get; init; } = new();
        public Queue<CardEffect> Draw { get; init; } = new();
        public List<SimEnemy> Enemies { get; init; } = new();
        public List<PlannedAction> Actions { get; init; } = new();
        public decimal Damage { get; set; }
        public int Block { get; set; }


        public int EnergySpent { get; set; }
        public int EnergyGained { get; set; }
    }

    private sealed class SearchContext
    {
        public required CardEffect Shiv { get; init; }
        /// <summary>刀扇打出后用的小刀模板（攻击全体）。</summary>
        public required CardEffect ShivAll { get; init; }
        public required int CurrentBlock { get; init; }
        public required int CurrentHp { get; init; }
        public required List<SimEnemy> EnemiesBefore { get; init; }

        public TurnPlan Best { get; private set; } = new();
        public int Nodes { get; private set; }
        private bool _hasBest;

        /// <summary>已探索过的等价状态（同一手牌/能量/敌人状态不重复展开，消除出牌顺序带来的排列爆炸）。</summary>

        private readonly HashSet<string> _visited = new();

        public void Dfs(SearchState state, int depth)
        {
            string key = BuildStateKey(state);
            if (!_visited.Add(key))
                return;

            Nodes++;
            Consider(state);

            if (Nodes > MaxNodes || depth > 14)
                return;

            for (int i = 0; i < state.Hand.Count; i++)
            {
                CardEffect card = state.Hand[i];
                if (!card.Supported)
                    continue;

                // 同名牌只尝试一次：同样的小刀/打击互相替换不会产生更优解，
                // 但会带来 N! 级别的重复分支（5 张小刀 = 120 条等价路径）
                bool duplicate = false;
                for (int k = 0; k < i; k++)
                {
                    CardEffect other = state.Hand[k];
                    if (other.Id == card.Id && other.Cost == card.Cost && other.Damage == card.Damage
                        && other.Block == card.Block && other.Poison == card.Poison
                        && other.Shivs == card.Shivs && other.Vulnerable == card.Vulnerable
                        && other.Weak == card.Weak)
                    {
                        duplicate = true;
                        break;
                    }
                }
                if (duplicate)
                    continue;

                // 猛扑：下一张技能牌免费（与子弹时间的"手牌免费"一样都是 0 费）
                int effectiveCost = card.IsXCost
                    ? state.Energy
                    : (state.HandFree || (state.NextSkillFree && card.IsSkill) ? 0 : card.Cost);
                if (effectiveCost > state.Energy)
                    continue;

                bool needsTarget = !card.HitsAll && (
                    card.Damage > 0 || card.Poison > 0 || card.Weak > 0 || card.Vulnerable > 0
                    || card.StrengthLoss > 0
                    || (card.IsXCost && (card.WeakPerX > 0 || card.StrengthLossPerX > 0)));

                if (needsTarget)
                {
                    foreach (SimEnemy target in state.Enemies.Where(e => e.Alive).ToList())
                        PlayCard(state, i, card, target.Index, depth, effectiveCost);
                }
                else
                {
                    PlayCard(state, i, card, 0, depth, effectiveCost);
                }
            }
        }

        private void PlayCard(SearchState state, int handIndex, CardEffect card, int targetIndex, int depth, int effectiveCost)
        {
            // 爆发：这张牌如果是"本回合额外打出一次"的技能牌，卡牌自身的载荷整体翻倍。
            // ⚠️ 只翻倍"这张牌自己的效果"，不重复"每打出一张牌"类触发（余像/群蛇形态/紧勒）——
            //    后者的语义是"打出行为"而非"结算内容"，这条是假设，已记在 STATUS 待实机确认。
            bool doubled = card.IsSkill && state.DoubleSkillCount > 0;
            int times = doubled ? 2 : 1;

            var next = new SearchState
            {
                Energy = state.Energy - effectiveCost + card.EnergyGain * times,
                Dex = state.Dex + card.Dexterity * times,
                DiscardedThisTurn = state.DiscardedThisTurn,
                AttacksPlayed = state.AttacksPlayed + (card.IsAttack ? 1 : 0),
                HandFree = state.HandFree || card.HandFree,
                NoDraw = state.NoDraw || card.NoDraw,
                ShivBonus = state.ShivBonus + card.ShivBonus * times,
                BlockPerCard = state.BlockPerCard + card.BlockPerCard * times,
                Envenom = state.Envenom + card.Envenom * times,
                PoisonPerDraw = state.PoisonPerDraw + card.PoisonPerDraw * times,
                DamagePerCardPlayed = state.DamagePerCardPlayed + card.DamagePerCardPlayed * times,
                DamagePerDraw = state.DamagePerDraw + card.DamagePerDraw * times,
                DoubleBlock = state.DoubleBlock || card.DoubleBlock,
                FirstShivBonus = state.FirstShivBonus + card.FirstShivBonus * times,
                FirstShivBonusUsed = state.FirstShivBonusUsed,
                ShivsHitAll = state.ShivsHitAll || card.MakesShivsHitAll,
                TrackingNew = state.TrackingNew || card.GrantsTracking,
                Intangible = state.Intangible + card.GrantsIntangible * times,
                Accelerant = state.Accelerant + card.GrantsAccelerant * times,
                // 紧勒：以最后打出的一张为准（同一回合叠加两次没有意义，取大的那个更安全）
                StrangleAmount = card.Strangle * times > state.StrangleAmount ? card.Strangle * times : state.StrangleAmount,
                StrangleTarget = card.Strangle > 0 ? targetIndex : state.StrangleTarget,
                // 猛扑：打出后置位；打出一张技能牌就消耗掉；其它牌不影响
                NextSkillFree = card.MakesNextSkillFree || (state.NextSkillFree && !card.IsSkill),
                // 爆发：打出就置位（打出 N 张）；打出一张技能牌消耗一层；其它牌不影响
                DoubleSkillCount = card.DoublesNextSkills > 0
                    ? card.DoublesNextSkills
                    : (card.IsSkill ? Math.Max(0, state.DoubleSkillCount - 1) : state.DoubleSkillCount),
                Hand = new List<CardEffect>(state.Hand),
                Draw = new Queue<CardEffect>(state.Draw),
                Enemies = state.Enemies.Select(e => e.Clone()).ToList(),
                Actions = new List<PlannedAction>(state.Actions),
                Damage = state.Damage,
                // 格挡 = 卡面格挡(+敏捷) + 余像"每张牌 N 格挡"，本回合这些都属于"获得的格挡"，融入暗影时一起翻倍。
                // ⚠️ 余像那一项以前被写成 `card.Block > 0 ? 0 : ...`，导致出格挡牌时不加，这里必须无条件计入；
                //    余像是"每打出一张牌"类触发，所以不随爆发翻倍。
                Block = state.Block
                            + ((BlockGain(card, state) + (card.Block > 0 ? state.Dex : 0)) * times
                               + state.BlockPerCard)
                              * (state.DoubleBlock || card.DoubleBlock ? 2 : 1),
                EnergySpent = state.EnergySpent + effectiveCost,
                EnergyGained = state.EnergyGained + card.EnergyGain * times,
            };

            CardEffect played = next.Hand[handIndex];
            next.Hand.RemoveAt(handIndex);
            next.Actions.Add(new PlannedAction { Card = played, TargetIndex = targetIndex });

            // 这张牌造成的伤害（X 费按已投入能量放大；小刀补上本回合的精准加成）
            decimal damage = played.IsXCost ? played.Damage * effectiveCost : played.Damage;

            switch (played.Scaling)


            {


                case ScalingKind.AttacksPlayed:


                    // 终结技：含自身在内，本回合已打出的攻击牌数。
                    // 基数取游戏卡面上"（攻击 N 次）"的 N，这样本回合之前已经出过的攻击牌不会被漏掉。


                    damage = played.Damage * (played.ScalingBaseHits + state.AttacksPlayed);


                    break;


                case ScalingKind.SkillsInHand:


                    // 飞镖：按"此刻手牌里的技能牌数"（打出技能会减少计数，所以它会被排到前面）


                    damage = played.Damage * Math.Max(1, next.Hand.Count(c => c.IsSkill));


                    break;


            }



            if (played.DamagePerDiscard > 0 && next.DiscardedThisTurn > 0)

                damage += played.DamagePerDiscard * next.DiscardedThisTurn;
            if (SilentLogic.IsShivCard(played.ClassName))
            {
                damage += next.ShivBonus;
                if (!next.FirstShivBonusUsed && next.FirstShivBonus > 0)
                {
                    damage += next.FirstShivBonus;
                    next.FirstShivBonusUsed = true;
                }
            }

            // 刀刃陷阱：把消耗牌堆里的 N 张小刀对同一目标打出（张数取卡面 CalculatedShivs）
            if (played.ExhaustShivs > 0)
                damage = played.ExhaustShivs * Shiv.Damage;

            // 爆发：技能牌额外打出一次 → 这张牌自己的载荷整体翻倍
            damage *= times;

            // 手上技法：给手牌里"弃掉最划算"的那张技能牌加奇巧（爆发翻倍时加两张）
            for (int t = 0; t < times && played.GrantsSlyToSkill; t++)
                MarkBestSkillSly(next);

            // 紧勒：本回合之后每打出一张牌，该名敌人都会失去 N 点生命（无视格挡）
            if (state.StrangleAmount > 0)
            {
                SimEnemy? strangled = next.Enemies.FirstOrDefault(e => e.Index == state.StrangleTarget && e.Alive);
                if (strangled is not null)
                    ApplyDamageIgnoringBlock(next, strangled, state.StrangleAmount);
            }

            // 暴露这类"先清格挡再结算"的牌：清完格挡，后面的伤害/易伤才不会被格挡吃掉
            if (played.RemovesBlock)
            {
                foreach (SimEnemy victim in next.Enemies.Where(e => e.Alive && (played.HitsAll || e.Index == targetIndex)))
                    victim.Block = 0;
            }

            // 刀扇：本回合剩下的（手牌 + 抽牌堆里的）小刀都改打全体
            if (played.MakesShivsHitAll)
            {
                for (int i = 0; i < next.Hand.Count; i++)
                    next.Hand[i] = NormalizeShiv(next, next.Hand[i]);
            }

            if (damage > 0)
            {
                if (played.HitsAll)
                {
                    int aliveBefore = next.Enemies.Count(e => e.Alive);
                    ApplyDamageToAll(next, damage, played.IsAttack);
                    // 回响斩击：每有一名敌人被击杀，就重复一次全体伤害
                    if (played.RepeatsOnKill)
                    {
                        for (int round = 0; round < 8; round++)
                        {
                            int aliveNow = next.Enemies.Count(e => e.Alive);
                            if (aliveNow >= aliveBefore || aliveNow == 0)
                                break;
                            aliveBefore = aliveNow;
                            ApplyDamageToAll(next, damage, played.IsAttack);
                        }
                    }
                }
                else
                {
                    SimEnemy? target = next.Enemies.FirstOrDefault(e => e.Index == targetIndex && e.Alive);
                    if (target is not null)
                    {
                        int before = target.Hp;
                        ApplyDamage(next, target, damage * DamageMultiplier(next, target, played.IsAttack));
                        if (target.Hp < before)
                            ApplyEnvenom(next, target);
                    }
                }
            }

            // 中毒 / 虚弱 / 易伤
            // 全体目标（AllEnemies）作用于所有存活敌人；单体目标按编号选
            IReadOnlyList<SimEnemy> debuffTargets = played.HitsAll
                ? next.Enemies.Where(e => e.Alive).ToList()
                : next.Enemies.Where(e => e.Index == targetIndex).ToList();

            foreach (SimEnemy debuffTarget in debuffTargets)
            {
                if (played.Poison > 0 && !(played.NeedsPoisonedTarget && debuffTarget.Poison == 0))
                    debuffTarget.Poison += played.Poison * times;
                if (played.Weak > 0)
                {
                    // 只有"原本不虚弱"的敌人才会因为本回合的虚弱额外减伤（意图伤害里已含它当前的虚弱）
                    if (debuffTarget.Weak == 0)
                        debuffTarget.WeakThisTurn += played.Weak * times;
                    debuffTarget.Weak += played.Weak * times;
                }
                if (played.IsXCost && played.WeakPerX > 0)
                {
                    if (debuffTarget.Weak == 0)
                        debuffTarget.WeakThisTurn += played.WeakPerX * effectiveCost;
                    debuffTarget.Weak += played.WeakPerX * effectiveCost;
                }
                if (played.Vulnerable > 0)
                {
                    debuffTarget.Vulnerable += played.Vulnerable * times;
                    debuffTarget.VulnerableThisTurn += played.Vulnerable * times;
                }
                if (played.IsXCost && played.StrengthLossPerX > 0)
                    debuffTarget.StrengthLoss += played.StrengthLossPerX * effectiveCost;
            }

            // 毒性爆发：给完中毒后立即触发一次（中毒伤害无视格挡）
            if (played.TriggersPoisonNow)
            {
                foreach (SimEnemy victim in next.Enemies.Where(e => e.Alive).ToList())
                {
                    if (victim.Poison > 0)
                        ApplyDamageIgnoringBlock(next, victim, victim.Poison);
                }
            }

            // 毒性爆发：给完中毒后立即触发一次（中毒伤害无视格挡；爆发翻倍时触发两次）
            for (int t = 0; t < times; t++)
            {
                if (played.TriggersPoisonNow)
                {
                    foreach (SimEnemy victim in next.Enemies.Where(e => e.Alive).ToList())
                    {
                        if (victim.Poison > 0)
                            ApplyDamageIgnoringBlock(next, victim, victim.Poison);
                    }
                }
            }

            if (played.StrengthLoss > 0 && played.HitsAll)
            {
                foreach (SimEnemy enemy in next.Enemies.Where(e => e.Alive))
                    enemy.StrengthLoss += played.StrengthLoss * times;
            }

            // 弃掉整手牌：钢铁风暴（每张换小刀）或计算下注（抽等量）
            if (played.DiscardHandForShivs || played.DiscardHandForDraw || played.DiscardsEntireHand)
            {
                List<CardEffect> discardedAll = new List<CardEffect>(next.Hand);
                next.Hand.Clear();

                // 被弃掉的奇巧(Sly)牌会立刻自动打出（与游戏一致）
                foreach (CardEffect dc in discardedAll)
                {
                    next.DiscardedThisTurn++;
                    ApplyDiscardTrigger(next, dc);
                }

                if (played.DiscardHandForShivs)
                {
                    for (int s = 0; s < discardedAll.Count; s++)
                        next.Hand.Add(NormalizeShiv(next, Shiv));
                }
                else if (played.DiscardHandForDraw)
                {
                    HandleDraws(next, discardedAll.Count);
                }
                // 暗影步：弃整手且不补充
            }

            // 普通弃牌（含被弃触发）；爆发翻倍时弃两次
            for (int d = 0; d < played.Discard * times && next.Hand.Count > 0; d++)
            {
                int pick = ChooseDiscard(next.Hand);
                CardEffect discarded = next.Hand[pick];
                next.Hand.RemoveAt(pick);
                next.DiscardedThisTurn++;
                ApplyDiscardTrigger(next, discarded);
            }

            // 抽牌（子弹时间后本回合不能再抽）；爆发翻倍时抽两次
            if (!next.NoDraw && played.Draw > 0)
                HandleDraws(next, played.Draw * times);

            // 生成小刀
            for (int s = 0; s < played.Shivs * times; s++)
                next.Hand.Add(NormalizeShiv(next, Shiv));

            // 群蛇形态：每打出一张牌对随机一名敌人造成伤害（这里按残血最少的目标近似）
            if (state.DamagePerCardPlayed > 0)
            {
                SimEnemy? victim = next.Enemies.Where(e => e.Alive).OrderBy(e => e.Hp).FirstOrDefault();
                if (victim is not null)
                    ApplyDamage(next, victim, state.DamagePerCardPlayed);
            }

            Dfs(next, depth + 1);
        }        /// <summary>"这张牌被弃掉时自动打出"的价值——挑弃哪张奇巧、以及手上技法给谁加奇巧都用它。</summary>
        private static decimal SlyValue(CardEffect c)
            => c.Damage + c.Block * 0.8m + c.Poison * 2m + c.Shivs * 4m
               + c.EnergyGain * 10m + c.Draw * 3m + c.Dexterity * 2m;

        /// <summary>
        /// 手上技法：给手牌里最值得"被弃掉自动打出"的技能牌加奇巧。
        /// 选择由模型替玩家做（面板本来就是给建议的）；手里没有可加的技能牌就不加。
        /// </summary>
        private static void MarkBestSkillSly(SearchState state)
        {
            int best = -1;
            decimal bestScore = -1m;
            for (int i = 0; i < state.Hand.Count; i++)
            {
                CardEffect c = state.Hand[i];
                if (c.IsSly || !c.IsSkill)
                    continue;
                decimal score = SlyValue(c);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }
            if (best < 0)
                return;
            CardEffect marked = state.Hand[best];
            // Id 必须变：状态指纹只按 Id 区分手牌，否则"加了奇巧"与"没加"会被当成同一状态而被剪掉
            state.Hand[best] = marked with { IsSly = true, Id = marked.Id + "_SLY" };
        }

        /// <summary>
        /// 弃牌优先级：先弃「奇巧(Sly)」牌——它们被弃时会自动打出（免费生效），挑价值最高的那张；
        /// 手上没有奇巧牌时，弃期望价值最低的那张。
        /// </summary>
        private static int ChooseDiscard(List<CardEffect> hand)
        {
            int bestSly = -1;
            decimal bestSlyScore = -1m;
            for (int i = 0; i < hand.Count; i++)
            {
                if (!hand[i].IsSly)
                    continue;
                decimal score = SlyValue(hand[i]);
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

        /// <summary>
        /// 被弃触发：奇巧（Sly）牌会**自动打出**（不花能量）。
        /// 战术大师给能量、本能反应抽牌都属于这一类，这里统一按"自动打出"处理。
        /// </summary>
        private void ApplyDiscardTrigger(SearchState state, CardEffect discarded)
        {
            if (!discarded.IsSly)
                return;

            // 自动打出：目标取当前血量最低的敌人（近似"优先补刀"）
            SimEnemy? target = state.Enemies.Where(e => e.Alive).OrderBy(e => e.Hp).FirstOrDefault();

            if (discarded.Damage > 0)
            {
                if (discarded.HitsAll)
                {
                    foreach (SimEnemy enemy in state.Enemies.Where(e => e.Alive).ToList())
                        ApplyDamage(state, enemy, discarded.Damage * DamageMultiplier(state, enemy, discarded.IsAttack));
                }
                else if (target is not null)
                {
                    ApplyDamage(state, target, discarded.Damage * DamageMultiplier(state, target, discarded.IsAttack));
                }
            }

            if (discarded.Block > 0)
                state.Block += discarded.Block + state.Dex;

            if (target is not null)
            {
                if (discarded.Poison > 0)
                    target.Poison += discarded.Poison;
                if (discarded.Weak > 0)
                {
                    if (target.Weak == 0)
                        target.WeakThisTurn += discarded.Weak;
                    target.Weak += discarded.Weak;
                }
                if (discarded.Vulnerable > 0)
                {
                    target.Vulnerable += discarded.Vulnerable;
                    target.VulnerableThisTurn += discarded.Vulnerable;
                }
            }

            state.Energy += discarded.EnergyGain;
            state.EnergyGained += discarded.EnergyGain;
            state.Dex += discarded.Dexterity;
            if (discarded.Draw > 0 && !state.NoDraw)
                HandleDraws(state, discarded.Draw);
            if (discarded.Shivs > 0)
            {
                for (int s = 0; s < discarded.Shivs; s++)
                    state.Hand.Add(NormalizeShiv(state, Shiv));
            }
        }

        /// <summary>
        /// 取当前应当使用的小刀模板：打过刀扇之后是"攻击全体"的版本。
        /// 生成的小刀、从抽牌堆翻出来的小刀都要走这里，否则刀扇之后的伤害会被按单体算。
        /// </summary>
        private CardEffect NormalizeShiv(SearchState state, CardEffect shiv)
            => state.ShivsHitAll && SilentLogic.IsShivCard(shiv.ClassName) ? ShivAll : shiv;

        /// <summary>抽牌（含腐蚀波/速行者的抽牌副作用）。</summary>
        private void HandleDraws(SearchState state, int count)
        {
            for (int d = 0; d < count && state.Draw.Count > 0; d++)
            {
                // 翻出来的小刀也要过一遍模板（刀扇之后应该是打全体的那版）
                state.Hand.Add(NormalizeShiv(state, state.Draw.Dequeue()));

                if (state.PoisonPerDraw > 0)
                {
                    foreach (SimEnemy enemy in state.Enemies.Where(e => e.Alive))
                        enemy.Poison += state.PoisonPerDraw;
                }

                if (state.DamagePerDraw > 0)
                {
                    foreach (SimEnemy enemy in state.Enemies.Where(e => e.Alive).ToList())
                        ApplyDamage(state, enemy, state.DamagePerDraw);
                }
            }
        }

        /// <summary>涂毒：攻击造成伤害时给目标叠中毒。</summary>
        private static void ApplyEnvenom(SearchState state, SimEnemy target)
        {
            if (state.Envenom > 0)
                target.Poison += state.Envenom;
        }

        /// <summary>中毒伤害：无视格挡。</summary>
        private static void ApplyDamageIgnoringBlock(SearchState state, SimEnemy target, decimal amount)
        {
            int dealt = Math.Min(target.Hp, (int)amount);
            if (dealt <= 0)
                return;
            target.Hp -= dealt;
            state.Damage += dealt;
        }

        /// <summary>
        /// 伤害倍率：易伤 ×1.5；跟踪（Tracking）只对**攻击**生效，对处于虚弱的敌人再 ×1.5。
        /// 两个 1.5 叠乘（这也是游戏里的行为：易伤与跟踪是不同来源的加成）。
        /// </summary>
        private static decimal DamageMultiplier(SearchState state, SimEnemy target, bool isAttack)
        {
            decimal multiplier = 1m;
            if (target.VulnerableThisTurn > 0)
                multiplier *= 1.5m;
            if (isAttack && state.TrackingNew && target.Weak > 0)
                multiplier *= 1.5m;
            return multiplier;
        }

        /// <summary>对全体存活敌人结算一次伤害（含涂毒）。</summary>
        private static void ApplyDamageToAll(SearchState state, decimal damage, bool isAttack)
        {
            foreach (SimEnemy enemy in state.Enemies.Where(e => e.Alive).ToList())
            {
                int before = enemy.Hp;
                ApplyDamage(state, enemy, damage * DamageMultiplier(state, enemy, isAttack));
                if (enemy.Hp < before)
                    ApplyEnvenom(state, enemy);
            }
        }

        private static void ApplyDamage(SearchState state, SimEnemy target, decimal amount)
        {
            int remaining = (int)amount;
            if (remaining <= 0)
                return;

            // 先扣敌人格挡，只有未被格挡的部分才算伤害（也才触发涂毒）
            if (target.Block > 0)
            {
                int absorbed = Math.Min(target.Block, remaining);
                target.Block -= absorbed;
                remaining -= absorbed;
            }
            if (remaining <= 0)
                return;

            int dealt = Math.Min(target.Hp, remaining);
            if (dealt <= 0)
                return;
            target.Hp -= dealt;
            state.Damage += dealt;
        }        /// <summary>状态指纹：能量/格挡/敏捷/弃牌数/本回合已出攻击数/各类标记 + 手牌 + 抽牌堆 + 敌人状态。</summary>
        private static string BuildStateKey(SearchState state)
        {
            var sb = new System.Text.StringBuilder(128);
            sb.Append(state.Energy).Append('|').Append(state.Block).Append('|').Append(state.Dex).Append('|')
              .Append(state.DiscardedThisTurn).Append('|')
              // ⚠️ 已出攻击数必须进指纹：终结技的伤害直接取决于它，漏掉会让"攒攻击数再打终结技"的分支被剪掉
              .Append(state.AttacksPlayed).Append('|')
              .Append(state.HandFree ? 1 : 0).Append(state.NoDraw ? 1 : 0).Append(state.DoubleBlock ? 1 : 0)
              .Append(state.FirstShivBonusUsed ? 1 : 0).Append(state.ShivsHitAll ? 1 : 0)
              .Append(state.TrackingNew ? 1 : 0).Append(state.NextSkillFree ? 1 : 0)
              .Append(state.DoubleSkillCount).Append('|')
              .Append('|').Append(state.ShivBonus).Append('|').Append(state.BlockPerCard).Append('|')
              .Append(state.Envenom).Append('|').Append(state.PoisonPerDraw).Append('|')
              .Append(state.DamagePerCardPlayed).Append('|').Append(state.DamagePerDraw).Append('|')
              .Append(state.FirstShivBonus).Append('|')
              .Append(state.Intangible).Append('|').Append(state.StrangleAmount).Append('|')
              .Append(state.StrangleTarget).Append('|').Append(state.Accelerant).Append('|');

            foreach (string id in state.Hand.Select(c => c.Id).OrderBy(x => x, StringComparer.Ordinal))
                sb.Append(id).Append(',');

            sb.Append('|');
            foreach (string id in state.Draw.Select(c => c.Id))
                sb.Append(id).Append(',');

            sb.Append('|');
            foreach (SimEnemy e in state.Enemies.OrderBy(x => x.Index))
                sb.Append(e.Index).Append(':').Append(e.Hp).Append(':').Append(e.Block).Append(':')
                  .Append(e.Poison).Append(':').Append(e.Weak).Append(':').Append(e.WeakThisTurn)
                  .Append(':').Append(e.Vulnerable).Append(':').Append(e.VulnerableThisTurn)
                  .Append(':').Append(e.StrengthLoss).Append(',');

            return sb.ToString();
        }

        /// <summary>这张牌此刻能提供多少格挡（蜃景按当前敌人中毒总和）。</summary>


        private static int BlockGain(CardEffect card, SearchState state)


        {


            if (card.Scaling == ScalingKind.EnemyPoisonTotal)


                return state.Enemies.Where(e => e.Alive).Sum(e => e.Poison);


            return card.Block;


        }



        private void Consider(SearchState state)
        {
            int incoming = state.Enemies.Where(e => e.Alive && !e.DiesToPoisonWith(state.Accelerant)).Sum(e => e.IncomingWith(state.Intangible));
            int totalBlock = CurrentBlock + state.Block;
            int hpLoss = Math.Max(0, incoming - totalBlock);
            bool lethal = hpLoss >= CurrentHp;

            var plan = new TurnPlan
            {
                Actions = new List<PlannedAction>(state.Actions),
                Damage = state.Damage,
                Block = state.Block,
                EnergySpent = state.EnergySpent,
                EnergyGained = state.EnergyGained,
                HpLoss = hpLoss,
                Lethal = lethal,

                Kills = state.Enemies.Count(e => !e.Alive || e.DiesToPoisonWith(state.Accelerant)),
                EnemiesAfter = state.Enemies.Select(e => e.Clone()).ToList(),
            };

            if (!_hasBest)
            {
                Best = plan;
                _hasBest = true;
                return;
            }

            if (IsBetter(plan, Best))
                Best = plan;
        }

        /// <summary>
        /// 打分顺序：不死 → 掉血 ≤ 预算（超出预算就先比谁掉血少）→ 优先击杀 → 最大伤害。
        /// 「优先击杀」是跨回合考量：打死一只怪等于省掉它下一轮的攻击。
        /// </summary>
        /// <summary>
        /// 两种优先级模式（F6 切换）：
        ///   保命优先：不死 → 掉血 ≤ 预算（超预算则少掉血优先）→ 优先击杀 → 最大伤害
        ///   输出优先：不死 → 优先击杀 → 最大伤害 → 掉血最少
        /// </summary>
        private static bool IsBetter(TurnPlan candidate, TurnPlan current)
        {
            if (candidate.Lethal != current.Lethal)
                return !candidate.Lethal;

            if (AdvisorSettings.DamageFirst)
            {
                if (candidate.Kills != current.Kills)
                    return candidate.Kills > current.Kills;
                if (candidate.Damage != current.Damage)
                        return candidate.Damage > current.Damage;
                if (candidate.HpLoss != current.HpLoss)
                    return candidate.HpLoss < current.HpLoss;
                return candidate.EnergySpent < current.EnergySpent;
            }

            int budget = AdvisorSettings.HpLossBudget;
            bool candidateInBudget = candidate.HpLoss <= budget;
            bool currentInBudget = current.HpLoss <= budget;

            if (candidateInBudget != currentInBudget)
                return candidateInBudget;

            if (!candidateInBudget && candidate.HpLoss != current.HpLoss)
                return candidate.HpLoss < current.HpLoss;

            if (candidate.Kills != current.Kills)
                    return candidate.Kills > current.Kills;

            if (candidate.Damage != current.Damage)
                return candidate.Damage > current.Damage;



            // 完全平局时别浪费资源：少花能量优先（避免为了"没收益的能力牌"白扔能量）


            return candidate.EnergySpent < current.EnergySpent;
        }
    }

    /// <summary>把一张手牌解析成可量化的效果（猎人专有语义见 SilentLogic）。</summary>
    private static CardEffect Analyze(CardModel card, AnalyzeContext? context)
    {
        string className = card.GetType().Name;
        string name = SafeName(card);
        int cost = SafeCost(card);
        bool supported = true;
        string note = "";

        bool isSly = false;


        bool unplayable = false;


        string keywordsText = "";


        try


        {


            isSly = card.IsSlyThisTurn;


            unplayable = card.Keywords.Contains(CardKeyword.Unplayable);



            var kw = new List<string>();


            if (card.Keywords.Contains(CardKeyword.Sly)) kw.Add("奇巧");


            if (card.Keywords.Contains(CardKeyword.Exhaust)) kw.Add("消耗");


            if (card.Keywords.Contains(CardKeyword.Innate)) kw.Add("固有");


            if (card.Keywords.Contains(CardKeyword.Retain)) kw.Add("保留");


            if (card.Keywords.Contains(CardKeyword.Ethereal)) kw.Add("虚无");


            if (unplayable) kw.Add("不可打出");


            keywordsText = kw.Count == 0 ? "" : string.Join("/", kw);


        }


        catch


        {


            // 读不到就按可打出处理


        }



        string unsupported = SilentLogic.UnsupportedReason(className);
        if (unsupported.Length > 0)
        {
                supported = false;
                note = unsupported;
        }

        else if (unplayable)

        {

                supported = false;

                note = "不可打出（Unplayable）";

        }
        else if (card.EnergyCost is { CostsX: true } && !SilentLogic.IsXCost(className))
        {
            supported = false;
            note = "X 费暂不支持";
        }

        Creature? target = context?.Target;

        // 伤害


        decimal damage = 0m;


        int repeats = HitCount(card);
        bool hitsAll = card.TargetType == TargetType.AllEnemies;
        int hits = 1;

        // 伤害/格挡随搜索状态变化的牌，统一在 PlayCard 里实时计算
        ScalingKind scaling = ScalingKind.None;
        if (SilentLogic.ScalesWithAttacksPlayed(className))
            scaling = ScalingKind.AttacksPlayed;
        else if (SilentLogic.ScalesWithSkillsInHand(className))
            scaling = ScalingKind.SkillsInHand;
        else if (SilentLogic.BlockFromEnemyPoison(className))
            scaling = ScalingKind.EnemyPoisonTotal;

        if (card.Type == CardType.Attack || SilentLogic.ScalesWithAttacksPlayed(className) || SilentLogic.ScalesWithSkillsInHand(className))
        {
            decimal perHit = target is null ? 0m : EstimateDamage(card, target);
            // 描述里写死的段数（匕首雨"两次"）也要乘进来，否则伤害只有一半
            hits = HitCount(card) * SilentLogic.HardcodedHits(className);

            // 终结技/飞镖：这里的 Damage 是"每次"的伤害，次数在出牌时按状态计算
            if (scaling != ScalingKind.None)
                hits = 1;

            damage = perHit * hits;
        }

        // 终结技的基数取游戏卡面上"（攻击 N 次）"的 N（含自身），这样本回合先前出过的攻击牌不会漏
        int scalingBaseHits = 1;
        if (scaling == ScalingKind.AttacksPlayed)
            scalingBaseHits = Math.Max(1, (int)Math.Floor(ReadPreview(card, "CalculatedHits")));

        // 格挡：取卡面预览值（含敏捷/遗物等修正）。取基础值会让"本场已有的敏捷"整个漏掉，
        // 导致格挡低估、预计掉血偏悲观、多推荐一张防御牌。
        int block = ReadPreviewByType(card, v => v is BlockVar) ?? (int)Math.Floor(ReadPreview(card, "Block"));
        if (SilentLogic.BlockFromEnemyPoison(className) && context is not null)
        {
            // 只算存活敌人；搜索内部每次出牌都会实时重算（见 BlockGain）
            block = context.Enemies.Where(e => e.Alive).Sum(e => e.Poison);
            note = "格挡=敌人中毒总和";
        }

        // 卡面效果
        int poison = ReadByType(card, v => v is PowerVar<PoisonPower>) ?? ReadInt(card, "PoisonPower");

        // 弹跳药瓶这类"随机给予 N 层中毒 Repeat 次"：总量 = N × 次数

        if (damage == 0m && poison > 0 && repeats > 1)

            poison *= repeats;
        int weak = ReadByType(card, v => v is PowerVar<WeakPower>) ?? ReadInt(card, "WeakPower");
        int vulnerable = ReadByType(card, v => v is PowerVar<VulnerablePower>) ?? ReadInt(card, "VulnerablePower");
        int strengthLoss = ReadInt(card, "StrengthLoss");
        // 侧步这类"下回合 +能量"的牌，变量同样叫 Energy，但本回合拿不到，不能算进搜索
        int energyGain = SilentLogic.GainsEnergyNextTurn(className)
            ? 0
            : ReadByType(card, v => v is EnergyVar) ?? ReadInt(card, "Energy");
        int dexterity = ReadByType(card, v => v is PowerVar<DexterityPower>) ?? ReadInt(card, "DexterityPower");

        // Cards 变量：抽牌 or 生成小刀（逐张表）
        int cardsVar = ReadByType(card, v => v is CardsVar) ?? ReadInt(card, "Cards");
        int draw = 0;
        int shivs = 0;
        switch (SilentLogic.CardsMeaning(className))
        {
            case CardsVarMeaning.Draw:
                draw = cardsVar;
                break;
            case CardsVarMeaning.GenerateShivs:
                shivs = cardsVar;
                break;
        }

        // Shivs 变量
        int shivsVar = ReadIntAny(card, "Shivs", "Shiv");
        if (shivsVar > 0 && SilentLogic.ShivsFromShivsVar(className))
            shivs += shivsVar;

        // 固定抽牌（描述里写死、没有 Cards 变量的牌）
        draw = Math.Max(draw, SilentLogic.FixedDraw(className));

        // 弃牌
        int discard = SilentLogic.FixedDiscard(className);
        if (SilentLogic.DiscardsCardsVar(className))
            discard += cardsVar;

        // 条件限制
        if (SilentLogic.RequiresEmptyDrawPile(className))
        {
            int drawCount = context?.DrawPileCount ?? 1;
            if (drawCount > 0)
            {
                supported = false;
                note = $"需抽牌堆为空（当前 {drawCount} 张）";
            }
            else
            {
                note = "抽牌堆已空，可打出";
            }
        }


        // ---- v0.6 机制 ----
        bool isX = SilentLogic.IsXCost(className);
        bool handFree = SilentLogic.MakesHandFree(className);
        bool noDraw = handFree;
        bool discardHandShivs = SilentLogic.DiscardsHandForShivs(className);
        bool discardHandDraw = SilentLogic.DiscardsHandForDraw(className);
        bool discardsEntireHand = SilentLogic.DiscardsEntireHand(className);
        int shivBonus = SilentLogic.GrantsShivBonus(className) ? ReadInt(card, "AccuracyPower") : 0;
        int blockPerCard = SilentLogic.GrantsBlockPerCard(className) ? ReadInt(card, "AfterimagePower") : 0;
        int weakPerX = className == "Malaise" ? 1 : 0;
        int strengthLossPerX = className == "Malaise" ? 1 : 0;
        int envenom = SilentLogic.GrantsEnvenom(className) ? ReadInt(card, "EnvenomPower") : 0;
        int poisonPerDraw = SilentLogic.GrantsPoisonPerDraw(className) ? ReadInt(card, "CorrosiveWave") : 0;
        int damagePerCardPlayed = SilentLogic.GrantsDamagePerCardPlayed(className) ? ReadInt(card, "SerpentFormPower") : 0;
        int damagePerDraw = SilentLogic.GrantsDamagePerDraw(className) ? ReadInt(card, "SpeedsterPower") : 0;
        bool doubleBlock = SilentLogic.DoublesBlock(className);
        int firstShivBonus = SilentLogic.GrantsFirstShivBonus(className) ? ReadInt(card, "PhantomBladesPower") : 0;

        int damagePerDiscard = className == "MementoMori" ? ReadInt(card, "ExtraDamage") : 0;

        bool triggersPoisonNow = className == "Outbreak";
        bool removesBlock = SilentLogic.RemovesEnemyBlock(className);
        bool makesShivsHitAll = SilentLogic.MakesShivsHitAll(className);
        // 静默低估补齐：这些牌以前被判成"有支持但没效果"或干脆没算
        bool grantsTracking = SilentLogic.GrantsTracking(className);
        int grantsIntangible = SilentLogic.GrantsIntangible(className) ? ReadInt(card, "IntangiblePower") : 0;
        int strangle = SilentLogic.AppliesStrangle(className) ? ReadInt(card, "StranglePower") : 0;
        bool nextSkillFree = SilentLogic.MakesNextSkillFree(className);
        bool needsPoisonedTarget = SilentLogic.NeedsPoisonedTarget(className);
        bool repeatsOnKill = SilentLogic.RepeatsOnKill(className);
        // 覆盖率补齐：显性"未建模"的 2 张 + 几处变量里看不到的第二效果
        int exhaustShivs = SilentLogic.PlaysExhaustShivs(className)
            ? (int)Math.Floor(ReadPreview(card, "CalculatedShivs"))
            : 0;
        if (exhaustShivs > 0)
            damage = exhaustShivs * (context?.ShivDamage ?? 0m);
        bool grantsSlyToSkill = SilentLogic.GrantsSlyToSkill(className);
        int grantsAccelerant = SilentLogic.GrantsAccelerant(className) ? ReadInt(card, "Accelerant") : 0;
        int doublesNextSkills = SilentLogic.DoublesNextSkills(className)
            ? Math.Max(1, ReadInt(card, "Skills"))
            : 0;
        if (SilentLogic.VulnerableFromPowerVar(className) && vulnerable == 0)
            vulnerable = ReadInt(card, "Power");
        string powerNote = SilentLogic.ImmediatePowerNote(className);
        if (powerNote.Length > 0)
            note = powerNote;
        if (isX)
            note = "X 费（按剩余能量算）";
        if (discardHandShivs || discardHandDraw)
                note = discardHandShivs ? "弃整手，每张换小刀" : "弃整手，抽等量";

        if (discardsEntireHand)

            note = "弃整手（下回合攻击翻倍，未建模）";

        // 所有机制都算完之后再判定"是否完全没有任何可量化效果"：


        // 特殊机制（弃整手/手牌免费/奇巧加成/能力联动…）也要计入，否则会被误判为未建模


        bool hasEffect = damage != 0m || block != 0 || poison != 0 || weak != 0 || vulnerable != 0


            || strengthLoss != 0 || draw != 0 || shivs != 0 || energyGain != 0 || dexterity != 0 || discard != 0


            || discardHandShivs || discardHandDraw || discardsEntireHand || handFree || shivBonus != 0 || blockPerCard != 0


            || envenom != 0 || poisonPerDraw != 0 || damagePerCardPlayed != 0 || damagePerDraw != 0


            || doubleBlock || firstShivBonus != 0 || removesBlock || makesShivsHitAll
            || grantsTracking || grantsIntangible != 0 || strangle != 0 || nextSkillFree
            || needsPoisonedTarget || repeatsOnKill || exhaustShivs != 0 || grantsSlyToSkill
            || grantsAccelerant != 0 || doublesNextSkills != 0;



        if (!hasEffect && string.IsNullOrEmpty(note))


        {


            if (card.Type == CardType.Power)


                note = "能力牌（本回合无即时收益）";


            else


            {


                supported = false;


                note = "未建模";


            }


        }



        return new CardEffect
        {
            Name = name,
            Id = SafeId(card),
            ClassName = className,
            Cost = cost,
            Damage = damage,
            HitsAll = hitsAll,
            Hits = hits,
            Block = block,
            Poison = poison,
            Weak = weak,
            Vulnerable = vulnerable,
            StrengthLoss = strengthLoss,
            Draw = draw,
            Shivs = shivs,
            EnergyGain = energyGain,
            Dexterity = dexterity,            Discard = discard,
            IsXCost = isX,
            DamagePerX = isX ? damage : 0m,
            WeakPerX = weakPerX,
            StrengthLossPerX = strengthLossPerX,
            HandFree = handFree,
            NoDraw = noDraw,
            DiscardHandForShivs = discardHandShivs,
            DiscardHandForDraw = discardHandDraw,
            DiscardsEntireHand = discardsEntireHand,
            ShivBonus = shivBonus,            BlockPerCard = blockPerCard,
            Envenom = envenom,
            PoisonPerDraw = poisonPerDraw,
            DamagePerCardPlayed = damagePerCardPlayed,
            DamagePerDraw = damagePerDraw,
            DoubleBlock = doubleBlock,
            FirstShivBonus = firstShivBonus,

            DamagePerDiscard = damagePerDiscard,

            TriggersPoisonNow = triggersPoisonNow,


            IsSly = isSly,
            IsAttack = card.Type == CardType.Attack,
            IsSkill = card.Type == CardType.Skill,
            Scaling = scaling,
            ScalingBaseHits = scalingBaseHits,
            RemovesBlock = removesBlock,
            MakesShivsHitAll = makesShivsHitAll,
            GrantsTracking = grantsTracking,
            GrantsIntangible = grantsIntangible,
            Strangle = strangle,
            MakesNextSkillFree = nextSkillFree,
            NeedsPoisonedTarget = needsPoisonedTarget,
            RepeatsOnKill = repeatsOnKill,
            ExhaustShivs = exhaustShivs,
            GrantsSlyToSkill = grantsSlyToSkill,
            GrantsAccelerant = grantsAccelerant,
            DoublesNextSkills = doublesNextSkills,



            KeywordsText = keywordsText,
            Supported = supported,
            Note = note,
            Source = card,
        };
    }

    /// <summary>
    /// 单只怪本回合打到"我"身上的意图伤害。
    /// 联机模式下敌人攻击对全队造成相同伤害，所以按自己作为目标计算即为准确值；
    /// 真正需要区分的是"我自己的格挡/血量"，那是玩家个人数据。
    /// </summary>
    public static (int Total, int Hits) IncomingOf(Creature enemy, Creature me)
        => IncomingInternal(enemy, new[] { me });

    /// <summary>诊断用：按全体友方计算，用于对比两者在联机下的差异。</summary>
    public static (int Total, int Hits) IncomingOfAllies(Creature enemy, IReadOnlyList<Creature> allies)
        => IncomingInternal(enemy, allies);

    private static (int Total, int Hits) IncomingInternal(Creature enemy, IEnumerable<Creature> targets)
    {
        int total = 0;
        int hits = 0;
        try
        {
            if (!enemy.IsAlive || enemy.Monster is null)
                return (0, 0);
            foreach (AbstractIntent intent in enemy.Monster.NextMove?.Intents ?? Array.Empty<AbstractIntent>())
            {
                if (intent is AttackIntent attack)
                {
                    int single = Math.Max(0, attack.GetSingleDamage(targets, enemy));
                    int repeats = Math.Max(1, attack.Repeats);
                    total += single * repeats;
                    hits += repeats;
                }
            }
        }
        catch
        {
            // 读不到就当 0
        }
        return (total, Math.Max(1, hits));
    }

    /// <summary>小刀基础伤害：规范小刀 + 力量 + 精准。</summary>
    private static decimal BuildShivDamage(Creature? target, int strength, int accuracy, bool playerWeak)
    {
        decimal baseDamage = 4m;
        try
        {
            Shiv canonical = ModelDb.Card<Shiv>();
            if (canonical.DynamicVars.TryGetValue("Damage", out DynamicVar? shivVar) && shivVar is not null)
                baseDamage = shivVar.BaseValue;
        }
        catch
        {
            // 用默认值
        }
        decimal shiv = baseDamage + strength + accuracy;
        if (playerWeak)
            shiv = shiv * 3 / 4;
        return Math.Max(0m, Math.Floor(shiv));
    }

    /// <summary>
    /// 生成小刀的模板。hitsAll=true 是"刀扇之后"的版本（攻击全体），
    /// Id 加后缀是为了让状态指纹把两种小刀区分开。
    /// </summary>
    private static CardEffect BuildShivEffect(decimal shivDamage, bool hitsAll)
    {
        try
        {
            Shiv canonical = ModelDb.Card<Shiv>();
            return new CardEffect
            {
                Name = SafeName(canonical),
                Id = SafeId(canonical) + (hitsAll ? "_AOE" : ""),
                ClassName = "Shiv",
                Cost = canonical.EnergyCost?.Canonical ?? 0,
                Damage = shivDamage,
                HitsAll = hitsAll,
                Supported = true,
                Note = hitsAll ? "小刀（打全体）" : "小刀",
                Source = canonical,
            };
        }
        catch
        {
            return new CardEffect
            {
                Name = "小刀",
                Id = hitsAll ? "SHIV_AOE" : "SHIV",
                ClassName = "Shiv",
                Cost = 0,
                Damage = 4m,
                HitsAll = hitsAll,
                Supported = true,
                Note = hitsAll ? "小刀（打全体）" : "小刀",
            };
        }
    }

    /// <summary>读某个变量的"卡面预览值"（终结技的 CalculatedHits 用它取本回合已出攻击数）。</summary>
    private static decimal ReadPreview(CardModel card, string key)
    {
        try
        {
            return card.DynamicVars.TryGetValue(key, out DynamicVar? variable) && variable is not null
                ? variable.PreviewValue
                : 0m;
        }
        catch
        {
            return 0m;
        }
    }

    private static decimal EstimateDamage(CardModel card, Creature target)    {
        try
        {
            // 精确切击/谋杀/铭记死亡这类计算型伤害放在 CalculatedDamage 里
            if (!card.DynamicVars.TryGetValue("Damage", out DynamicVar? variable) || variable is null)
            {
                if (!card.DynamicVars.TryGetValue("CalculatedDamage", out variable) || variable is null)
                    return 0m;
            }

            // 用游戏自己维护的卡面预览值：它已经算进了力量、虚弱、易伤等修正
            // （自己调 UpdateCardPreview 反而会把虚弱之类的修正覆盖掉，也会动到卡面显示）
            decimal preview = variable.PreviewValue > 0 ? variable.PreviewValue : variable.BaseValue;
            return Math.Max(0m, Math.Floor(preview));
        }
        catch
        {
            return 0m;
        }
    }

    private static int ReadPower<T>(Creature creature) where T : PowerModel
    {
        try
        {
            return creature.GetPower<T>()?.Amount ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static int HitCount(CardModel card)
        => Math.Max(1, ReadByType(card, v => v is RepeatVar) ?? ReadIntAny(card, "Repeat", "Repeats"));

    /// <summary>
    /// 按类型取变量的**卡面预览值**（含敏捷/遗物等修正），取不到退回基础值。
    /// ⚠️ 与 ReadByType 的区别：那个取 IntValue（基础值），格挡用它会把"本场已有的敏捷"整个漏掉
    /// （实测：防御 面板 5 / 游戏 7~10，差额正是敏捷）。本回合新获得的敏捷仍由 state.Dex 动态叠加，不会重复算。
    /// </summary>
    private static int? ReadPreviewByType(CardModel card, Func<DynamicVar, bool> match)
    {
        try
        {
            foreach (KeyValuePair<string, DynamicVar> pair in card.DynamicVars)
            {
                if (match(pair.Value))
                {
                    decimal value = pair.Value.PreviewValue > 0 ? pair.Value.PreviewValue : pair.Value.BaseValue;
                    return (int)Math.Floor(value);
                }
            }
        }
        catch
        {
            // 忽略
        }
        return null;
    }

    private static int? ReadByType(CardModel card, Func<DynamicVar, bool> match)
    {
        try
        {
            foreach (KeyValuePair<string, DynamicVar> pair in card.DynamicVars)
            {
                if (match(pair.Value))
                    return pair.Value.IntValue;
            }
        }
        catch
        {
            // 忽略
        }
        return null;
    }

    private static int ReadIntAny(CardModel card, params string[] keys)
    {
        foreach (string key in keys)
        {
            int value = ReadInt(card, key);
            if (value != 0)
                return value;
        }
        return 0;
    }

    private static int ReadInt(CardModel card, string key)
    {
        try
        {
            return card.DynamicVars.TryGetValue(key, out DynamicVar? variable) && variable is not null ? variable.IntValue : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 当前费用：走 <see cref="CardEnergyCost.GetWithModifiers"/>，这样"精密瞄准：本回合每打出过一张技能牌，
    /// 其耗能减少 1"以及遗物/其它牌带来的费用修正都自动覆盖（以前读 Canonical = 基础费用）。
    /// X 费单独处理：GetWithModifiers 对 X 没有意义，仍用 Canonical。
    /// </summary>
    private static int SafeCost(CardModel card)
    {
        try
        {
            CardEnergyCost? cost = card.EnergyCost;
            if (cost is null)
                return 99;
            return cost.CostsX ? cost.Canonical : cost.GetResolved();
        }
        catch
        {
            return 99;
        }
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

    /// <summary>卡名走游戏本地化表。</summary>
    public static string SafeName(CardModel card)
    {
        string fallback = "?";
        try
        {
            fallback = SafeId(card);
            LocString description = card.Description;
            string table = description.LocTable;
            string key = description.LocEntryKey;
            string baseKey = key.EndsWith(".description", StringComparison.Ordinal)
                ? key[..^".description".Length]
                : key;

            foreach (string candidate in new[] { baseKey + ".title", baseKey + ".name", baseKey })
            {
                if (!LocString.Exists(table, candidate))
                    continue;
                LocString? loc = LocString.GetIfExists(table, candidate);
                if (loc is null)
                    continue;
                string text = loc.GetFormattedText();
                if (!string.IsNullOrWhiteSpace(text))
                    return text.Trim();
            }
        }
        catch
        {
            // 落到 Id
        }
        return fallback;
    }

    public static string DescribeVars(CardModel card)
    {
        try
        {
            var parts = new List<string>();
            foreach (KeyValuePair<string, DynamicVar> pair in card.DynamicVars)
                parts.Add($"{pair.Key}={pair.Value.BaseValue:0.#}/{pair.Value.PreviewValue:0.#}");
            return string.Join(",", parts);
        }
        catch (Exception ex)
        {
            return "vars 读取失败：" + ex.Message;
        }
    }

    /// <summary>敌人身上关键 debuff 的指纹（面板刷新判断用：只有中毒层数变化时也要重算）。</summary>
    public static string DebuffKey(Creature enemy)
        => $"{ReadPower<PoisonPower>(enemy)}/{ReadPower<WeakPower>(enemy)}"
         + $"/{ReadPower<VulnerablePower>(enemy)}/{ReadPower<StrengthPower>(enemy)}";

    /// <summary>诊断用：敌人身上的关键 debuff（中毒/易伤/虚弱/力量），含队友施加的。</summary>


    public static string DescribePowers(Creature enemy)

    {


        try


        {


            var parts = new List<string>();


            int poison = ReadPower<PoisonPower>(enemy);


            int vulnerable = ReadPower<VulnerablePower>(enemy);


            int weak = ReadPower<WeakPower>(enemy);


            int strength = ReadPower<StrengthPower>(enemy);


            if (poison != 0) parts.Add($"中毒={poison}");


            if (vulnerable != 0) parts.Add($"易伤={vulnerable}");


            if (weak != 0) parts.Add($"虚弱={weak}");


            if (strength != 0) parts.Add($"力量={strength}");


            return parts.Count == 0 ? "无" : string.Join(" ", parts);


        }


        catch (Exception ex)


        {


            return "buff 读取失败：" + ex.Message;


        }


    }



    public static string DescribeIntent(Creature enemy, IReadOnlyList<Creature> allies)
    {
        try
        {
            if (enemy.Monster is null)
                return "非怪物";
            string moveId = enemy.Monster.NextMove?.Id ?? "(null)";
            var parts = new List<string>();
            foreach (AbstractIntent intent in enemy.Monster.NextMove?.Intents ?? Array.Empty<AbstractIntent>())
            {
                if (intent is AttackIntent attack)
                    parts.Add($"Attack[single={attack.GetSingleDamage(allies, enemy)},repeats={attack.Repeats}]");
                else
                    parts.Add(intent.GetType().Name);
            }
            return $"move={moveId} intents=[{string.Join(",", parts)}]";
        }
        catch (Exception ex)
        {
            return "意图读取失败：" + ex.Message;
        }
    }
}




































