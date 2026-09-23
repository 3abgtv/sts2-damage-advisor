// 影子状态搜索（SimSearch）的无头自检台 —— 只用手搭的 SimState，不碰任何游戏对象。
// 跑法见同目录 SelfCheck.csproj。每加一条镜像语义/一条缺口判据，就往这里加一条用例。
using System.Diagnostics;
using DamageAdvisor;

int fails = 0;

void Check(string name, string got, string want)
{
    bool ok = got == want;
    if (!ok) fails++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}   got={got}   want={want}");
}

SimState Make(int energy, int block, int hp, int incoming, int foeHp,
              int foeBlock = 0, int foePoison = 0, int hits = 1)
{
    var sim = new SimState
    {
        PlayerHp = hp,
        PlayerBlock = block,
        PlayerEnergy = energy,
        PlayerMaxEnergy = energy,
        RoundNumber = 1,
    };
    sim.Foes.Add(new SimFoe
    {
        Index = 1,
        Name = "测试怪",
        Hp = foeHp,
        Block = foeBlock,
        Poison = foePoison,
        BaseIncoming = incoming,
        Hits = hits,
    });
    return sim;
}

static CardEffect Atk(string id, int cost, int dmg) => new()
{
    Name = id, Id = id, ClassName = id, Cost = cost, Damage = dmg, IsAttack = true, Supported = true,
};

static CardEffect Def(string id, int cost, int block) => new()
{
    Name = id, Id = id, ClassName = id, Cost = cost, Block = block, IsSkill = true, Supported = true,
};

// ① 掉血预算为 0：攻击 + 格挡，两边都超预算 → 比掉血最少，再比伤害最大
{
    SimState s = Make(energy: 3, block: 0, hp: 40, incoming: 10, foeHp: 20);
    s.Hand.Add(Atk("打击", 1, 6));
    s.Hand.Add(Def("防御", 1, 5));
    SimSearchResult r = SimSearch.Solve(s, 0);
    Check("① 伤害/格挡/掉血", $"{r.Plan.Damage}/{r.Plan.Block}/{r.Plan.HpLoss}", "6/5/5");
    Check("① 终态与计划同源", $"{r.Terminal.PlayerBlock}/{r.Terminal.PlayerEnergy}", "5/1");
}

// ② 能量只够打一张
{
    SimState s = Make(energy: 1, block: 0, hp: 40, incoming: 10, foeHp: 20);
    s.Hand.Add(Atk("打击", 1, 6));
    s.Hand.Add(Atk("打击", 1, 6));
    SimSearchResult r = SimSearch.Solve(s, 0);
    Check("② 能量 1 只打 1 张", $"{r.Plan.Damage}/{r.Plan.EnergySpent}", "6/1");
    Check("② 同名剪枝：状态数很少", $"{r.Nodes <= 8}", "True");
}

// ③ 先扣格挡再扣血
{
    SimState s = Make(energy: 3, block: 0, hp: 40, incoming: 0, foeHp: 20, foeBlock: 3);
    s.Hand.Add(Atk("打击", 1, 6));
    SimSearchResult r = SimSearch.Solve(s, 0);
    Check("③ 格挡吃掉 3 点", $"{r.Plan.Damage}", "3");
}

// ④ 击杀计数
{
    SimState s = Make(energy: 3, block: 0, hp: 40, incoming: 0, foeHp: 5);
    s.Hand.Add(Atk("打击", 1, 6));
    SimSearchResult r = SimSearch.Solve(s, 0);
    Check("④ 打死 1 只", $"{r.Plan.Kills}", "1");
}

// ⑤ 中毒先手击杀：这怪不会打过来，也不该算进掉血
{
    SimState s = Make(energy: 0, block: 0, hp: 40, incoming: 10, foeHp: 4, foePoison: 5);
    SimSearchResult r = SimSearch.Solve(s, 0);
    Check("⑤ 中毒先手击杀→不掉血/算击杀", $"{r.Plan.HpLoss}/{r.Plan.Kills}", "0/1");
}

