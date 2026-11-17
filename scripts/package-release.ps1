[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$GamePath = $env:CASUALTIESTOGETHER_GAME_PATH,

    [Parameter(Mandatory = $false)]
    [ValidateSet("ReleaseFullgame", "DebugFullgame", "Release", "Debug")]
    [string]$Configuration = "ReleaseFullgame",

    [Parameter(Mandatory = $false)]
    [switch]$IncludeDebug
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$projPath = Join-Path $repoRoot "ScavPrototypeMP\CasualtiesTogether.csproj"
$pkgRoot  = Join-Path $repoRoot "build\packages"

if ([string]::IsNullOrWhiteSpace($GamePath)) {
    $defaultSteam = "D:\SteamLibrary\steamapps\common\Casualties Unknown"
    $cDriveSteam  = "C:\Program Files (x86)\Steam\steamapps\common\Casualties Unknown"
    if (Test-Path (Join-Path $defaultSteam "CasualtiesUnknown_Data\Managed\Assembly-CSharp.dll")) {
        $GamePath = $defaultSteam
    } elseif (Test-Path (Join-Path $cDriveSteam "CasualtiesUnknown_Data\Managed\Assembly-CSharp.dll")) {
        $GamePath = $cDriveSteam
    } else {
        throw "未找到游戏安装目录，请通过 -GamePath 或环境变量 CASUALTIESTOGETHER_GAME_PATH 指定。"
    }
}

[xml]$propsXml = Get-Content (Join-Path $repoRoot "Directory.Build.props") -Raw
$modVer = ($propsXml.Project.PropertyGroup | ForEach-Object { $_.ModVersion } | Where-Object { $_ } | Select-Object -First 1).Trim()

if (Test-Path $pkgRoot) {
    Remove-Item -Path $pkgRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $pkgRoot -Force | Out-Null

function Invoke-ModPackage {
    param([string]$Cfg)

    $stageRoot   = Join-Path $repoRoot "build\stage\$Cfg"
    $pluginStage = Join-Path $stageRoot "BepInEx\plugins\Together"
    if (Test-Path $stageRoot) {
        Remove-Item -Path $stageRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $pluginStage -Force | Out-Null

    Write-Host ">>> 正在构建 $Cfg (ModVersion=$modVer) ..." -ForegroundColor Cyan
    dotnet build $projPath -c $Cfg -p:BaseGamePath="$GamePath" -p:ModPluginOutputDir="$pluginStage" --nologo -v minimal | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build $Cfg 失败，退出码: $LASTEXITCODE"
    }

    $pdbPath = Join-Path $repoRoot "build\artifacts\CasualtiesTogether\bin\$Cfg\CasualtiesMP.pdb"
    if (Test-Path $pdbPath) {
        Copy-Item -Path $pdbPath -Destination $pluginStage -Force
    }

    $zipName = "CasualtiesTogether-$Cfg-v$modVer.zip"
    $zipPath = Join-Path $pkgRoot $zipName
    Compress-Archive -Path (Join-Path $stageRoot "BepInEx") -DestinationPath $zipPath -Force
    return $zipPath
}

$builtZips = @()
$builtZips += Invoke-ModPackage -Cfg $Configuration

if ($IncludeDebug -and $Configuration -eq "ReleaseFullgame") {
    $builtZips += Invoke-ModPackage -Cfg "DebugFullgame"
}

$hashLines = foreach ($zip in $builtZips) {
    $hash = (Get-FileHash -Path $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    $leaf = Split-Path -Leaf $zip
    "$hash  $leaf"
}

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$sumsFile  = Join-Path $pkgRoot "SHA256SUMS.txt"
[System.IO.File]::WriteAllLines($sumsFile, $hashLines, $utf8NoBom)

Write-Host ">>> 打包完成，输出目录: $pkgRoot" -ForegroundColor Green
$hashLines | ForEach-Object { Write-Host "    $_" }
