---
name: silent-coverage-audit
description: 需要盘点"猎人还有哪些牌没被正确建模"时使用——用游戏卡池转储与代码里的语义表做差异对比，产出未覆盖清单并排优先级。不适用于单张牌的语义实现细节。
---

# 猎人覆盖率审计（DamageAdvisor）

## 目标

回答两个问题：**猎人 91 张牌里，哪些没被建模？哪些建模了但没在实机验证过？**

## 1. 取权威清单

进战斗后从日志提取卡池转储（也可直接看本机 `docs/silent-pool.txt`，该文件不入库）：

```powershell
Get-Content "…\mods\DamageAdvisor\DamageAdvisor.log" |
  Select-String -Pattern '卡池 ' |
  ForEach-Object { ($_.Line -replace '^.*\[DamageAdvisor\] 卡池 ','') } |
  Set-Content "…\moddev\DamageAdvisor\docs\silent-pool.txt" -Encoding UTF8
```

每行含：类名、`id`、中文名、`type`、`cost`、`target`、`kw`（关键字）、`desc`（描述原文）、`vars`（全部动态变量）。

## 2. 与代码对照

1. `src/SilentLogic.cs` 是语义判定的唯一入口，检查：
   - `CardsMeaning`（`Cards=` 到底是抽牌还是生成小刀）；
   - `FixedDiscard` / `DiscardsCardsVar`（弃牌数）；
   - `GrantsXxx`（各机制开关）；
   - `UnsupportedReason`（明确不建模的牌与原因）；
   - `ImmediatePowerNote`（本回合会生效的能力牌）。
2. `src/DamageModel.cs` 的 `Analyze` 是把上面判定翻译成 `CardEffect` 的地方，检查每个 `GrantsXxx` 是否真的接到了字段，字段是否在 `PlayCard` 里被消费。
3. **常见缺口**：
   - 变量名不在预期位置（例如易伤在 `Power`、计算型伤害在 `CalculatedDamage`）；
   - 需要跨回合信息的牌（夜魇复制、残影保留、毒雾回合开始）——应标注"本回合无即时收益"，而不是算成 0；
   - 需要消耗堆/整手操作的牌（刀刃陷阱、钢铁风暴、计算下注）。

## 3. 输出

产出一张表（写进 `STATUS.md` 或 issue）：

| 牌 | 类型 | 建模状态 | 验证状态 | 备注 |
|---|---|---|---|---|
| 刀刃之舞 | Skill | ✅ 生成小刀×N | ✅ 实机 | `Cards=` 重载已处理 |
| 刀刃陷阱 | Skill | ❌ 未建模 | — | 需要消耗堆小刀连击 |

优先级建议按：**使用频率高的基础牌（打击/防御/毒/小刀类）> 影响斩杀判断的牌 > 影响保命的牌 > 长尾**。
"验证状态"只有实机日志证据才可标 ✅，否则标"待验证"。

## 4. 边界

- 不要为了凑覆盖率把不确定的牌硬算成某个数字——宁可标"未建模"。
- 转储文件只作本机参考，不要提交（含游戏内文本，见 `.gitignore`）。
