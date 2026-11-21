[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$ManagedDir = "",

    [Parameter(Mandatory = $false)]
    [string]$ProjectPath = "",

    [Parameter(Mandatory = $false)]
    [string]$OutputDir = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
    $ProjectPath = Join-Path $repoRoot "ScavPrototypeMP\CasualtiesTogether.csproj"
}
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot "references"
}

if ([string]::IsNullOrWhiteSpace($ManagedDir)) {
    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($env:CASUALTIESTOGETHER_GAME_PATH)) {
        $candidates += (Join-Path $env:CASUALTIESTOGETHER_GAME_PATH "CasualtiesUnknown_Data\Managed")
    }
    $candidates += @(
        "D:\SteamLibrary\steamapps\common\Casualties Unknown\CasualtiesUnknown_Data\Managed",
        "C:\Program Files (x86)\Steam\steamapps\common\Casualties Unknown\CasualtiesUnknown_Data\Managed"
    )
    $ManagedDir = $candidates | Where-Object { Test-Path (Join-Path $_ "Assembly-CSharp.dll") } | Select-Object -First 1
}

if ([string]::IsNullOrWhiteSpace($ManagedDir) -or -not (Test-Path (Join-Path $ManagedDir "Assembly-CSharp.dll"))) {
    throw "未找到有效的游戏 Managed 目录（缺少 Assembly-CSharp.dll）：'$ManagedDir'。请通过 -ManagedDir 显式指定。"
}

if (-not (Test-Path $ProjectPath)) {
    throw "未找到项目文件：'$ProjectPath'"
}

$nugetRoot = if (-not [string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
    $env:NUGET_PACKAGES
} else {
    Join-Path $HOME ".nuget\packages"
}

function Find-PublicizerLibDir {
    $pkgRoot = Join-Path $nugetRoot "bepinex.assemblypublicizer.msbuild"
    if (-not (Test-Path $pkgRoot)) {
        return $null
    }
    $dll = Get-ChildItem -Path $pkgRoot -Recurse -Filter "BepInEx.AssemblyPublicizer.dll" |
        Where-Object { $_.FullName -match "netstandard2\.1" } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if ($null -eq $dll) {
        return $null
    }
    return $dll.DirectoryName
}

$pubLibDir = Find-PublicizerLibDir
if ($null -eq $pubLibDir) {
    # NuGet 缓存尚未还原时先执行 restore 拉取 BepInEx.AssemblyPublicizer.MSBuild
    dotnet restore $ProjectPath --nologo -v minimal | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore 失败，退出码：$LASTEXITCODE"
    }
    $pubLibDir = Find-PublicizerLibDir
}

if ($null -eq $pubLibDir) {
    throw "在 NuGet 缓存 '$nugetRoot' 中未找到 netstandard2.1 版 BepInEx.AssemblyPublicizer.dll。"
}

Get-ChildItem -Path $pubLibDir -Filter "*.dll" | ForEach-Object {
    [System.Reflection.Assembly]::LoadFrom($_.FullName) | Out-Null
}

[xml]$csproj = Get-Content -Path $ProjectPath -Raw
$targetDllNames = @(
    $csproj.Project.ItemGroup.Reference |
        Where-Object { $_.HintPath -match '\$\(GameManagedDir\)' } |
        ForEach-Object { Split-Path ($_.HintPath -replace '\$\(GameManagedDir\)[\\/]', '') -Leaf }
    "com.rlabrecque.steamworks.net.dll"
) | Sort-Object -Unique

foreach ($required in @("Assembly-CSharp.dll", "com.rlabrecque.steamworks.net.dll")) {
    if ($targetDllNames -notcontains $required) {
        throw "解析出的目标 DLL 列表缺少必需项：$required"
    }
}

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

# 清理不在项目引用清单中的旧残留 DLL，保持引用目录与 csproj 严格一致
Get-ChildItem -Path $OutputDir -Filter "*.dll" -File -ErrorAction SilentlyContinue |
    Where-Object { $targetDllNames -notcontains $_.Name } |
    Remove-Item -Force

# Strip=true 且 Target=None：剥离全部方法体 IL，保留原始访问修饰符与完整类型/成员/特性元数据签名
$stripOptions = [BepInEx.AssemblyPublicizer.AssemblyPublicizerOptions]::new()
$stripOptions.Strip = $true
$stripOptions.Target = [BepInEx.AssemblyPublicizer.PublicizeTarget]::None
$stripOptions.IncludeOriginalAttributesAttribute = $false

$totalSrcBytes = 0L
$totalDstBytes = 0L

foreach ($dllName in $targetDllNames) {
    $srcPath = Join-Path $ManagedDir $dllName
    if (-not (Test-Path $srcPath)) {
        throw "Managed 目录缺少引用的程序集：'$srcPath'"
    }

    $dstPath = Join-Path $OutputDir $dllName
    $totalSrcBytes += (Get-Item $srcPath).Length

    $asmDef = [AsmResolver.DotNet.AssemblyDefinition]::FromFile($srcPath)
    [BepInEx.AssemblyPublicizer.AssemblyPublicizer]::Publicize($asmDef, $stripOptions) | Out-Null
    foreach ($module in $asmDef.Modules) {
        $module.Resources.Clear()
    }
    $asmDef.Write($dstPath)

    $dstLen = (Get-Item $dstPath).Length
    if ($dstLen -le 0) {
        throw "生成的脱壳引用桩为空文件：'$dstPath'"
    }
    $totalDstBytes += $dstLen
}

$srcMb = [Math]::Round($totalSrcBytes / 1MB, 2)
$dstMb = [Math]::Round($totalDstBytes / 1MB, 2)
Write-Host ">>> 已生成 $($targetDllNames.Count) 个脱壳引用桩至 '$OutputDir'（原始 ${srcMb} MB -> 精简桩 ${dstMb} MB）" -ForegroundColor Green
