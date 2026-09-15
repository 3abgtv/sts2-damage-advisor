# Agent Skills

本目录是本项目给 AI 编码代理使用的 skills（Codex 风格：`SKILL.md` = YAML frontmatter + 正文）。
每个 skill 只负责一类改动，避免一次改太多东西导致无法验证。

| Skill | 什么时候用 |
|---|---|
| `card-semantics-change` | 新增/修改一张牌的战斗语义（先取权威语义 → 接进状态机 → 实机验证） |
| `panel-ui-change` | 改面板显示、按键、拖动、折叠（含 Godot 挂载与刷新约束） |
| `recommendation-objective` | 改推荐目标与评分（保命优先 / 掉血预算 / 击杀优先 / 最大伤害） |
| `game-api-reference` | 需要访问游戏内部 API 时的速查（战斗状态、卡牌、意图、卡池、加牌） |
| `deploy-and-release` | 构建、部署到游戏、实机验证与发 Release |
| `log-triage` | 从 `DamageAdvisor.log` 判断一条推荐为什么是这样 |
| `silent-coverage-audit` | 盘点猎人还有哪些牌没建模 / 没验证 |

## 约定

- **算不准就标出来**：未建模的牌必须在面板与日志里写明原因，不允许给出误导性建议。
- **一次一类改动**：语义、UI、评分分开改，分开验证。
- **实机证据**：只有实机日志能证明"已验证"；否则写"待验证"。
- 语义参考自 CombatSolver / RandomForeseer 的公开实现，**不复制其代码**。

## 相关文档

- `README.md`：对外说明（安装、按键、推荐逻辑、限制）
- `STATUS.md`：开发进度、已验证项、待办与踩坑记录
