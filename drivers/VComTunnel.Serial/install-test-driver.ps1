param(
    [string]$InfPath = "$PSScriptRoot\VComTunnel.Serial.inf",
    [switch]$Install
)

$ErrorActionPreference = "Stop"

$packageRoot = Split-Path -Parent $InfPath
$sysPath = Join-Path $packageRoot "VComTunnel.Serial.sys"
$catPath = Join-Path $packageRoot "VComTunnel.Serial.cat"

Write-Host "VComTunnel.Serial KMDF package check"
Write-Host "INF: $InfPath"
Write-Host "SYS: $sysPath"
Write-Host "CAT: $catPath"
Write-Host ""

if (-not (Test-Path -LiteralPath $InfPath)) {
    throw "INF was not found: $InfPath"
}

if (-not (Test-Path -LiteralPath $sysPath) -or -not (Test-Path -LiteralPath $catPath)) {
    Write-Host "This is still a scaffold. Build and test-sign the WDK driver first."
    Write-Host "Expected files:"
    Write-Host "  VComTunnel.Serial.sys"
    Write-Host "  VComTunnel.Serial.cat"
    exit 2
}

Write-Host "Package files are present."
Write-Host "Review DESIGN.md and SERVICE_CHANNEL.md before installing on a test machine."
Write-Host ""
Write-Host "Install command:"
Write-Host "pnputil.exe /add-driver `"$InfPath`" /install"

if ($Install) {
    Write-Host ""
    Write-Host "Running pnputil. This must be executed from an elevated PowerShell."
    pnputil.exe /add-driver "$InfPath" /install
}
