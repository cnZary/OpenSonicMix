# SonicCubePanel 完整编译脚本
#
# 默认编译为框架依赖(framework-dependent)版本, 体积小, 目标机器需装 .NET 10 运行时:
#   1. 还原 + 构建整个解决方案 (Release)
#   2. 运行协议、IPC 与 WPF 电平动画回归测试
#   3. 发布 SonicCube.Tray   (框架依赖单文件, win-x64)
#   4. 发布 SonicCube.Panel  (框架依赖单文件, win-x64)
#   5. 构建 zh-CN 卫星程序集
#   6. 收集产物到 publish\  (SonicCube.Tray.exe / SonicCube.Panel.exe / zh-CN\)
#
# 加 -Static 后改为静态(self-contained)版本, 无需目标机器装运行时, 但体积大很多:
#   - Tray 默认用 NativeAOT (~3MB), 加 -SkipAot 可退回普通 self-contained 单文件
#   - Panel 用 self-contained 单文件 (~130MB)
#
# 用法:
#   .\build.ps1                      框架依赖编译(默认, 小体积)
#   .\build.ps1 -Static              静态编译(自包含, 大体积)
#   .\build.ps1 -Static -SkipAot     静态编译, Tray 不用 NativeAOT
#   .\build.ps1 -SkipTests           跳过单元测试
#   .\build.ps1 -NoClean             不清空 publish\ 目录,直接覆盖产物文件
#   .\build.ps1 -Configuration Debug 使用其他构建配置
#
# 参数可任意组合,例如:  .\build.ps1 -Static -SkipTests

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$Static,
    [switch]$SkipTests,
    [switch]$SkipAot,
    [switch]$NoClean
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Write-Step([string]$Message) {
    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function Get-SecurePath([string]$Path) {
    try {
        $resolved = (Get-Item -LiteralPath $Path -Force -ErrorAction Stop).FullName
        return $resolved
    } catch {
        return $Path
    }
}

function Get-SourceExeHash([string]$ExePath) {
    if (Test-Path -LiteralPath $ExePath) {
        return (Get-FileHash -LiteralPath $ExePath -Algorithm SHA256).Hash
    }
    return $null
}

function Invoke-Dotnet([string[]]$Arguments, [string]$What) {
    Write-Host ("    dotnet " + ($Arguments -join ' ')) -ForegroundColor DarkGray
    & dotnet @Arguments 2>&1 | ForEach-Object {
        if ($_ -is [System.Management.Automation.ErrorRecord]) {
            Write-Host $_.Exception.Message -ForegroundColor DarkGray
        } else {
            Write-Host $_
        }
    }
    if ($LASTEXITCODE -ne 0) {
        throw "$What 失败 (exit code $LASTEXITCODE)"
    }
}

$RepoRoot = Get-SecurePath $PSScriptRoot
$PublishDir = Join-Path $RepoRoot 'publish'
$Solution = Join-Path $RepoRoot 'SonicCubePanel.slnx'
$TrayProj = Join-Path $RepoRoot 'SonicCube.Tray\SonicCube.Tray.csproj'
$PanelProj = Join-Path $RepoRoot 'SonicCube.Panel\SonicCube.Panel.csproj'
$Rid = 'win-x64'
$StartTime = Get-Date

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "未找到 dotnet SDK。请安装 .NET 10 SDK: https://dotnet.microsoft.com/download"
}

Write-Step "还原并构建解决方案 ($Configuration)"
Invoke-Dotnet @('build', $Solution, '-c', $Configuration) '解决方案构建'

if (-not $SkipTests) {
    Write-Step "运行单元测试 (Protocol + Panel)"
    Invoke-Dotnet @('test', $Solution, '-c', $Configuration, '--no-build') '单元测试'
} else {
    Write-Host "`n==> 跳过单元测试 (-SkipTests)" -ForegroundColor Yellow
}

if (-not $NoClean -and (Test-Path -LiteralPath $PublishDir)) {
    Remove-Item -LiteralPath $PublishDir -Recurse -Force
}
New-Item -ItemType Directory -Force (Join-Path $PublishDir 'zh-CN') | Out-Null

$TrayPublishDir = Join-Path $RepoRoot "SonicCube.Tray\bin\$Configuration\net10.0-windows\$Rid\publish"
$PanelPublishDir = Join-Path $RepoRoot "SonicCube.Panel\bin\$Configuration\net10.0-windows\$Rid\publish"

