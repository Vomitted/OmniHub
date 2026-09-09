<#
.SYNOPSIS
    Builds the OmniHub installer.

.DESCRIPTION
    Publishes the application self-contained for win-x64, then compiles installer\OmniHub.iss
    against that publish directory and leaves dist\OmniHub-<version>-setup.exe behind.

    Self-contained is not optional here. The repository's ordinary Release output is
    framework-dependent -- about 1.6 MB, and useless on a machine with no .NET 8 desktop
    runtime -- while every published release has been self-contained at around 63 MB. Nothing
    recorded which flags produced the shipped zip, so this script exists partly to stop that
    difference living only in somebody's shell history.

    The version is read from the csproj rather than passed in, because UpdateCheck compares
    that same value against the published release tags at runtime. Two places to bump is one
    place to forget.
#>
[CmdletBinding()]
param(
    # Skips dotnet publish and compiles against whatever is already in the publish directory.
    # For iterating on the .iss without waiting for a full publish each time.
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $root 'OmniHub.App\OmniHub.App.csproj'
$publishDir = Join-Path $root 'OmniHub.App\bin\Release\net8.0-windows\win-x64\publish'

# --- version ---------------------------------------------------------------
$version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> found in $csproj" }
Write-Host "OmniHub $version" -ForegroundColor Cyan

# --- locate the Inno Setup compiler ----------------------------------------
# Inno Setup 6.7 installs per-user under LOCALAPPDATA by default, which is not where the
# machine-wide paths people usually check would find it.
$isccCandidates = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    throw "ISCC.exe not found. Install it with: winget install JRSoftware.InnoSetup`nLooked in:`n  $($isccCandidates -join "`n  ")"
}

# --- publish ---------------------------------------------------------------
if (-not $SkipPublish) {
    Write-Host 'Publishing self-contained win-x64...' -ForegroundColor Cyan
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    dotnet publish $csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
}

if (-not (Test-Path (Join-Path $publishDir 'OmniHub.exe'))) {
    throw "No OmniHub.exe in $publishDir. Run without -SkipPublish."
}

$payload = [math]::Round((Get-ChildItem $publishDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host "Payload: $payload MB" -ForegroundColor DarkGray

# --- compile ---------------------------------------------------------------
Write-Host 'Compiling installer...' -ForegroundColor Cyan
& $iscc "/DAppVersion=$version" "/DSourceDir=$publishDir" (Join-Path $PSScriptRoot 'OmniHub.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }

# --- report ----------------------------------------------------------------
$setup = Join-Path $root "dist\OmniHub-$version-setup.exe"
if (-not (Test-Path $setup)) { throw "ISCC reported success but $setup is missing" }

$size = [math]::Round((Get-Item $setup).Length / 1MB, 1)
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLower()

Write-Host ''
Write-Host "  $setup" -ForegroundColor Green
Write-Host "  $size MB"
Write-Host "  sha256 $hash"
Write-Host ''
Write-Host 'Publish the checksum with the release; docs/index.html quotes it by hand.' -ForegroundColor DarkGray
