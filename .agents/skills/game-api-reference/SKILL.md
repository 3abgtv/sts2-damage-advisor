---
name: game-api-reference
description: 需要访问《杀戮尖塔 2》游戏内部 API（战斗状态、卡牌、意图、本地化、卡池、往手牌加牌）时使用，汇总本项目已实测可用的类型与方法，避免重新反编译摸索。不适用于纯项目内部逻辑。
---

# 游戏 API 速查（实测可用）

程序集来源：`<游戏目录>/data_sts2_windows_x86_64/sts2.dll`（与本项目一起编译，`<Private>false</Private>`）。
反编译工具：本项目不依赖，可用 `ildasm`/`ILSpy`；元数据速查可自建 `System.Reflection.Metadata` 小工具按类型名过滤。

## 战斗状态

```csharp
CombatManager? m = CombatManager.Instance;         // 可能为空
if (m is null || !m.IsInProgress) return;
CombatState? state = m.DebugOnlyGetState();        // 战斗状态（空则不在战斗）
state.CurrentSide      // CombatSide.Player / Enemy
state.Players          // IReadOnlyList<Player>
state.Enemies          // IReadOnlyList<Creature>（含已死，按屏幕顺序 → 编号 1..N）
state.PlayerCreatures  // IReadOnlyList<Creature>
Player? me = LocalContext.GetMe(state);
```

## 玩家 / 手牌 / 能量

```csharp
PlayerCombatState pcs = me.PlayerCombatState;
pcs.Hand.Cards         // IReadOnlyList<CardModel>
pcs.DrawPile.Cards     // 抽牌顺序（用于模拟抽牌）
pcs.DiscardPile.Cards
pcs.Energy / pcs.MaxEnergy / pcs.Phase (PlayerTurnPhase.Play) / pcs.TurnNumber
Creature c = me.Creature;  c.CurrentHp / c.MaxHp / c.Block / c.IsAlive / c.Name
```

## 卡牌

```csharp
card.GetType().Name    // 类名（语义分发用：BladeDance / Acrobatics …）
card.Id                // ModelId，ToString() 形如 CARD.BLADE_DANCE
card.Type              // CardType.Attack / Skill / Power / Status / Curse
card.TargetType        // TargetType.AnyEnemy / AllEnemies / Self / RandomEnemy …
foreach (KeyValuePair<string, DynamicVar> kv in card.DynamicVars) { kv.Key; kv.Value.BaseValue; kv.Value.PreviewValue; }
card.DynamicVars.TryGetValue("Damage", out DynamicVar v)
```

### 费用（CardEnergyCost）

```csharp
card.EnergyCost?.Canonical        // 基础费用——**不含任何修正**
card.EnergyCost?.CostsX           // 是否 X 费
card.EnergyCost?.GetResolved()    // ✅ 当前费用：含全部修正（精密瞄准每张技能 -1、遗物、其它牌的效果）
card.EnergyCost?.HasLocalModifiers// 这张牌的费用是否被本地修正过
```

- **要"这张牌现在花多少能量"就用 `GetResolved()`**，用 `Canonical` 会漏掉所有费用修正（X 费仍走 `Canonical` + `CostsX` 单独处理）。
  `GetWithModifiers(CostModifiers)` 需要自己传修正集合，一般用不上。
- **伤害取 `PreviewValue`**（游戏自己维护的卡面数字，含力量/虚弱/易伤）。`UpdateCardPreview(...)` 会覆盖它，别调。
- 变量类型匹配优先：`BlockVar` / `PowerVar<PoisonPower>` / `PowerVar<WeakPower>` / `CardsVar` / `RepeatVar` / `EnergyVar`。
- 卡名：`card.Description.LocTable` + `LocEntryKey`（形如 `X.description`）→ 用 `LocString.Exists/GetIfExists(table, "X.title")` 取本地化名。

## 敌人意图

```csharp
MonsterModel? mon = enemy.Monster;
IReadOnlyList<AbstractIntent> intents = mon.NextMove?.Intents ?? Array.Empty<AbstractIntent>();
if (intent is AttackIntent atk) { int single = atk.GetSingleDamage(allies, enemy); int reps = Math.Max(1, atk.Repeats); }
```

`GetSingleDamage` 已含力量/虚弱修正。

## 往手牌加牌（测试用，见 F9）

```csharp
CardModel instance = state.CreateCard(model, me);
await CardPileCmd.AddGeneratedCardToCombat(instance, PileType.Hand, me, CardPilePosition.Top);
// 手牌上限 10：满时先 await CardPileCmd.Add(last, PileType.Discard, CardPilePosition.Top, null, false);
```

## 卡池与规范模型

```csharp
CardPoolModel pool = ModelDb.CardPool<SilentCardPool>();       // ⚠ 初始化时不可用，需延后到首次战斗
foreach (CardModel c in pool.AllCards) { ... }
Shiv canonical = ModelDb.Card<Shiv>();
foreach (CardModel c in ModelDb.AllCards) { if (c.GetType().Name == "BladeDance") ... }
```

## Mod 加载

```csharp
[ModInitializer("Initialize")]   // 游戏原生入口；不需要 RitsuLib
public static class Entry { public static void Initialize() { ... } }
```

清单 `DamageAdvisor.json`：`id / name / author / version / has_pck / has_dll / affects_gameplay / min_game_version / dependencies`。
`affects_gameplay: false` 表示非玩法改动（联机 Mod 匹配更友好）。

## 内置开发者控制台（可选）

`MegaCrit.Sts2.Core.DevConsole.DevConsole` 提供 `card / draw / energy / fight / room / block / applypower` 等命令，可通过 `ProcessCommand(string)` 调用（本项目未依赖它，仅测试时可用）。

## 注意

- 直接运行 `SlayTheSpire2.exe` 会因缺 appID 导致 Steam 初始化失败（弹错误框、NGame 走不完启动流程）——**必须通过 Steam 启动**。
- `ModManager` 会校验联机 Mod 一致性；只读 Mod 仍建议与其他玩家保持一致。