// ⑥ 0 费 6 张同名：剪枝后应能打满，且不炸搜索
{
    SimState s = Make(energy: 10, block: 0, hp: 40, incoming: 0, foeHp: 200);
    for (int i = 0; i < 6; i++)
        s.Hand.Add(Atk("打击", 0, 1));
    var sw = Stopwatch.StartNew();
    SimSearchResult r = SimSearch.Solve(s, 0);
    sw.Stop();
    Check("⑥ 6 张 0 费同名打满", $"{r.Plan.Damage}", "6");
    Check("⑥ 状态数没炸", $"{r.Nodes <= 20}", "True");
    Console.WriteLine($"      （⑥ 耗时 {sw.ElapsedMilliseconds}ms，{r.Nodes} 状态）");
}

// ⑦ 会打的怪掉血：无实体让每段只吃 1 点
{
    SimState s = Make(energy: 0, block: 0, hp: 40, incoming: 12, foeHp: 30, hits: 3);
    Check("⑦ 通常会掉 12", $"{SimSearch.Solve(s, 0).Plan.HpLoss}", "12");
    Check("⑦ 无实体只掉 3", $"{SimSearch.Solve(s, 1).Plan.HpLoss}", "3");
}

// ⑧ 空手牌也是合法答案（登记预测时要能识别）
{
    SimState s = Make(energy: 3, block: 0, hp: 40, incoming: 6, foeHp: 30);
    SimSearchResult r = SimSearch.Solve(s, 0);
    Check("⑧ 不打牌", $"{r.Plan.Actions.Count}/{r.Plan.HpLoss}", "0/6");
}

// ⑨ 未镜像语义要能认出来（给足伤害，确保它会被选进计划）
{
    SimState s = Make(energy: 3, block: 0, hp: 40, incoming: 0, foeHp: 30);
    s.Hand.Add(new CardEffect
    {
        Name = "幽魂形态", Id = "幽魂形态", ClassName = "WraithForm", Cost = 1,
        Damage = 10, GrantsIntangible = 2, Supported = true,
    });
    SimSearchResult r = SimSearch.Solve(s, 0);
    Check("⑨ 认出未镜像语义", $"{r.HandGaps.Count > 0}/{r.PlanGaps.Count > 0}", "True/True");
    Console.WriteLine($"      （⑨ {string.Join(" | ", r.PlanGaps)}）");
}

// ⑩ 爆发的判据必须挂在 DoublesNextSkills 上。它漏过一次，而漏法最阴：
//    影子不实现 → 算不出那条线 → 不选它 → 它进不了 PlanGaps，只有手牌这层拦得住。
{
    SimState s = Make(energy: 3, block: 0, hp: 40, incoming: 0, foeHp: 30);
    s.Hand.Add(Atk("打击", 1, 6));
    s.Hand.Add(new CardEffect
    {
        Name = "爆发", Id = "爆发", ClassName = "Burst", Cost = 1,
        DoublesNextSkills = 1, IsSkill = true, Supported = true,
    });
    SimSearchResult r = SimSearch.Solve(s, 0);
    Check("⑩ 手牌里的爆发要拦得住", $"{r.HandGaps.Count > 0}/{r.PlanGaps.Count}", "True/0");
    Console.WriteLine($"      （⑩ {string.Join(" | ", r.HandGaps)}）");
}

// ⑪ 有模板时刀刃之舞能打满 3 刀（模板由主模型下发，值是 4）
{
    SimState s = Make(energy: 3, block: 0, hp: 40, incoming: 0, foeHp: 60);
    s.ShivTemplate = new CardEffect { Name = "小刀", Id = "SHIV", ClassName = "Shiv",
        Cost = 0, Damage = 4, IsAttack = true, Supported = true };
    s.ShivTemplateAoe = s.ShivTemplate with { Id = "SHIV_AOE", HitsAll = true };
    s.Hand.Add(new CardEffect { Name = "刀刃之舞", Id = "刀刃之舞", ClassName = "BladeDance",
        Cost = 1, Shivs = 3, IsSkill = true, Supported = true });
    SimSearchResult r = SimSearch.Solve(s, 0);
    Check("⑪ 有模板：3 刀打满 12", $"{r.Plan.Damage}", "12");
    Check("⑪ 计划含 4 张（刀刃之舞 + 3 小刀）", $"{r.Plan.Actions.Count}", "4");
}

