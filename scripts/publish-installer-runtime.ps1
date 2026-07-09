#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Publishes the Native-AOT SigilBuild.Installer.Host runtime and stages it as
    the exe-wrapper runtime the packager stamps (spec T3).

.DESCRIPTION
    For each requested RID this runs

        dotnet publish src/SigilBuild.Installer.Host -c <Configuration> `
            -r <rid> -p:PublishAot=true -p:SigilAotPublish=true

    and copies the produced `installer.exe` to
    `<DestinationRoot>/runtimes/<rid>/SigilBuild.Installer.Host.exe` — the exact
    layout WrapperRuntimeLocator.Locate(architecture) resolves at pack time.

    This is deliberately a SEPARATE, explicitly-invoked step: it is NOT part of
    `dotnet build Sigil.sln` / `dotnet test`, because the AOT publish is slow and
    the win-arm64 link requires the "MSVC v143 - VS 2022 C++ ARM64 build tools"
    component, which is absent on some dev boxes. Per-RID failures are tolerated
    (warned) unless -RequireAll is set; the win-x64 size gate always applies to a
    win-x64 that did build.

    Prerequisites (both provisioned on GitHub's windows-latest image):
      * win-x64   : "Desktop development with C++" workload (the Native AOT linker).
      * win-arm64 : additionally "MSVC v143 C++ ARM64 build tools".

.PARAMETER Rids
    Runtime identifiers to publish. Default: win-x64, win-arm64.

.PARAMETER Configuration
    Build configuration. Default: Release.

.PARAMETER DestinationRoot
    Directory that receives the `runtimes/<rid>/` tree. Default: the CLI's build
    output (`src/SigilBuild.Cli/bin/<Configuration>/net10.0`) so a locally-built
    `sigil` resolves the runtime next to itself.

.PARAMETER SizeGateMb
    Fail if the staged win-x64 host exe exceeds this many MB. Default: 40.
    (Measured win-x64 AOT size is ~30 MB; the gate carries ~10 MB headroom for
    Skia/ANGLE/HarfBuzz native-lib version bumps. 25 MB is unattainable — those
    native libs alone are ~19 MB in every variant. See docs/architecture/adr-avalonia-aot.md.)

.PARAMETER RequireAll
    Treat a per-RID publish failure as fatal (used by CI legs that provision all
    toolchains). Off by default so a dev box without ARM64 tools still stages x64.
#>
[CmdletBinding()]
param(
    [string[]] $Rids = @('win-x64', 'win-arm64'),
    [string] $Configuration = 'Release',
    [string] $DestinationRoot,
    [double] $SizeGateMb = 40,
    [switch] $RequireAll
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$hostProject = Join-Path $repoRoot 'src/SigilBuild.Installer.Host'
$runtimeFileName = 'SigilBuild.Installer.Host.exe'

if (-not $DestinationRoot) {
    $DestinationRoot = Join-Path $repoRoot "src/SigilBuild.Cli/bin/$Configuration/net10.0"
}

# --- MSVC toolchain discovery -------------------------------------------------
# The Native AOT link needs (a) link.exe (found by the ILC targets via vswhere,
# which ships in the VS Installer dir, not on PATH) and (b) the VC runtime import
# libraries (libcmt.lib etc.). A box can have MULTIPLE MSVC toolsets installed
# where some are INCOMPLETE for a given arch (missing lib\<arch>\libcmt.lib); the
# ILC targets may pick an incomplete toolset's link.exe, yielding LNK1104. link.exe
# also honours the LIB env var, so we prepend the newest COMPLETE toolset's
# lib\<arch> for the target arch — which also makes "no ARM64 C++ tools installed"
# a clean, fast per-RID skip instead of a slow failed publish.
$pf86 = [Environment]::GetFolderPath('ProgramFilesX86')
$vswhere = Join-Path $pf86 'Microsoft Visual Studio\Installer\vswhere.exe'
$vsInstallPath = $null
if (Test-Path $vswhere) {
    $vswhereDir = Split-Path $vswhere
    if ($env:PATH -notlike "*$vswhereDir*") { $env:PATH = "$vswhereDir;$env:PATH" }
    $vsInstallPath = & $vswhere -latest -products * -property installationPath
    if ($LASTEXITCODE -ne 0) { $vsInstallPath = $null }
}
else {
    Write-Warning "vswhere.exe not found; the Native AOT link may fail to locate link.exe. Ensure the 'Desktop development with C++' workload is installed."
}

# Snapshot the incoming LIB so each RID starts from the same base.
$baseLib = $env:LIB

# Resolves the newest complete MSVC lib\<arch> dir (one that has libcmt.lib) and
# prepends it to LIB. Returns $true when the toolchain for $Arch is present.
function Set-VcLibForArch {
    param([string] $Arch)   # 'x64' | 'arm64'
    $env:LIB = $baseLib
    if (-not $vsInstallPath) { return $true }  # no VS layout probed; let ILC try.
    $msvcRoot = Join-Path $vsInstallPath 'VC\Tools\MSVC'
    if (-not (Test-Path $msvcRoot)) { return $true }
    $complete = Get-ChildItem $msvcRoot -Directory |
        Where-Object { Test-Path (Join-Path $_.FullName "lib\$Arch\libcmt.lib") } |
        Sort-Object { [version]$_.Name } -Descending |
        Select-Object -First 1
    if (-not $complete) {
        Write-Warning ("No complete MSVC toolset with lib\$Arch\libcmt.lib found " +
            "(the VC.Tools.$Arch build-tools component is missing).")
        return $false
    }
    $libDir = Join-Path $complete.FullName "lib\$Arch"
    $env:LIB = if ([string]::IsNullOrEmpty($baseLib)) { $libDir } else { "$libDir;$baseLib" }
    Write-Host "    using MSVC $($complete.Name) libs for ${Arch}: $libDir"
    return $true
}

$failed = @()
$staged = @()

foreach ($rid in $Rids) {
    Write-Host "==> Publishing Native AOT host for $rid ..." -ForegroundColor Cyan
    $toolArch = if ($rid -eq 'win-arm64') { 'arm64' } else { 'x64' }

    if (-not (Set-VcLibForArch $toolArch)) {
        $msg = "MSVC C++ build tools for $toolArch are not installed; cannot AOT-link $rid. " +
               "Install the 'MSVC v143 - VS 2022 C++ $($toolArch.ToUpper()) build tools' component, or build $rid in CI."
        if ($RequireAll) { throw $msg }
        Write-Warning $msg
        $failed += $rid
        continue
    }

    $publishOut = Join-Path ([System.IO.Path]::GetTempPath()) "sigil-host-$rid-$([guid]::NewGuid().ToString('N'))"

    & dotnet publish $hostProject -c $Configuration -r $rid `
        -p:PublishAot=true -p:SigilAotPublish=true -o $publishOut
    $publishExit = $LASTEXITCODE

    $producedExe = Join-Path $publishOut 'installer.exe'
    if ($publishExit -ne 0 -or -not (Test-Path $producedExe)) {
        $msg = "AOT publish for $rid failed (exit $publishExit). " +
               "If this is win-arm64 on a box without the 'MSVC v143 C++ ARM64 build tools' component, build it in CI instead."
        if ($RequireAll) { throw $msg }
        Write-Warning $msg
        $failed += $rid
        continue
    }

    $destDir = Join-Path $DestinationRoot "runtimes/$rid"
    New-Item -ItemType Directory -Force -Path $destDir | Out-Null
    $destExe = Join-Path $destDir $runtimeFileName
    Copy-Item -Path $producedExe -Destination $destExe -Force

    # T18: the AOT publish emits the host's native dependencies (Skia/ANGLE/
    # HarfBuzz — ~18 MB) as loose *.dll BESIDE installer.exe. Stage them under
    # runtimes/<rid>/native/ so ExeWrapperPackager can archive them into the
    # stamped Setup.exe's SIGIL_RUNTIME_V1 resource, making the wizard installer
    # self-contained (WrapperRuntimeLocator.LocateNativeDeps resolves this folder).
    # Managed IL is baked into the AOT exe, so the loose DLLs are all native.
    $nativeDir = Join-Path $destDir 'native'
    New-Item -ItemType Directory -Force -Path $nativeDir | Out-Null
    $nativeDlls = Get-ChildItem -Path $publishOut -Filter '*.dll' -File
    foreach ($dll in $nativeDlls) {
        Copy-Item -Path $dll.FullName -Destination (Join-Path $nativeDir $dll.Name) -Force
    }
    $nativeCount = @($nativeDlls).Count
    $nativeMb = if ($nativeCount -gt 0) {
        [math]::Round((($nativeDlls | Measure-Object -Property Length -Sum).Sum) / 1MB, 2)
    } else { 0 }
    Write-Host "    staged $nativeCount native dep dll(s) ($nativeMb MB) into $nativeDir"

    $exeMb = [math]::Round((Get-Item $destExe).Length / 1MB, 2)
    # The shippable host footprint = the AOT exe plus the native libraries it
    # loads at runtime (Skia/ANGLE/HarfBuzz), excluding debug PDBs. This total is
    # what the size gate measures, because those native libs (~19 MB) ship in
    # every variant and are the reason the spec's 25 MB target is unattainable.
    $footprintBytes = (Get-ChildItem -Path $publishOut -Recurse -File |
        Where-Object { $_.Extension -ne '.pdb' } |
        Measure-Object -Property Length -Sum).Sum
    $footprintMb = [math]::Round($footprintBytes / 1MB, 2)
    Write-Host "    staged $destExe (exe $exeMb MB; host footprint $footprintMb MB)" -ForegroundColor Green
    $staged += [pscustomobject]@{ Rid = $rid; ExeMb = $exeMb; FootprintMb = $footprintMb; Path = $destExe }

    # Size gate applies to the win-x64 host (the primary shipping RID that always
    # builds locally and in CI).
    if ($rid -eq 'win-x64' -and $footprintMb -gt $SizeGateMb) {
        throw "win-x64 host footprint is $footprintMb MB, exceeds the $SizeGateMb MB size gate."
    }

    Remove-Item -Recurse -Force $publishOut -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host 'Staged runtimes:' -ForegroundColor Cyan
if ($staged.Count -gt 0) {
    $staged | Format-Table -AutoSize | Out-String | Write-Host
}
if ($failed.Count -gt 0) {
    Write-Warning "Deferred RIDs (build in CI): $($failed -join ', ')"
}

# win-x64 is mandatory: if it did not stage, that is always a hard failure.
$stagedRids = @($staged | ForEach-Object { $_.Rid })
if ($stagedRids -notcontains 'win-x64') {
    throw 'win-x64 host runtime was not staged; cannot proceed.'
}
