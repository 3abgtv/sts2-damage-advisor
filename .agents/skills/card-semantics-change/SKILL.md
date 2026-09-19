---
name: card-semantics-change
description: 新增或修改《杀戮尖塔 2》卡牌在本 Mod 里的战斗语义时使用——判断一张牌"本回合到底做什么"、接进搜索状态机、并保证不给出错误建议。不适用于纯 UI 文案调整或评分目标改动。
---

# 卡牌语义变更（DamageAdvisor）

## 适用边界

本 skill 负责"一张牌在本回合的效果如何被量化"。评分权重改动走 `recommendation-objective`；面板显示走 `panel-ui-change`；部署与发布走 `deploy-and-release`。

**第一原则：算不准就显式标出来，绝不猜。** 未建模的牌必须在面板上写清原因（`SilentLogic.UnsupportedReason`），而不是给一个看起来合理的数字。

## 1. 先拿到权威语义（不要凭记忆写）

按可靠性排序：

1. **游戏内卡池转储**：进战斗后日志里的 `卡池 <类名> id=... name=... type=... cost=... target=... kw=[...] desc=[...] vars=[...]`
   —— 含**卡牌描述原文**与**全部动态变量**，这是最关键的一手资料（`docs/silent-pool.txt` 是本机转储副本，已被 .gitignore 排除，不随仓库分发）。
2. **CombatSolver 的公开实现**（`Torch1230/CombatSolver`，`src/Prediction/` 下按牌名分发的 `CardOnPlaySupport.cs`、`CardPowerOnPlaySupport*.cs`、`CorePowerSupport.cs`、`CardPileOnPlaySupport.cs`、`CardChoiceSupport.cs`）
   —— 它必须精确才能算对，是最好的交叉验证。**只参考语义，不要复制代码**（该仓库无授权）。
3. 卡面描述里的占位符（如 `{Cards:diff()}`、`{Shivs:diff()}`）能直接告诉你是哪个变量、代表什么。

⚠️ 变量含义**可能是重载的**：`Cards=` 在 `刀刃之舞` 上是"生成小刀数量"，在 `杂技` 上是"抽牌数"。必须逐张判定，见 `SilentLogic.CardsMeaning`。

## 2. 落到代码

- 判定函数集中在 `src/SilentLogic.cs`（`CardsMeaning` / `FixedDiscard` / `GrantsXxx` / `UnsupportedReason` / `ImmediatePowerNote`）。
- 数值读取一律**按变量类型匹配**（`BlockVar`、`PowerVar<PoisonPower>`、`CardsVar`、`RepeatVar`…），字符串键只做兜底：游戏对 Power 变量命名为 `XxxPower`（`WeakPower`、`PoisonPower`），但键名不保证稳定。
- 伤害优先取**游戏自己维护的卡面预览值**（`DynamicVar.PreviewValue`，已含力量/虚弱/易伤）。**不要**自己调 `UpdateCardPreview` 覆盖它——实测会把虚弱等修正抹掉，并且会动到卡面显示。
- 需要在搜索里推进的状态（能量、敏捷、易伤、中毒、弃牌触发、X 费、手牌免费等）加到 `SearchState`，并在 `SearchContext.PlayCard` 里推进；只影响"这张牌自己"的值放 `CardEffect`。
- 能力牌按 `ImmediatePowerNote` 分类：**本回合会生效的**（精准/余像/涂毒/腐蚀波/群蛇形态/速行者/融入暗影/幻影之刃）与**纯跨回合的**（无尽刀刃/毒雾/计划妥当/残影/幽魂形态/必备工具）要写清楚，后者写"本回合无即时收益"。

## 2.5 顺序敏感的牌（容易算错的一类）

