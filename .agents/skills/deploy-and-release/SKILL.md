---
name: deploy-and-release
description: 构建、部署到游戏、实机验证与发布 GitHub Release 时使用，含 DLL 占用、必须经 Steam 启动、版本号与打包约定。不适用于战斗语义或 UI 逻辑本身。
---

# 构建 / 部署 / 发布（DamageAdvisor）

## 1. 开发循环

```powershell
# 1) 关游戏（运行中的游戏会锁住 DLL，复制会失败）
Stop-Process -Name SlayTheSpire2 -Force

# 2) 构建 + 部署（脚本会复制 dll/json 到 mods\DamageAdvisor\）
pwsh -File "…\moddev\DamageAdvisor\build.ps1"

# 3) 必须经 Steam 启动（直接跑 exe 会因缺 appID 导致 Steam 初始化失败）
Start-Process "steam://rungameid/2868840"

# 4) 看日志
Get-Content "<游戏目录>\mods\DamageAdvisor\DamageAdvisor.log"
```

- 只做编译校验、不部署时：`dotnet build DamageAdvisor.csproj -c Release -p:GameDir="<游戏目录>"`。
- 环境若无法读取用户级 NuGet 配置，构建脚本里已把 `APPDATA` / `DOTNET_CLI_HOME` / `NUGET_PACKAGES` 指向临时目录。
- **重启游戏会退出当前这局**（回到主菜单），所以攒几处改动再一起部署，避免反复打断使用者。

## 2. 实机验证要点

- 面板出现且显示"伤害计算"内容 → 说明挂载与刷新都正常。
- 改动语义后按 `F9` 注入对应牌，核对日志（见 `log-triage`）。
- 改动 UI/按键后，逐个试 `F7/F8/F9/F10` 与拖动，并确认**折叠时按键提示仍可见**。
- 无法实机验证时，在 `STATUS.md` 写"待实机验证"，不要声称已验证。

## 3. 版本与发布

1. 同步两处版本号：
   - `DamageAdvisor.json` 的 `version`
   - `src/Entry.cs` 的 `Version` 常量
2. 提交：`git add -A && git commit -m "feat: ..."`（仓库根即本项目目录）。
3. 打包（只含安装所需文件）：

```powershell
$stage="$env:TEMP\da-release-<stamp>\DamageAdvisor"
Copy-Item bin\Release\net9.0\DamageAdvisor.dll $stage
Copy-Item DamageAdvisor.json, README.md $stage
Compress-Archive -Path "$stage\*" -DestinationPath "$env:TEMP\da-release-<stamp>\DamageAdvisor-<ver>.zip"
```

   注意：**不要用递归删除清理临时目录**（会被安全策略拒绝），改用带时间戳的新目录。
4. 发布：`gh release create v<ver> <zip> --title "..." --notes "..."` —— `gh` 必须在仓库目录内执行。

## 4. 边界

- 不要提交 `bin/`、`obj/`、`shots/`、`*.log`、`*.cfg`、`docs/silent-pool.txt`（见 `.gitignore`；卡池转储含游戏内文本，不随仓库分发）。
- 发布说明里如实写明只读特性、已知限制，以及"语义参考了 CombatSolver / RandomForeseer 的公开实现但未复制代码"。
- 许可证：本项目尚未指定许可证，新增 `LICENSE` 属于需要使用者确认的决定。