if ($Static) {
    if ($SkipAot) {
        Write-Step "发布 SonicCube.Tray (self-contained 单文件, 非 AOT)"
        Invoke-Dotnet @('publish', $TrayProj, '-c', $Configuration, '-r', $Rid,
                        '--self-contained', 'true', '--no-restore',
                        '-p:PublishAot=false', '-p:PublishSingleFile=true',
                        '-p:IncludeNativeLibrariesForSelfExtract=true') 'Tray 发布'
    } else {
        Write-Step "发布 SonicCube.Tray (NativeAOT, 需要 C++ 生成工具, 耗时较长)"
        Invoke-Dotnet @('publish', $TrayProj, '-c', $Configuration, '-r', $Rid, '--no-restore') 'Tray 发布'
    }

    Write-Step "发布 SonicCube.Panel (self-contained 单文件)"
    Invoke-Dotnet @('publish', $PanelProj, '-c', $Configuration, '-r', $Rid,
                    '--self-contained', 'true', '--no-restore') 'Panel 发布'
} else {
    Write-Step "发布 SonicCube.Tray (框架依赖单文件)"
    Invoke-Dotnet @('publish', $TrayProj, '-c', $Configuration, '-r', $Rid,
                    '--self-contained', 'false', '--no-restore',
                    '-p:PublishAot=false', '-p:PublishSingleFile=true',
                    '-p:IncludeNativeLibrariesForSelfExtract=true') 'Tray 发布'

    Write-Step "发布 SonicCube.Panel (框架依赖单文件)"
    Invoke-Dotnet @('publish', $PanelProj, '-c', $Configuration, '-r', $Rid,
                    '--self-contained', 'false', '--no-restore',
                    '-p:PublishSingleFile=true',
                    '-p:IncludeNativeLibrariesForSelfExtract=true') 'Panel 发布'
}

# 卫星程序集直接在 publish\ 下做临时目录, 不在仓库根建 artifacts\
$SatelliteDir = Join-Path $PublishDir '.satellite-tmp'
Write-Step "构建 zh-CN 卫星程序集"
Invoke-Dotnet @('build', $PanelProj, '-c', $Configuration, '--no-restore',
                '-p:SatelliteResourceLanguages=zh-CN', '-o', $SatelliteDir) '卫星程序集构建'

Write-Step "收集产物到 publish\"
$TraySrcExe = Join-Path $TrayPublishDir 'SonicCube.Tray.exe'
$PanelSrcExe = Join-Path $PanelPublishDir 'SonicCube.Panel.exe'
$TraySrcHash = Get-SourceExeHash $TraySrcExe
$PanelSrcHash = Get-SourceExeHash $PanelSrcExe

Copy-Item -LiteralPath $TraySrcExe -Destination $PublishDir
Copy-Item -LiteralPath $PanelSrcExe -Destination $PublishDir

$TrayDst = Join-Path $PublishDir 'SonicCube.Tray.exe'
$PanelDst = Join-Path $PublishDir 'SonicCube.Panel.exe'
$TrayDstHash = (Get-FileHash -LiteralPath $TrayDst -Algorithm SHA256).Hash
$PanelDstHash = (Get-FileHash -LiteralPath $PanelDst -Algorithm SHA256).Hash
if ($TraySrcHash -and $TrayDstHash -ne $TraySrcHash) {
    throw "Tray exe 复制到 publish\ 后哈希不匹配, 可能被篡改"
}
if ($PanelSrcHash -and $PanelDstHash -ne $PanelSrcHash) {
    throw "Panel exe 复制到 publish\ 后哈希不匹配, 可能被篡改"
}

$SatelliteZh = Join-Path $SatelliteDir 'zh-CN'
if (Test-Path -LiteralPath $SatelliteZh) {
    Copy-Item (Join-Path $SatelliteZh '*') (Join-Path $PublishDir 'zh-CN') -ErrorAction SilentlyContinue
}
Remove-Item -LiteralPath $SatelliteDir -Recurse -Force -ErrorAction SilentlyContinue

$Elapsed = (Get-Date) - $StartTime
Write-Step "编译完成, 耗时 $([math]::Round($Elapsed.TotalSeconds, 1)) 秒"
Write-Host "输出目录: $PublishDir"
Get-ChildItem -Recurse $PublishDir | ForEach-Object {
    $size = if ($_.PSIsContainer) { '' } else { '  ({0:N1} MB)' -f ($_.Length / 1MB) }
    Write-Host "    $($_.FullName.Substring($PublishDir.Length + 1))$size"
}
