<#
.SYNOPSIS
  Builds OpenPin Windows Installer (Setup.exe) and Portable Zip with bundled dependencies.
.PARAMETER Version
  The release version string, e.g. 0.1.0
.PARAMETER OutDir
  The destination output directory for release artifacts (defaults to publish/release)
#>
param(
    [string]$Version = "0.1.1",
    [string]$OutDir = "publish/release"
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
$OutDir = if ([IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $repo $OutDir }

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " Building OpenPin v$Version Self-Contained Release Packages" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Locate Inno Setup Compiler (ISCC.exe)
$cmd = Get-Command iscc -ErrorAction SilentlyContinue
$iscc = if ($cmd) { $cmd.Source } else { $null }
if (-not $iscc) {
    $searchPaths = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
    )
    foreach ($p in $searchPaths) {
        if (Test-Path $p) { $iscc = $p; break }
    }
}

if (-not $iscc) {
    Write-Warning "ISCC.exe not found. Setup.exe will be skipped. Install Inno Setup 6 via: winget install JRSoftware.InnoSetup"
} else {
    Write-Host "Found Inno Setup Compiler: $iscc" -ForegroundColor Green
}

# 2. Output directories
$scPublishDir = Join-Path $repo "publish/OpenPin-SelfContained"
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Force $OutDir | Out-Null }

# 3. Publish self-contained single-file binary
Write-Host "Publishing self-contained win-x64 binary..." -ForegroundColor Yellow
dotnet publish (Join-Path $repo 'src/OpenPin/OpenPin.csproj') -c Release -r win-x64 `
    --self-contained true `
    -p:PublishReadyToRun=true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:Version=$Version `
    -o $scPublishDir --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# 4. Create Portable Package (.zip)
Write-Host "Creating Portable Package..." -ForegroundColor Yellow
$portableStage = Join-Path $env:TEMP ("OpenPin-portable-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $portableStage | Out-Null
try {
    Copy-Item (Join-Path $scPublishDir "OpenPin.exe") (Join-Path $portableStage "OpenPin.exe")
    Copy-Item (Join-Path $repo "LICENSE") (Join-Path $portableStage "LICENSE")
    # Marker file for OpenPin portable mode (saves data into .\data instead of %AppData%)
    [IO.File]::WriteAllText((Join-Path $portableStage "portable.txt"), "portable")

    $portableZip = Join-Path $OutDir "OpenPin-$Version-win-x64-Portable.zip"
    if (Test-Path $portableZip) { Remove-Item -Force $portableZip }
    Compress-Archive -Path "$portableStage\*" -DestinationPath $portableZip -CompressionLevel Optimal
    Write-Host "Portable Zip created: $portableZip" -ForegroundColor Green
}
finally {
    Remove-Item -Recurse -Force $portableStage -ErrorAction SilentlyContinue
}

# 5. Build Inno Setup Installer (Setup.exe)
if ($iscc) {
    Write-Host "Building Windows Setup installer..." -ForegroundColor Yellow
    $issFile = Join-Path $PSScriptRoot "innosetup/openpin.iss"
    & $iscc "/DMyAppVersion=$Version" "/DMySourceDir=$scPublishDir" "/DMyOutputDir=$OutDir" $issFile
    if ($LASTEXITCODE -ne 0) { throw "ISCC compilation failed" }
    Write-Host "Setup installer created in: $OutDir" -ForegroundColor Green
}

# 6. Generate SHA256 checksums
Write-Host "Generating SHA256SUMS.txt..." -ForegroundColor Yellow
$checksums = @()
Get-ChildItem -Path $OutDir -File | Where-Object { $_.Name -notmatch "SHA256SUMS" } | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksums += "$hash  $($_.Name)"
}
$sumsFile = Join-Path $OutDir "SHA256SUMS.txt"
$checksums -join "`r`n" | Set-Content -Path $sumsFile -Encoding utf8
Write-Host "Checksums written to: $sumsFile" -ForegroundColor Green

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host (" Release Artifacts Ready in " + $OutDir + ":") -ForegroundColor Cyan
Get-ChildItem -Path $OutDir | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize
Write-Host "==========================================================" -ForegroundColor Cyan
