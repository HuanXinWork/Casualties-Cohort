[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$VersionTag = "",

    [Parameter(Mandatory = $false)]
    [string]$OutputDir = "",

    [Parameter(Mandatory = $false)]
    [string]$GamePath = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$projPath = Join-Path $repoRoot "ScavPrototypeMP\CasualtiesTogether.csproj"
$propsPath = Join-Path $repoRoot "Directory.Build.props"
$libsDir = Join-Path $repoRoot "libs\dlls"
$refsDir = Join-Path $repoRoot "references"

if (-not (Test-Path $projPath)) {
    throw "未找到项目文件：'$projPath'"
}
if (-not (Test-Path $propsPath)) {
    throw "未找到构建属性文件：'$propsPath'"
}

[xml]$propsXml = Get-Content -Path $propsPath -Raw
$modVersion = ($propsXml.Project.PropertyGroup | ForEach-Object { $_.ModVersion } | Where-Object { $_ } | Select-Object -First 1).Trim()
if ([string]::IsNullOrWhiteSpace($modVersion)) {
    throw "无法从 '$propsPath' 读取 <ModVersion>。"
}

if ([string]::IsNullOrWhiteSpace($VersionTag)) {
    $VersionTag = "v$modVersion"
}

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot "build\release-assets"
} else {
    $OutputDir = [System.IO.Path]::GetFullPath($OutputDir)
}

$runtimeDllNames = @(
    "K4os.Compression.LZ4.dll",
    "LiteEntitySystem.dll",
    "LiteNetLib.dll",
    "OpusSharp.Core.dll",
    "opus.dll",
    "RefMagic.dll",
    "System.Runtime.CompilerServices.Unsafe.dll"
)

foreach ($dllName in $runtimeDllNames) {
    $dllPath = Join-Path $libsDir $dllName
    if (-not (Test-Path $dllPath)) {
        throw "缺少必需的运行时依赖库：'$dllPath'"
    }
}

# 注册隔离可卸载的 AssemblyLoadContext，以内存流方式反射校验编译产物而不锁定磁盘文件
if (-not ("CohortBuildVerifierLoadContext" -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

public sealed class CohortBuildVerifierLoadContext : AssemblyLoadContext {
    private readonly string[] _probeDirs;

    public CohortBuildVerifierLoadContext(string[] probeDirs) : base(isCollectible: true) {
        _probeDirs = probeDirs ?? Array.Empty<string>();
    }

    protected override Assembly Load(AssemblyName assemblyName) {
        string simpleName = assemblyName.Name;
        if (string.IsNullOrEmpty(simpleName)) {
            return null;
        }
        // BCL 程序集交由宿主默认上下文解析，避免传入真机 Mono Managed 目录时误加载 Mono 版 mscorlib/netstandard
        if (simpleName == "mscorlib" || simpleName == "netstandard" || simpleName == "System"
            || (simpleName.StartsWith("System.", StringComparison.Ordinal) && simpleName != "System.Runtime.CompilerServices.Unsafe")) {
            return null;
        }
        foreach (string dir in _probeDirs) {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) {
                continue;
            }
            string candidate = Path.Combine(dir, simpleName + ".dll");
            if (File.Exists(candidate)) {
                using var fs = File.OpenRead(candidate);
                return LoadFromStream(fs);
            }
        }
        return null;
    }
}
"@
}

