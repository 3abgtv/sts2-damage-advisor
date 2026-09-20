#if !WORKSHOP
namespace DamageAdvisor;

/// <summary>新引擎自算的结果。Terminal 是**搜索自己走到的终态**，可以直接拿去登记差分预测。</summary>
internal sealed class SimSearchResult
{
    public required TurnPlan Plan { get; init; }
    /// <summary>最优计划的终态影子状态（与 Plan.Actions 严格同源，不是重放出来的）。</summary>
    public required SimState Terminal { get; init; }
    public required int Nodes { get; init; }
    /// <summary>这套计划**用到了**未镜像的语义（非空＝这套数字不能与主模型并排比）。</summary>
    public required IReadOnlyList<string> PlanGaps { get; init; }
    /// <summary>手牌/抽牌堆里**存在**未镜像的语义（非空＝数字可能偏小，但这套计划未必用到它）。</summary>
    public required IReadOnlyList<string> HandGaps { get; init; }
}

/// <summary>
/// 第四步：给新引擎加**它自己的搜索**。
///
/// 与主模型 `DamageModel.SearchContext` 的关系是"同一套骨架、不同的地基"：
/// 骨架（记忆化 DFS + 同名牌剪枝 + 逐目标枚举 + 每个节点都 Consider）照抄，
/// 但每一步推进走的是 `SimCommands`——它踩在影子状态上，不碰任何游戏对象。
///
/// 打分共用 `DamageModel.IsBetter`：分数口径必须两个引擎一致，否则差分里会多出一个
/// "偏好不同"的假差异，把真正的规则差异淹掉。
///
/// ⚠️ 影子模拟还没镜像的语义（见 `Gaps`）会让这套数字**偏小**。这种回合不该拿数字去比 ——
/// `PlanGaps` / `HandGaps` 就是用来把"不可比"如实标出来，而不是让玩家看到假的差异。
/// </summary>
internal static class SimSearch
{
    // 与主模型同量级的预算：记忆化之后 Nodes 计的是"唯一状态数"，不是步数
    private const int MaxNodes = 25000;
    private const int MaxDepth = 14;

    /// <summary>
    /// 从一份影子状态出发搜索本回合的最优出牌顺序。
    /// `playerIntangible` 由调用方从活体读（影子状态不记无实体 —— 那是 Gap 之一）。
    /// </summary>
    public static SimSearchResult Solve(SimState root, int playerIntangible)
    {
        var ctx = new Ctx
        {
            Intangible = playerIntangible,
            RootBlock = root.PlayerBlock,
            RootFoeHp = root.Foes.Sum(f => f.Hp),
            HandGaps = GapsIn(root.Hand.Concat(root.Draw)),
        };

        Dfs(ctx, root, depth: 0, spent: 0, path: new List<PlannedAction>());

        // 一张牌都打不出来（全是打不出的/不支持的）也是合法答案："原地结束回合"。
        // 这时仍然给出终态，调用方照样能登记一次预测。
        TurnPlan plan = ctx.Best ?? EmptyPlan(root);
        SimState terminal = ctx.BestTerminal ?? root.Clone();

        List<string> planGaps = new(GapsIn(plan.Actions.Select(a => a.Card)));
        List<string> handGaps = new(ctx.HandGaps);
        // 小刀模板为空时，"生成小刀"的牌会被按 0 张算。这条缺口**不是卡牌字段能表达的**
        // ——同一张刀刃之舞，手上有真小刀时是对的、没有时是错的——所以台账里挂不上，
        // 得单独判一次。它最阴的地方是让影子静默地低估这类牌、进而选错计划：
        // 计划里没有它 → 进不了 PlanGaps，只有手牌那一层拦得住（2026-09-20 实机踩过）。
        if (root.ShivTemplate is null)
        {
            const string why = "生成小刀（影子里没有小刀模板 → 按 0 张算）";
            if (plan.Actions.Any(a => MakesShivs(a.Card)))
                planGaps.Add(why);
            else if (root.Hand.Concat(root.Draw).Any(MakesShivs))
                handGaps.Add(why);
        }

        return new SimSearchResult
        {
            Plan = plan,
            Terminal = terminal,
            Nodes = ctx.Nodes,
            PlanGaps = planGaps,
            HandGaps = handGaps,
        };
    }