// ⑫ 没有模板时按 0 张算 —— 关键是**必须出声**（实机踩过：静默低估 → 选错计划）
{
    SimState s = Make(energy: 3, block: 0, hp: 40, incoming: 0, foeHp: 60);
    s.Hand.Add(new CardEffect { Name = "刀刃之舞", Id = "刀刃之舞", ClassName = "BladeDance",
        Cost = 1, Shivs = 3, IsSkill = true, Supported = true });
    SimSearchResult r = SimSearch.Solve(s, 0);
    Check("⑫ 无模板：按 0 张算", $"{r.Plan.Damage}", "0");
    Check("⑫ 无模板：要报 gap", $"{r.HandGaps.Count > 0 || r.PlanGaps.Count > 0}", "True");
    Console.WriteLine($"      （⑫ {string.Join(" | ", r.PlanGaps.Concat(r.HandGaps))}）");
}

// ⑬ 弃牌提示 + **顺序**：杂技是"先抽后弃"，该弃的是刚抽上来那张（垃圾），
//    不是手里原有的打击。老代码两边都写成"先弃后抽"，那样会把打击弃掉、留下三张垃圾。
{
    SimState s = Make(energy: 3, block: 0, hp: 40, incoming: 0, foeHp: 60);
    s.Hand.Add(new CardEffect { Name = "杂技", Id = "杂技", ClassName = "Acrobatics",
        Cost = 1, Draw = 3, Discard = 1, IsSkill = true, Supported = true });
    s.Hand.Add(Atk("打击", 0, 6));
    // 抽到的要有值钱的（否则杂技不值得花那 1 点能量，计划里根本不会有它）；
    // 同时塞两张垃圾，弃牌该挑的正是这两张
    s.Draw.Enqueue(Atk("好牌", 0, 6));
    for (int i = 0; i < 2; i++)
        s.Draw.Enqueue(new CardEffect { Name = "垃圾", Id = "垃圾" + i, ClassName = "Junk",
            Cost = 0, IsSkill = true, Supported = true });
    SimSearchResult r = SimSearch.Solve(s, 0);
    string order = string.Join(" → ", r.Plan.Actions.Select(a => DamageModel.DescribeAction(a, false)));
    Check("⑬ 弃的是刚抽上来那张（先抽后弃）", $"{order.Contains("（弃 垃圾）")}", "True");
    Check("⑬ 手里原有的打击要留住（伤害 12）", $"{r.Plan.Damage}", "12");
    Console.WriteLine($"      （⑬ {order}）");
}

// ⑭⑮ 虚弱：本回合新上的要按 25% 减来袭；**原本就虚弱的不能再乘一次**
//      （意图伤害里已含它当前的虚弱）。守卫必须与主模型 ApplyCard 里 `Weak == 0` 同式。
void WeakCase(string name, int preWeak, int wantHpLoss)
{
    SimState s = Make(energy: 3, block: 0, hp: 40, incoming: 12, foeHp: 60);
    s.Foes[0].Weak = preWeak;
    s.Hand.Add(new CardEffect { Name = "中和", Id = "中和", ClassName = "Neutralize",
        Cost = 0, Damage = 3, Weak = 1, IsAttack = true, Supported = true });
    SimSearchResult r = SimSearch.Solve(s, 0);
    Check(name, $"{r.Plan.HpLoss}", $"{wantHpLoss}");
}
WeakCase("⑭ 新上的虚弱让掉血 12→9", preWeak: 0, wantHpLoss: 9);
WeakCase("⑮ 原本就虚弱：不再减（12 不变）", preWeak: 2, wantHpLoss: 12);
{
    // 顺带确认台账那条已删：中和 不该再触发"不可比"（否则一副带中和的手牌整轮都没法核对）
    SimState s = Make(energy: 3, block: 0, hp: 40, incoming: 12, foeHp: 60);
    s.Hand.Add(new CardEffect { Name = "中和", Id = "中和", ClassName = "Neutralize",
        Cost = 0, Damage = 3, Weak = 1, IsAttack = true, Supported = true });
    SimSearchResult r = SimSearch.Solve(s, 0);
    Check("⑮ 中和 不再被标不可比", $"{r.PlanGaps.Count == 0 && r.HandGaps.Count == 0}", "True");
}

Console.WriteLine(fails == 0 ? "\n全部通过" : $"\n{fails} 项失败");
return fails == 0 ? 0 : 1;