function Get-NuGetProbeDirs {
    $nugetRoot = if (-not [string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
        $env:NUGET_PACKAGES
    } else {
        Join-Path $HOME ".nuget\packages"
    }
    $dirs = [System.Collections.Generic.List[string]]::new()
    if (Test-Path $nugetRoot) {
        foreach ($pattern in @("bepinex*", "harmonyx*", "monomod*")) {
            Get-ChildItem -Path $nugetRoot -Directory -Filter $pattern -ErrorAction SilentlyContinue | ForEach-Object {
                Get-ChildItem -Path $_.FullName -Recurse -Filter "*.dll" -ErrorAction SilentlyContinue |
                    Select-Object -ExpandProperty DirectoryName -Unique |
                    ForEach-Object { $dirs.Add($_) }
            }
        }
    }
    return $dirs.ToArray()
}

function Test-CompiledAssemblyMetadata {
    param(
        [Parameter(Mandatory = $true)][string]$DllPath,
        [Parameter(Mandatory = $true)][string]$ExpectedVersion,
        [Parameter(Mandatory = $true)][bool]$ExpectedRelease,
        [Parameter(Mandatory = $true)][bool]$ExpectedDebug
    )

    if (-not (Test-Path $DllPath)) {
        throw "未找到编译产物：'$DllPath'"
    }

    $probeDirs = [System.Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($GamePath)) {
        $managedCandidate = Join-Path $GamePath "CasualtiesUnknown_Data\Managed"
        if (Test-Path $managedCandidate) {
            $probeDirs.Add($managedCandidate)
        }
    }
    if (Test-Path $refsDir) {
        $probeDirs.Add($refsDir)
    }
    $probeDirs.Add($libsDir)
    foreach ($d in (Get-NuGetProbeDirs)) {
        $probeDirs.Add($d)
    }

    $alc = [CohortBuildVerifierLoadContext]::new($probeDirs.ToArray())
    try {
        $bytes = [System.IO.File]::ReadAllBytes($DllPath)
        $ms = [System.IO.MemoryStream]::new($bytes)
        try {
            $asm = $alc.LoadFromStream($ms)
        } finally {
            $ms.Dispose()
        }

        $flags = [System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::NonPublic -bor [System.Reflection.BindingFlags]::Static
        $buildInfoType = $asm.GetType("Together.BuildInfo", $true)
        $pluginType = $asm.GetType("Together.Plugin", $true)

        $buildInfoVer = [string]$buildInfoType.GetField("MOD_VERSION", $flags).GetRawConstantValue()
        $pluginVer = [string]$pluginType.GetField("MOD_VERSION", $flags).GetValue($null)
        $isRelease = [bool]$pluginType.GetField("IS_RELEASE_BUILD", $flags).GetValue($null)
        $isDebug = [bool]$pluginType.GetField("IS_DEBUG_BUILD", $flags).GetValue($null)
        $isFullgame = [bool]$pluginType.GetField("IS_FULLGAME_BUILD", $flags).GetValue($null)

        if ($buildInfoVer -ne $ExpectedVersion -or $pluginVer -ne $ExpectedVersion) {
            throw "版本常量不匹配 ($DllPath)：BuildInfo.MOD_VERSION='$buildInfoVer', Plugin.MOD_VERSION='$pluginVer', 期望='$ExpectedVersion'"
        }
        if ($isRelease -ne $ExpectedRelease) {
            throw "IS_RELEASE_BUILD 校验失败 ($DllPath)：实际=$isRelease, 期望=$ExpectedRelease"
        }
        if ($isDebug -ne $ExpectedDebug) {
            throw "IS_DEBUG_BUILD 校验失败 ($DllPath)：实际=$isDebug, 期望=$ExpectedDebug"
        }
        if (-not $isFullgame) {
            throw "IS_FULLGAME_BUILD 校验失败 ($DllPath)：实际=$isFullgame, 期望=True"
        }

        Write-Host "    [校验通过] $(Split-Path -Leaf $DllPath): MOD_VERSION=$pluginVer, IS_RELEASE_BUILD=$isRelease, IS_DEBUG_BUILD=$isDebug, IS_FULLGAME_BUILD=$isFullgame" -ForegroundColor DarkGreen
    } finally {
        $alc.Unload()
    }
}

function Invoke-ConfigurationBuild {
    param([Parameter(Mandatory = $true)][string]$Configuration)

    $buildArgs = @(
        "build",
        $projPath,
        "-c", $Configuration,
        "--no-incremental",
        "--nologo",
        "-v", "minimal",
        # 置空插件输出目录，防止打包脚本修改开发者本机真实游戏的 BepInEx/plugins/Together
        "-p:ModPluginOutputDir="
    )
    if (-not [string]::IsNullOrWhiteSpace($GamePath)) {
        $buildArgs += "-p:BaseGamePath=$GamePath"
    }

    Write-Host ">>> 正在构建 $Configuration (VersionTag=$VersionTag, ModVersion=$modVersion) ..." -ForegroundColor Cyan
    & dotnet @buildArgs | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build $Configuration 失败，退出码：$LASTEXITCODE"
    }
}

function New-PluginZipArchive {
    param(
        [Parameter(Mandatory = $true)][string]$ZipPath,
        [Parameter(Mandatory = $true)][string[]]$SourceFiles,
        [Parameter(Mandatory = $true)][int]$ExpectedCount
    )

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    if (Test-Path $ZipPath) {
        Remove-Item -Path $ZipPath -Force
    }

    # 显式使用正斜杠写入 ZIP 条目名，确保 Windows 与 Linux 解压器均识别标准目录结构
    $fs = [System.IO.File]::Open($ZipPath, [System.IO.FileMode]::CreateNew)
    try {
        $zip = [System.IO.Compression.ZipArchive]::new($fs, [System.IO.Compression.ZipArchiveMode]::Create, $false)
        try {
            foreach ($filePath in $SourceFiles) {
                if (-not (Test-Path $filePath)) {
                    throw "打包缺失源文件：'$filePath'"
                }
                $entryName = "BepInEx/plugins/Together/$(Split-Path -Leaf $filePath)"
                [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                    $zip,
                    $filePath,
                    $entryName,
                    [System.IO.Compression.CompressionLevel]::Optimal
                ) | Out-Null
            }
        } finally {
            $zip.Dispose()
        }
    } finally {
        $fs.Dispose()
    }

    $verifyZip = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        if ($verifyZip.Entries.Count -ne $ExpectedCount) {
            throw "ZIP 条目数异常 ($ZipPath)：实际=$($verifyZip.Entries.Count), 期望=$ExpectedCount"
        }
    } finally {
        $verifyZip.Dispose()
    }
}

