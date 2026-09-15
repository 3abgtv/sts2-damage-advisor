# 构建 DamageAdvisor 并把产物复制到游戏 mods 目录
param(
    [string]$Configuration = "Release",
    [string]$GameDir = "D:\SteamLibrary\steamapps\common\Slay the Spire 2"
)

$ErrorActionPreference = "Stop"

# 本机 .NET 的用户级 NuGet 配置在沙箱外不可读，这里把 APPDATA 指到临时目录绕开它
$env:APPDATA = Join-Path $env:TEMP "appdata"
$env:DOTNET_CLI_HOME = Join-Path $env:TEMP "dotnethome"
$env:NUGET_PACKAGES = Join-Path $env:TEMP "nugetpkgs"
New-Item -ItemType Directory -Force -Path $env:APPDATA | Out-Null

$project = Join-Path $PSScriptRoot "DamageAdvisor.csproj"
$target = Join-Path $GameDir "mods\DamageAdvisor"

dotnet build $project -c $Configuration -p:GameDir="$GameDir" --nologo
if ($LASTEXITCODE -ne 0) { throw "构建失败" }

New-Item -ItemType Directory -Force -Path $target | Out-Null
Copy-Item (Join-Path $PSScriptRoot "bin\$Configuration\net9.0\DamageAdvisor.dll") $target -Force
Copy-Item (Join-Path $PSScriptRoot "DamageAdvisor.json") $target -Force

Write-Host "已部署到 $target"
Get-ChildItem $target | Select-Object Name, Length
