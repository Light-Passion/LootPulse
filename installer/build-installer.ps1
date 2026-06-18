<#
.SYNOPSIS
  Publishes LootPulse (framework-dependent, win-x64) and compiles the Inno Setup installer.

.DESCRIPTION
  1. Publishes the app to installer\staging (framework-dependent — requires the .NET 9 Desktop
     Runtime on the target machine; the installer checks for it).
  2. Locates the Inno Setup compiler (ISCC.exe).
  3. Compiles installer\LootPulse.iss into installer\Output\LootPulse-Setup-<version>.exe.

  The app version comes from the published exe (csproj <Version>), so this script never needs editing
  when the version changes.

.EXAMPLE
  pwsh installer\build-installer.ps1
#>
param(
  [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$installerDir = $PSScriptRoot
$root         = Split-Path -Parent $installerDir
$proj         = Join-Path $root 'LootPulse.csproj'
$staging      = Join-Path $installerDir 'staging'
$iss          = Join-Path $installerDir 'LootPulse.iss'

Write-Host "==> Cleaning staging folder" -ForegroundColor Cyan
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }

Write-Host "==> Publishing $Configuration (framework-dependent, win-x64)" -ForegroundColor Cyan
dotnet publish $proj -c $Configuration -r win-x64 --self-contained false -o $staging
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

Write-Host "==> Locating Inno Setup compiler (ISCC.exe)" -ForegroundColor Cyan
$iscc = (Get-Command iscc -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
  foreach ($p in @(
      "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
      "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
      "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe")) {
    if (Test-Path $p) { $iscc = $p; break }
  }
}
if (-not $iscc) {
  throw "Inno Setup compiler (ISCC.exe) not found. Install it with: winget install --id JRSoftware.InnoSetup -e"
}
Write-Host "    using $iscc"

Write-Host "==> Compiling installer" -ForegroundColor Cyan
& $iscc "/DStagingDir=$staging\" $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed (exit $LASTEXITCODE)" }

$output = Join-Path $installerDir 'Output'
Write-Host "==> Done. Installer in: $output" -ForegroundColor Green
Get-ChildItem $output -Filter '*.exe' | Select-Object Name, Length, LastWriteTime