Invoke-ConfigurationBuild -Configuration "ReleaseFullgame"
Invoke-ConfigurationBuild -Configuration "DebugFullgame"

$binRoot = Join-Path $repoRoot "build\artifacts\CasualtiesTogether\bin"
$releaseDll = Join-Path $binRoot "ReleaseFullgame\CasualtiesMP.dll"
$debugDll = Join-Path $binRoot "DebugFullgame\CasualtiesMP.dll"
$debugPdb = Join-Path $binRoot "DebugFullgame\CasualtiesMP.pdb"

if (-not (Test-Path $debugPdb)) {
    throw "DebugFullgame 构建未生成调试符号文件：'$debugPdb'"
}

Test-CompiledAssemblyMetadata -DllPath $releaseDll -ExpectedVersion $modVersion -ExpectedRelease $true -ExpectedDebug $false
Test-CompiledAssemblyMetadata -DllPath $debugDll -ExpectedVersion $modVersion -ExpectedRelease $false -ExpectedDebug $true

if (Test-Path $OutputDir) {
    Remove-Item -Path $OutputDir -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

$runtimeDllPaths = $runtimeDllNames | ForEach-Object { Join-Path $libsDir $_ }

$releaseZipName = "CasualtiesCohort-$VersionTag.zip"
$debugZipName = "CasualtiesCohort-$VersionTag+debug.zip"
$singleReleaseDllName = "CasualtiesMP.dll"
$singleDebugDllName = "CasualtiesMP+debug.dll"

$releaseZipPath = Join-Path $OutputDir $releaseZipName
$debugZipPath = Join-Path $OutputDir $debugZipName
$singleReleaseDllPath = Join-Path $OutputDir $singleReleaseDllName
$singleDebugDllPath = Join-Path $OutputDir $singleDebugDllName

New-PluginZipArchive -ZipPath $releaseZipPath -SourceFiles (@($releaseDll) + $runtimeDllPaths) -ExpectedCount 8
New-PluginZipArchive -ZipPath $debugZipPath -SourceFiles (@($debugDll, $debugPdb) + $runtimeDllPaths) -ExpectedCount 9

Copy-Item -Path $releaseDll -Destination $singleReleaseDllPath -Force
Copy-Item -Path $debugDll -Destination $singleDebugDllPath -Force

$assetNames = @(
    $releaseZipName,
    $debugZipName,
    $singleReleaseDllName,
    $singleDebugDllName
)

$hashLines = foreach ($name in $assetNames) {
    $filePath = Join-Path $OutputDir $name
    $sha256 = (Get-FileHash -Path $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$sha256  $name"
}

$sumsPath = Join-Path $OutputDir "SHA256SUMS.txt"
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText($sumsPath, (($hashLines -join "`n") + "`n"), $utf8NoBom)

Write-Host ">>> 打包完成，5 项标准发布资产已输出至: $OutputDir" -ForegroundColor Green
$hashLines | ForEach-Object { Write-Host "    $_" }
