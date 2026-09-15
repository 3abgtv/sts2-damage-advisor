---
name: log-triage
description: 需要根据 DamageAdvisor.log 判断"这条推荐为什么是这样 / 哪里算错了"时使用，说明日志格式与逐项核对方法。不适用于纯代码阅读或发布流程。
---

# 日志定位（DamageAdvisor）

日志文件：`<游戏目录>/mods/DamageAdvisor/DamageAdvisor.log`（用户目录另存一份兜底）。

## 1. 日志构成

```
初始化开始 vX.Y.Z / 已挂钩 NGame._Ready / 覆盖层已挂载到 NGame，子节点数=.. / UI 已构建 / 已连接 SceneTree.ProcessFrame 信号
手牌快照#N turn=.. energy=.. hand=.. draw=.. enemies=.. hp=.. block=.. incoming=.. nodes=..
  牌 <中文名> cost=.. dmg=.. all=.. block=.. poison=.. draw=.. shivs=.. ok=.. note=.. vars=[原始变量]
  敌 <编号>.<名字> hp=.. incoming=.. | move=<招式id> intents=[Attack[single=..,repeats=..]|BuffIntent|...]
  计划 <动作序列，带目标编号> 伤害=.. 格挡+=.. 掉血=.. 致命=..
注入测试牌 #N <类名> → <中文名> vars=[...]        （F9）
面板显示=True/False · 面板折叠=... · 允许掉血上限=N （按键）
```

手牌一变就打一次快照（每场 ≤25 次），因此"抽到某张牌"一定能在日志里找到。

## 2. 逐项核对流程

1. **卡的数值对不对**：把 `牌 ... dmg=/block=/poison=/shivs=` 与同一行的 `vars=[...]` 对照。
   例如 `dmg=6` 而 `Damage=6/4.5` → 说明游戏自己的预览是 4.5（玩家虚弱），此时应取 4.5 向下取整 = 4，若显示 6 则是取数错误。
2. **来袭伤害对不对**：看 `敌 ... intents=[Attack[single=..,repeats=..]]`，`incoming` 应等于 `single × repeats`（再按虚弱/力量削减调整）。
   `BuffIntent`/`DefendIntent` 等非攻击意图 → `incoming=0` 是正确的。
3. **推荐是否自洽**：按动作序列手算：
   - 能量：Σ 费用 − Σ 能量收益 ≤ 当时能量；
   - 格挡：`block=..` + 已有格挡 ≥ 来袭 → `掉血=0`；
   - 伤害：Σ 每张牌伤害（注意毒杀：`DiesToPoison` 会把该怪从来袭里剔除）；
   - 击杀：`伤害(+中毒) ≥ 敌人血量` 时 `Kills` 应 ≥1，日志里会体现在"掉血=0"或 `✅ 击杀`。
4. **搜索是否被截断**：`nodes=` 撞上 `MaxNodes`（80000）说明剪枝失效（同名卡去重是否生效？分支是否爆炸）。

## 3. 常见症状 → 可能原因

| 症状 | 排查方向 |
|---|---|
| 面板一直"未在战斗中" | 是否在敌人回合/选牌阶段；`Refresh()` 只在 `CurrentSide==Player && Phase==Play` 时才输出快照 |
| 某张牌 dmg=0 但显然是攻击牌 | 伤害变量不在 `Damage`（可能是 `CalculatedDamage`）；或变量类型没匹配上 |
| 推荐明显低估 | 取数用了 `BaseValue` 而非 `PreviewValue`；或牌被标成 `ok=False` |
| 推荐明显虚高 | 漏算虚弱/力量削减；或 `ShieldsAll`（全体伤害）按敌人数乘算错误 |
| 计划里出现手里没有的牌 | 正常：可能是抽牌/生成小刀/弃牌触发（本能反应）带来的 |
| 注入失败 | 手牌满 10 张（已处理）、不在战斗、类名不在 `ModelDb.AllCards` |

## 4. 边界

日志里的 `vars=[...]` 是**游戏自己的数字**，出现分歧时以它为准。修改代码前先确认分歧发生在"取数"还是"建模"环节。