    /// <summary>这张牌会不会"生成小刀"（两条来源：牌面小刀数、弃整手换小刀）。</summary>
    private static bool MakesShivs(CardEffect c) => c.Shivs > 0 || c.DiscardHandForShivs;

    private sealed class Ctx
    {
        public required int Intangible { get; init; }
        public required int RootBlock { get; init; }
        public required int RootFoeHp { get; init; }
        public required IReadOnlyList<string> HandGaps { get; init; }

        /// <summary>已展开的等价状态（手牌/能量/敌人状态相同就不重复展开，消除出牌顺序的排列爆炸）。</summary>
        public readonly HashSet<string> Visited = new(StringComparer.Ordinal);
        public int Nodes;
        public TurnPlan? Best;
        public SimState? BestTerminal;
    }

    private static void Dfs(Ctx ctx, SimState sim, int depth, int spent, List<PlannedAction> path)
    {
        string key = BuildStateKey(sim);
        if (!ctx.Visited.Add(key))
            return;

        ctx.Nodes++;
        Consider(ctx, sim, spent, path);

        if (ctx.Nodes > MaxNodes || depth >= MaxDepth)
            return;

        for (int i = 0; i < sim.Hand.Count; i++)
        {
            CardEffect card = sim.Hand[i];
            if (!card.Supported)
                continue;
            if (IsDuplicate(sim.Hand, i))
                continue;

            // 与 SimCommands.PlayCard 里的收费口径**必须一致**（包括"用打出这张牌之前"的 HandFree）
            int cost = card.IsXCost ? sim.PlayerEnergy : (sim.HandFree ? 0 : card.Cost);
            if (cost > sim.PlayerEnergy)
                continue;

            bool needsTarget = !card.HitsAll && (card.Damage > 0 || card.Poison > 0 || card.Weak > 0
                || card.Vulnerable > 0 || card.StrengthLoss > 0
                || (card.IsXCost && (card.WeakPerX > 0 || card.StrengthLossPerX > 0)));

            if (needsTarget)
            {
                foreach (SimFoe foe in sim.Foes.Where(f => f.Alive).ToList())
                    Branch(ctx, sim, i, foe.Index, depth, spent + cost, path, card);
            }
            else
            {
                Branch(ctx, sim, i, 0, depth, spent + cost, path, card);
            }
        }
    }

    /// <summary>
    /// 走一步。**必须克隆**：SimCommands 是就地改状态的，直接在父状态上打会把兄弟分支互相污染。
    /// </summary>
    private static void Branch(Ctx ctx, SimState sim, int handIndex, int targetIndex,
        int depth, int spent, List<PlannedAction> path, CardEffect card)
    {
        SimState child = sim.Clone();
        // 先建好这一步、再把它的 Discards 交给 PlayCard 填：弃哪张是 PlayCard 里才定的，
        // 而面板要显示它（"打杂技"没说清怎么打）
        var action = new PlannedAction { Card = card, TargetIndex = targetIndex };
        SimCommands.PlayCard(child, handIndex, targetIndex, action.Discards);

        path.Add(action);
        Dfs(ctx, child, depth + 1, spent, path);
        path.RemoveAt(path.Count - 1);
    }

