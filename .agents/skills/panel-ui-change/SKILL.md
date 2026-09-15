---
name: panel-ui-change
description: 修改 DamageAdvisor 战斗面板（Godot UI）时使用——挂载方式、刷新驱动、按键、拖动与折叠。适用于显示层改动，不适用于战斗语义或评分逻辑。
---

# 面板 UI 变更（DamageAdvisor）

## 适用边界

只改"怎么显示/怎么交互"。语义改动走 `card-semantics-change`，评分改动走 `recommendation-objective`。

## 1. 本项目的 Godot 约束（踩过的坑，别重犯）

- **UI 必须挂到游戏根节点 `NGame.Instance`**。挂 `SceneTree.Root` + `CallDeferred` 会静默落空：节点不在场景树里，`_Ready` 永远不执行。
- **动态加载的 Mod 程序集不会收到 Godot 虚函数回调**：实测 `_Ready` / `_Process` / `_GuiInput` 都不触发。因此：
  - UI 由入口显式调用 `AdvisorRoot.EnsureStarted()` 构建；
  - 每帧刷新由 `SceneTree.ProcessFrame` 信号驱动（`TickFromSignal`）；
  - 鼠标拖拽用轮询（`Input.IsMouseButtonPressed` + `GetViewport().GetMousePosition()`）实现，不要指望 `_GuiInput`。
- 节点窗口坐标：用 `Control.GlobalPosition` / `GetRect()`；`Input.GetMousePosition()` 在该 Godot 版本不存在。

## 2. 现有结构

- `src/AdvisorRoot.cs`：面板本体。`BuildUi()` 创建 `PanelContainer` + `VBoxContainer` + 若干 `Label`；`Refresh()` 每 0.25s 计算并刷新文本；`Tick*` 系列处理按键与拖拽。
- 文本分三层：`_header`（回合/能量/预算）、`_status`（单人/联机、HP、格挡）、`_enemyLine`（敌人与来袭）、`_handLines`（每张手牌）、`_planLines`（推荐）、`_killLine`（斩杀）、`_footer`（限制说明）、`_hintLine`（按键提示）。
- `AdvisorSettings` 持久化"掉血上限"与"折叠"到 `mods/DamageAdvisor/DamageAdvisor.cfg`。

## 3. 按键约定

| 键 | 作用 |
|---|---|
| F7 | 循环掉血上限（0/1/2/3/5） |
| F8 | 显示/隐藏 |
| F9 | 注入测试牌（仅测试，战斗结束消失） |
| F10 | 折叠/展开 |
| 拖动 | 面板任意位置按住拖动 |

新增按键时：使用 `Input.IsKeyPressed` 边沿检测（`_previousXxxKey`），并把提示加到 `_hintLine`（**折叠时也要保留提示行**，否则用户忘了怎么展开）。

## 4. 显示原则

- 数字要和游戏一致：血量/格挡/能源取实时值，卡名走本地化表（`LocString.Exists/GetIfExists`，失败退回 Id）。
- 未建模/不支持的牌要**明确标注原因**，不要静默显示 0。
- 日志与面板同步：首次刷新、手牌变化、面板折叠/隐藏、注入测试牌都要写日志（`Entry.Log`），否则出问题无法离线定位。

## 5. 验证

1. 编译 0 error。
2. 实机：面板出现、位置正确、F7/F8/F10 与拖动可用、折叠后仍能看到推荐与按键提示。
3. 改动显示层后，确认日志里 `手牌快照` 的字段仍然完整（不要把 `DescribeVars` 等诊断删掉）。
