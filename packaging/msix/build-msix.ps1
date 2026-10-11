<#
.SYNOPSIS
  Builds an MSIX package of OpenPin (self-contained, win-x64).

.DESCRIPTION
  Requires the Windows SDK (makeappx.exe / signtool.exe). GitHub's windows-latest runners have it.
  Without -CertPath the package is unsigned: fine for uploading to the Microsoft Store (the Store signs it),
  but Windows will refuse to install an unsigned MSIX directly.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File packaging/msix/build-msix.ps1 -Version 0.1.0
.EXAMPLE
  powershell -File packaging/msix/build-msix.ps1 -Version 0.1.0 -CertPath my.pfx -CertPassword ***
#>
param(
    [string]$Version = "0.1.0",
    [string]$OutDir = "artifacts",
    [string]$IdentityName = "OpenPin",
    [string]$Publisher = "CN=OpenPin",
    [string]$PublisherDisplayName = "OpenPin contributors",
    [string]$CertPath = "",
    [string]$CertPassword = ""
)
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$OutDir = if ([IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $repo $OutDir }
$stage = Join-Path $OutDir 'msix-stage'
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force $stage | Out-Null

# MSIX versions are a.b.c.d and the Store requires d = 0.
$parts = ($Version -split '[.-]')[0..2] | ForEach-Object { [int]$_ }
while ($parts.Count -lt 3) { $parts += 0 }
$msixVersion = "{0}.{1}.{2}.0" -f $parts[0], $parts[1], $parts[2]

Write-Host "Publishing OpenPin $Version (self-contained)..."
dotnet publish (Join-Path $repo 'src\OpenPin\OpenPin.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -p:Version=$Version -o $stage
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# Manifest + assets
$manifest = Get-Content (Join-Path $PSScriptRoot 'AppxManifest.xml') -Raw -Encoding UTF8
$manifest = $manifest.Replace('$IDENTITY_NAME$', $IdentityName).Replace('$PUBLISHER$', $Publisher).
    Replace('$PUBLISHER_DISPLAY_NAME$', $PublisherDisplayName).Replace('$VERSION$', $msixVersion)
[IO.File]::WriteAllText((Join-Path $stage 'AppxManifest.xml'), $manifest, (New-Object System.Text.UTF8Encoding($false)))
Copy-Item -Recurse -Force (Join-Path $PSScriptRoot 'Assets') (Join-Path $stage 'Assets')

# Locate the newest Windows SDK tools
$kits = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
$makeappx = Get-ChildItem $kits -Recurse -Filter makeappx.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $makeappx) { throw "makeappx.exe not found. Install the Windows SDK (https://developer.microsoft.com/windows/downloads/windows-sdk/)." }

# resources.pri lets Windows pick the size-specific icons (targetsize-*_altform-unplated) for the taskbar / Start.
$makepri = Join-Path $makeappx.DirectoryName 'makepri.exe'
if (Test-Path $makepri) {
    $priConfig = Join-Path $OutDir 'priconfig.xml'
    & $makepri createconfig /cf $priConfig /dq zh-CN /pv 10.0.0 /o | Out-Null
    & $makepri new /pr $stage /cf $priConfig /of (Join-Path $stage 'resources.pri') /o | Out-Null
    Remove-Item $priConfig -ErrorAction SilentlyContinue
}

$msix = Join-Path $OutDir "OpenPin-$Version-win-x64.msix"
& $makeappx.FullName pack /d $stage /p $msix /o
if ($LASTEXITCODE -ne 0) { throw "makeappx failed" }

if ($CertPath) {
    $signtool = Join-Path $makeappx.DirectoryName 'signtool.exe'
    & $signtool sign /fd SHA256 /f $CertPath /p $CertPassword /tr http://timestamp.digicert.com /td SHA256 $msix
    if ($LASTEXITCODE -ne 0) { throw "signtool failed" }
}

Remove-Item -Recurse -Force $stage
Write-Host "MSIX: $msix"
