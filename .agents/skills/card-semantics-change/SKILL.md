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

## 3. 验证

1. 编译：`dotnet build DamageAdvisor.csproj -c Release -p:GameDir="<游戏目录>"`（0 warning / 0 error）。
2. 进战斗后按 `F9` 注入该牌（`TestCards` 列表在 `AdvisorRoot.cs`），看日志：
   - 解析行：`牌 <中文名> cost=.. dmg=.. block=.. poison=.. draw=.. shivs=.. ok=.. note=.. vars=[...]`
   - 数值必须与 `vars=` 里游戏自己的数字一致；
   - 打出后看计划行的伤害/格挡是否随之变化，以及是否出现在推荐里（说明它参与了搜索）。
3. 一次只验证一类机制，改动可回滚。无法实机验证时，在 `STATUS.md` 里写明"待实机验证"，不要声称已验证。

## 4. 边界

- 不要为未确认的牌写 `catch` 兜底成 0 就当作"已支持"——那会让面板显示误导性建议。
- 不要引入 CombatSolver / RandomForeseer 的代码或运行时依赖。
- 新增字段要同步 `DescribeVars` 级别的可观测性，否则出问题无法定位。