    /// <summary>
    /// 每个节点都是一套"可以就此收手"的候选计划 —— 所以任意前缀都在考虑范围内
    /// （不必打满手牌；打 1 张就收手常常才是对的）。
    /// </summary>
    private static void Consider(Ctx ctx, SimState sim, int spent, List<PlannedAction> path)
    {
        // 中毒先手击杀的怪不会打过来 —— 与主模型 Consider 同一算法
        int incoming = sim.Foes.Where(f => f.Alive && !f.DiesToPoisonWith(sim.Accelerant))
            .Sum(f => f.IncomingWith(ctx.Intangible));
        int hpLoss = Math.Max(0, incoming - sim.PlayerBlock);

        var plan = new TurnPlan
        {
            Actions = new List<PlannedAction>(path),
            // 伤害用"敌人总血量掉了多少"：与 CloneProbe 重放那条路径同一个口径
            Damage = ctx.RootFoeHp - sim.Foes.Sum(f => f.Hp),
            Block = sim.PlayerBlock - ctx.RootBlock,
            EnergySpent = spent,
            EnergyGained = 0,
            HpLoss = hpLoss,
            Lethal = hpLoss >= sim.PlayerHp,
            Kills = sim.Foes.Count(f => !f.Alive || f.DiesToPoisonWith(sim.Accelerant)),
            // EnemiesAfter / NextTurn 不回填：新引擎的面板直接用 Terminal 显示，
            // 跨回合资源账是第 6 步的事（IsBetter 不看这两个字段，所以不影响选出的计划）
        };

        if (ctx.Best is null || DamageModel.IsBetter(plan, ctx.Best))
        {
            ctx.Best = plan;
            // sim 在此之后不会再被改（Dfs 只在克隆体上继续推进），可以直接持有引用
            ctx.BestTerminal = sim;
        }
    }

    private static TurnPlan EmptyPlan(SimState root)
    {
        int incoming = root.Foes.Where(f => f.Alive && !f.DiesToPoisonWith(root.Accelerant))
            .Sum(f => f.Incoming);
        int hpLoss = Math.Max(0, incoming - root.PlayerBlock);
        return new TurnPlan
        {
            Actions = new List<PlannedAction>(),
            Damage = 0,
            Block = 0,
            HpLoss = hpLoss,
            Lethal = hpLoss >= root.PlayerHp,
            Kills = root.Foes.Count(f => !f.Alive || f.DiesToPoisonWith(root.Accelerant)),
        };
    }