描述里出现"本回合每…就…一次 / 手牌中每有一张… / 当前…总和"的牌，收益取决于**出牌顺序**：
`终结技`（已打出攻击牌数）、`飞镖`（手牌技能牌数）、`蜃景`（敌人中毒总和）、`铭记死亡`（本回合弃牌数）。
这类牌**不能在 Analyze 阶段算死**（那时状态还没推进），要用 `ScalingKind` 标记后
在 `SearchContext.PlayCard` 里按当时的搜索状态计算，否则顺序优化失效、永远只算最小值。

## 2.6 变量读得到，但语义和变量名不一致（v0.9.7 踩到的三类）

**a. 数字写在描述文本里、根本没有变量** —— 只按变量读就会漏：
`匕首雨`（"造成 4 点伤害**两次**"，没有 `Repeat`）、`逃脱计划`（"抽 1 张"，没有 `Cards`）。
这类要进 `HardcodedHits` / `FixedDraw` 这类逐张表，**别指望变量**。

**b. 变量名一样，生效回合不一样** —— 读对了数字也算错：
`侧步` 的 `Energy=1` 是**下回合**的能量，`肾上腺素`/`战术大师` 是本回合的。
凡是"下回合/下个回合"开头的描述，一律不能进本回合的 `SearchState`。

**c. 机制藏在关键词或描述第二句里** —— 只读数值会漏掉整段效果：
`暴露`（"去除敌人身上所有格挡值和人工制品"）、`刀扇`（"小刀现在会攻击所有敌人"）、
`消影`（`target=AnyAlly`，收益给队友）。
判定顺序：先看 `TargetType`，再把描述**整句**读完，最后才看 `vars`。

**d. 别忘了游戏已经算过的部分** —— 重复计算比漏算更隐蔽：
敌人意图的 `GetSingleDamage` **已含它当前的虚弱**，所以搜索里只应给"本回合新上的虚弱"
再乘一次减伤（见 `SimEnemy.WeakThisTurn`）；同理卡面 `PreviewValue` 已含力量/虚弱/易伤。

**e. 用游戏自己的"计算型变量"当基准** —— 需要"本回合已发生过的计数"时，
优先读卡面预览里的 `CalculatedHits`（终结技"（攻击 N 次）"）这类游戏自己维护的值，
比在 `Solve` 里从 0 数起准确（面板每次手牌变化都会重算，自己数会漏掉本次重算之前的出牌）。

## 3. 验证

1. 编译：`dotnet build DamageAdvisor.csproj -c Release -p:GameDir="<游戏目录>"`（0 warning / 0 error）。
2. 进战斗后按 `F9` 注入该牌（`TestCards` 列表在 `AdvisorRoot.cs`），看日志：
   - 解析行：`牌 <中文名> cost=.. dmg=.. block=.. poison=.. draw=.. shivs=.. ok=.. note=.. vars=[...]`
   - 数值必须与 `vars=` 里游戏自己的数字一致；
   - 打出后看计划行的伤害/格挡是否随之变化，以及是否出现在推荐里（说明它参与了搜索）。
3. **跑一遍自动比对**：`python tools/verify-log.py`（报告写进 `docs/verify-report.txt`，非零退出码= 有不一致）。
   它把日志里每条牌行的面板值与同一行 `vars=` 对照，能抓"读错变量/漏读抽牌/字段映射错"这类。
   ⚠️ **它抓不到"数字只写在描述里"的错误**（匕首雨"两次"、逃脱计划"抽1张"）——那类只能靠人工读 `desc`。
   ⚠️ **"没报不一致"≠"验过了"**：只覆盖日志里出现过的牌，没抽到的牌它一句话都不会说。
4. 一次只验证一类机制，改动可回滚。无法实机验证时，在 `STATUS.md` 里写明"待实机验证"，不要声称已验证。

## 4. 边界

- 不要为未确认的牌写 `catch` 兜底成 0 就当作"已支持"——那会让面板显示误导性建议。
- 不要引入 CombatSolver / RandomForeseer 的代码或运行时依赖。
- 新增字段要同步 `DescribeVars` 级别的可观测性，否则出问题无法定位。

