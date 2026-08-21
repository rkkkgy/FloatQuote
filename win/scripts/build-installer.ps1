# Builds a per-user Windows installer: dist\FloatQuote-Setup-<version>.exe
$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

$Version = "1.1.0"
$PublishDir = Join-Path $Root "publish"
$DistDir = Join-Path $Root "dist"
$Iss = Join-Path $Root "setup\FloatQuote.iss"

Write-Host "==> Stopping running FloatQuote.exe (if any)"
Get-Process -Name "FloatQuote" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

Write-Host "==> Publishing self-contained win-x64"
if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=none `
  -p:DebugSymbols=false `
  -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$Exe = Join-Path $PublishDir "FloatQuote.exe"
if (-not (Test-Path $Exe)) { throw "Published exe not found: $Exe" }

function Find-Iscc {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
        "${env:LOCALAPPDATA}\Programs\Inno Setup 6\ISCC.exe"
    )
    foreach ($p in $candidates) {
        if (Test-Path $p) { return $p }
    }
    $cmd = Get-Command iscc -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

function Install-InnoSetup {
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if ($winget) {
        Write-Host "==> Installing Inno Setup via winget"
        & winget install --id JRSoftware.InnoSetup -e --disable-interactivity `
            --accept-package-agreements --accept-source-agreements
        return
    }
    $setup = Join-Path $env:TEMP "innosetup-installer.exe"
    Write-Host "==> Downloading Inno Setup"
    Invoke-WebRequest -Uri "https://jrsoftware.org/download.php/is.exe" -OutFile $setup
    Write-Host "==> Installing Inno Setup (per-user silent)"
    Start-Process -FilePath $setup -ArgumentList "/VERYSILENT", "/CURRENTUSER", "/NORESTART" -Wait
}

$iscc = Find-Iscc
if (-not $iscc) {
    Install-InnoSetup
    $iscc = Find-Iscc
}
if (-not $iscc) { throw "Inno Setup compiler (ISCC.exe) not found after install." }

New-Item -ItemType Directory -Force -Path $DistDir | Out-Null
Write-Host "==> Compiling installer with $iscc"
& $iscc $Iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

$Setup = Join-Path $DistDir "FloatQuote-Setup-$Version.exe"
if (-not (Test-Path $Setup)) { throw "Installer not produced: $Setup" }

Write-Host ""
Write-Host "Installer: $Setup"
Write-Host ("Size: {0:N1} MB" -f ((Get-Item $Setup).Length / 1MB))