    /// <summary>同名牌只试一次：互相替换不会产生更优解，却带来 N! 级别的等价分支。</summary>
    private static bool IsDuplicate(List<CardEffect> hand, int i)
    {
        CardEffect card = hand[i];
        for (int k = 0; k < i; k++)
        {
            CardEffect other = hand[k];
            if (other.Id == card.Id && other.Cost == card.Cost && other.Damage == card.Damage
                && other.Block == card.Block && other.Poison == card.Poison
                && other.Shivs == card.Shivs && other.Vulnerable == card.Vulnerable
                && other.Weak == card.Weak)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 状态指纹。手牌**按 Id 排序**（同名牌的排列不算不同状态），抽牌堆**按顺序**（顺序决定抽到什么）。
    /// 凡是模拟会就地改、又会影响打分的量都必须进去，漏一个就会被错误剪枝。
    /// </summary>
    private static string BuildStateKey(SimState sim)
    {
        var sb = new System.Text.StringBuilder(160);
        sb.Append(sim.PlayerEnergy).Append('|').Append(sim.PlayerBlock).Append('|')
          .Append(sim.PlayerHp).Append('|')
          .Append(sim.HandFree ? 1 : 0).Append(sim.NoDraw ? 1 : 0)
          .Append(sim.DoubleBlock ? 1 : 0).Append(sim.ShivsHitAll ? 1 : 0)
          .Append(sim.DoubleSkillCount).Append('|')
          .Append(sim.ShivBonus).Append('|').Append(sim.BlockPerCard).Append('|')
          .Append(sim.Envenom).Append('|').Append(sim.PoisonPerDraw).Append('|')
          .Append(sim.DamagePerCardPlayed).Append('|').Append(sim.DamagePerDraw).Append('|')
          .Append(sim.StrangleAmount).Append('|').Append(sim.StrangleTarget).Append('|')
          .Append(sim.Accelerant).Append('|');

        foreach (string id in sim.Hand.Select(c => c.Id).OrderBy(x => x, StringComparer.Ordinal))
            sb.Append(id).Append(',');

        sb.Append('|');
        foreach (CardEffect c in sim.Draw)
            sb.Append(c.Id).Append(',');

        sb.Append('|');
        foreach (SimFoe f in sim.Foes.OrderBy(x => x.Index))
            sb.Append(f.Index).Append(':').Append(f.Hp).Append(':').Append(f.Block).Append(':')
              .Append(f.Poison).Append(':').Append(f.Weak).Append(':').Append(f.WeakThisTurn)
              .Append(':').Append(f.Vulnerable).Append(':').Append(f.VulnerableNew ? 1 : 0)
              .Append(':').Append(f.StrengthLoss).Append(',');

        return sb.ToString();
    }

    /// <summary>
    /// 影子模拟**尚未镜像**的语义台账（判据写在 CardEffect 的字段上，不是牌名清单 ——
    /// 新牌只要带这些字段就会自动被标住）。命中＝数字偏小，不能与主模型并排比。
    /// **每在 SimCommands 里补上一条，就删掉这里一条。这份清单是待办，不是设计。**
    ///
    /// 不在清单里的东西（跨回合资源账 BlockNextTurn/EnergyNextTurn/HandNextTurn/RetainBlock、
    /// 关键词 IsRetain、以及主模型自己也没读的 DamagePerX）不影响本回合选出的计划。
    /// </summary>
    private static readonly (Func<CardEffect, bool> Has, string Why)[] Gaps =
    {
        (c => c.Dexterity > 0, "敏捷（模拟里不往上累加）"),
        (c => c.GrantsIntangible > 0, "无实体（模拟里不记，掉血会算高）"),
        (c => c.Scaling != ScalingKind.None, "缩放伤害/格挡（模拟不按缩放算）"),
        (c => c.GrantsTracking, "跟踪（模拟里不记）"),
        (c => c.FirstShivBonus > 0, "首刀加成（模拟里不记）"),
        (c => c.MakesNextSkillFree, "下一张技能免费（模拟里不认）"),
        // 爆发漏了会**悄悄带偏计划**（2026-09-20 实机抓到）：影子算不出"技能双触发"那条线，
        // 就不会选它，于是它根本进不了 PlanGaps —— 只有手牌那一层（HandGaps）拦得住。
        (c => c.DoublesNextSkills > 0, "爆发（技能额外打出一次，模拟里不认）"),
        (c => c.StrengthLoss > 0 || c.StrengthLossPerX > 0, "削力量（模拟不改敌人力量）"),
        (c => c.Weak > 0 || c.WeakPerX > 0, "上虚弱（模拟不计入本回合来袭减伤）"),
        (c => c.DamagePerDiscard > 0, "每弃一张牌增伤（模拟里不数弃牌）"),
        (c => c.BlockIfSkillDrawn > 0, "逃脱计划（模拟不按抽到的牌判定）"),
        (c => c.TriggersPoisonNow, "毒性爆发（模拟不立即结算中毒）"),
        (c => c.RepeatsOnKill, "回响斩击（模拟不因击杀重复）"),
        (c => c.ExhaustShivs > 0, "刀刃陷阱（模拟不打消耗堆里的小刀）"),
    };

    private static IReadOnlyList<string> GapsIn(IEnumerable<CardEffect> cards)
    {
        var hits = new List<string>();
        foreach (CardEffect card in cards)
        {
            foreach ((Func<CardEffect, bool> has, string why) in Gaps)
            {
                if (!has(card))
                    continue;
                string tag = $"{card.Name}（{why}）";
                if (!hits.Contains(tag))
                    hits.Add(tag);
            }
        }
        return hits;
    }
}
#endif
