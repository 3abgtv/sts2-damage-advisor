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

## 4. 创意工坊发布

AppID **2868840**；官方上传器 [megacrit/sts2-mod-uploader](https://github.com/megacrit/sts2-mod-uploader)（`ModUploader.exe`，Release 里下 `ModUploader-win-x64.zip`）。

工作区就是仓库里的 `workshop/`（`content/` 已 gitignore，其余文件进版本控制）：

```
workshop/
├─ workshop.json   标题/描述/可见性/tags —— 描述就是工坊页面正文
├─ image.png       封面，必须 <1MB
├─ previews/       可选附加截图，按文件名对比，缺图会从后端删除
├─ mod_id.txt      首次上传后由工具生成，**必须保留**（丢了下次会新建一个物品）
└─ content/        build.ps1 -Workshop 填充：DLL + json + LICENSE
```

1. `pwsh -File build.ps1 -Workshop`
   编译时带 `-p:Workshop=true`（编译期剔除 F9 注入与卡池转储），然后**校验产物身份**
   （无注入/转储代码 + 含 `<版本>-ws`），再填充 `workshop/content/`，最后打印上传命令。
2. 检查 `workshop/workshop.json` 的 `visibility`（首次建议 `unlisted`）。
3. 在 `ModUploader.exe` 所在目录执行：
   `ModUploader.exe upload -w "<仓库>\workshop"`
   首次会创建物品并生成 `mod_id.txt`；之后同一条命令就是更新（可先填 `changeNote`）。
4. 出问题看同目录的 `mod-uploader.log`（官方要求报问题时附上它）。

注意事项：

- **两个渠道产物不同是有意的**：GitHub Release 发开发版（含 F9），工坊发无 F9 版；
  工坊版的面板标题与启动日志里版本号带 `-ws`，一眼可辨。手拷文件时别把开发版 DLL 放进 `content/`
  （`build.ps1 -Workshop` 会拦，手拷拦不住）。
- 作者账号**通常订阅不了自己的物品**，所以"装到本地能否加载"要用**第二个 Steam 账号**订阅，
  或手动把 `content/` 拷到 `steamapps/workshop/content/2868840/<itemid>/` 模拟。
- 可见性（`private`/`unlisted`/`friends_only`/`public`）随时能在网页端改，前三种都不进搜索结果。
- ⚠️ **`workshop.json` 的 `visibility` 实测在首次上传时不一定生效**：设为 `unlisted` 上传后，
  非作者用直链仍看不到（`GetPublishedFileDetails` 也查不到该物品），重传一次也没变。
  所以**首次上传后必须去网页端确认并修改可见性**（物品页 → 修改物品 → 可见性）。
  自测方法：用**未登录**的浏览器窗口打开物品链接——未登录也能看 = unlisted 已生效；
  提示找不到 = 还是 private / friends_only。
  （另：`steamcommunity.com` 在国内常被墙，页面"完全打不开/转圈"是网络问题，与可见性无关，需要代理。）
- `minBranch` / `maxBranch` 官方说行为怪异，留空（= 支持所有分支），尽量在网页端维护。
- 描述是玩家唯一的说明来源（`content/` 里不放 README），必须写清按键、已知限制与验证状态。
- 工坊版的 `affects_gameplay: false` 只有在**编译期剔除**注入功能后才成立，不能靠"默认关闭"。

## 5. 边界

- 不要提交 `bin/`、`obj/`、`shots/`、`*.log`、`*.cfg`、`docs/silent-pool.txt`、`workshop/content/`（见 `.gitignore`）。
- 发布说明里如实写明只读特性、已知限制，以及"语义参考了 CombatSolver / RandomForeseer 的公开实现但未复制代码"。
- 许可证：**MIT**（仓库根 `LICENSE`）；工坊包的 `content/` 里随附一份，满足 MIT 的"随副本附带声明"要求。
- `.ps1` 脚本必须存成**带 BOM 的 UTF-8**：Windows PowerShell 5.1 按 ANSI 读无 BOM 文件，中文会把引号吃掉导致语法错误。
  同理，脚本里读 `DamageAdvisor.json` 要用 `[System.IO.File]::ReadAllText($p, [Text.Encoding]::UTF8)`，
  别用 `Get-Content -Raw`（PS 5.1 默认 ANSI，json 会读坏、`ConvertFrom-Json` 抛异常）。
