# 构建 DamageAdvisor
#   默认      ：开发版（含 F9 注入测试牌与卡池转储）→ 复制到游戏 mods\DamageAdvisor
#   -Workshop ：工坊版（编译期剔除上述测试功能）→ 填充 workshop\content，并打印上传命令
param(
    [string]$Configuration = "Release",
    [string]$GameDir = "D:\SteamLibrary\steamapps\common\Slay the Spire 2",
    [switch]$Workshop
)

$ErrorActionPreference = "Stop"

# 本机 .NET 的用户级 NuGet 配置在沙箱外不可读，这里把 APPDATA 指到临时目录绕开它
$env:APPDATA = Join-Path $env:TEMP "appdata"
$env:DOTNET_CLI_HOME = Join-Path $env:TEMP "dotnethome"
$env:NUGET_PACKAGES = Join-Path $env:TEMP "nugetpkgs"
New-Item -ItemType Directory -Force -Path $env:APPDATA | Out-Null

$project = Join-Path $PSScriptRoot "DamageAdvisor.csproj"
$builtDll = Join-Path $PSScriptRoot "bin\$Configuration\net9.0\DamageAdvisor.dll"
$manifestPath = Join-Path $PSScriptRoot "DamageAdvisor.json"
# 显式按 UTF-8 读：PS 5.1 的 Get-Content 默认走 ANSI，会把中文 json 读坏导致 ConvertFrom-Json 抛异常
$manifest = [System.IO.File]::ReadAllText($manifestPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json

Write-Host "构建 $(if ($Workshop) { "工坊版" } else { "开发版" }) v$($manifest.version) ..."

$buildArgs = @($project, "-c", $Configuration, "-p:GameDir=$GameDir", "--nologo")
if ($Workshop) { $buildArgs += "-p:Workshop=true" }
dotnet build @buildArgs
if ($LASTEXITCODE -ne 0) { throw "构建失败" }

# ---- 校验产物身份 ----
# .NET 的方法名在元数据里是 ASCII，字符串字面量在 #US 堆里是 UTF-16LE，两种都查
$bytes = [System.IO.File]::ReadAllBytes($builtDll)
$ascii = [System.Text.Encoding]::ASCII.GetString($bytes)
$utf16 = [System.Text.Encoding]::Unicode.GetString($bytes)
$hasInject = $ascii.Contains("InjectNextTestCard") -or $ascii.Contains("TestCards")
$hasDump = $ascii.Contains("DumpSilentPool")
# 克隆探测与影子状态（engine/rewrite 阶段）同理：只在开发版里，工坊版不许带
$hasProbe = $ascii.Contains("CloneProbe") -or $ascii.Contains("SimCommands") -or $ascii.Contains("SimState")
$wsVersion = "$($manifest.version)-ws"
$hasWsTag = $utf16.Contains($wsVersion)

if ($Workshop) {
    # 绝不能把带测试功能的 DLL 传到工坊：文件里必须没有注入/转储代码，且带 -ws 版本串
    if ($hasInject) { throw "校验失败：DLL 里仍有 F9 注入代码，拒绝填充 content/" }
    if ($hasDump)   { throw "校验失败：DLL 里仍有卡池转储代码，拒绝填充 content/" }
    if ($hasProbe)  { throw "校验失败：DLL 里仍有克隆探测代码，拒绝填充 content/" }
    if (-not $hasWsTag) { throw "校验失败：DLL 里找不到版本串 $wsVersion（可能没真正带上 -p:Workshop=true）" }

    $content = Join-Path $PSScriptRoot "workshop\content"
    New-Item -ItemType Directory -Force -Path $content | Out-Null
    Copy-Item $builtDll $content -Force
    Copy-Item $manifestPath $content -Force
    Copy-Item (Join-Path $PSScriptRoot "LICENSE") $content -Force

    Write-Host "工坊版已填充（$wsVersion，无注入/转储）：$content"
    Get-ChildItem $content | Select-Object Name, Length
    Write-Host ""
    Write-Host "确认 workshop\workshop.json 的可见性后，在 ModUploader.exe 所在目录执行："
    Write-Host "    ModUploader.exe upload -w `"$PSScriptRoot\workshop`""
    return
}

# 开发版反查：万一增量构建没重编、把工坊版 DLL 部署进 mods\，这里要拦住
if (-not $hasInject) {
    throw "校验失败：开发版 DLL 里没有 F9 注入代码——增量构建可能没重编，请 dotnet clean 后重试"
}

$target = Join-Path $GameDir "mods\DamageAdvisor"
New-Item -ItemType Directory -Force -Path $target | Out-Null
Copy-Item $builtDll $target -Force
Copy-Item $manifestPath $target -Force

Write-Host "已部署开发版 v$($manifest.version)（含 F9 注入）到 $target"
Get-ChildItem $target | Select-Object Name, Length
