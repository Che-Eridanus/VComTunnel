[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$testId = [guid]::NewGuid().ToString('N')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "codex-vcom-control-$testId"
$resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$resolvedTest = [IO.Path]::GetFullPath($testRoot)
$leaf = [IO.Path]::GetFileName($resolvedTest)
if (-not $resolvedTest.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase) -or
    -not $leaf.StartsWith('codex-vcom-control-', [StringComparison]::Ordinal)) {
  throw "Unsafe control transport test path: $resolvedTest"
}

$serviceExe = (Resolve-Path "$PSScriptRoot\..\..\src\VComTunnel.Service\bin\Release\net8.0-windows\VComTunnel.Service.exe").Path
$cliExe = (Resolve-Path "$PSScriptRoot\..\..\src\VComTunnel.Cli\bin\Release\net8.0\VComTunnel.Cli.exe").Path
$portProbe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$portProbe.Start()
$testPort = ([Net.IPEndPoint]$portProbe.LocalEndpoint).Port
$portProbe.Stop()
$oldUrl = $env:VCOMTUNNEL_SERVICE_URL
$oldHome = $env:VCOMTUNNEL_HOME
$oldPipe = $env:VCOMTUNNEL_CONTROL_PIPE
$service = $null

New-Item -ItemType Directory -Path $resolvedTest | Out-Null
try {
  $env:VCOMTUNNEL_SERVICE_URL = "http://127.0.0.1:$testPort"
  $env:VCOMTUNNEL_HOME = $resolvedTest
  $env:VCOMTUNNEL_CONTROL_PIPE = "VComTunnel.Control.test-$testId"
  $service = Start-Process `
    -FilePath $serviceExe `
    -ArgumentList '--console' `
    -PassThru `
    -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $resolvedTest 'service.stdout.log') `
    -RedirectStandardError (Join-Path $resolvedTest 'service.stderr.log')

  $status = $null
  for ($attempt = 0; $attempt -lt 40; $attempt++) {
    if ($service.HasExited) {
      break
    }
    try {
      $status = Invoke-WebRequest `
        -UseBasicParsing `
        -Uri "http://127.0.0.1:$testPort/api/status" `
        -TimeoutSec 1
      if ($status.StatusCode -eq 200) {
        break
      }
    } catch {
      Start-Sleep -Milliseconds 250
    }
  }
  if ($null -eq $status -or $status.StatusCode -ne 200) {
    $stdout = Get-Content -LiteralPath (Join-Path $resolvedTest 'service.stdout.log') -Raw -ErrorAction SilentlyContinue
    $stderr = Get-Content -LiteralPath (Join-Path $resolvedTest 'service.stderr.log') -Raw -ErrorAction SilentlyContinue
    throw "Isolated VComTunnel service did not become ready. stdout=$stdout stderr=$stderr"
  }

  $httpMutationStatus = 0
  try {
    Invoke-WebRequest `
      -UseBasicParsing `
      -Method Delete `
      -Uri "http://127.0.0.1:$testPort/api/logs" `
      -TimeoutSec 3 | Out-Null
    $httpMutationStatus = 200
  } catch {
    $httpMutationStatus = [int]$_.Exception.Response.StatusCode
  }
  if ($httpMutationStatus -ne 403) {
    throw "Expected loopback HTTP mutation to return 403, got $httpMutationStatus."
  }

  $cliOutput = & $cliExe control-request --method DELETE --path /api/logs 2>&1
  if ($LASTEXITCODE -ne 0) {
    throw "Protected CLI mutation failed: $($cliOutput -join ' ')"
  }

  Write-Host "PASS control transport: read=$($status.StatusCode) httpMutation=$httpMutationStatus cliMutation=0"
} finally {
  if ($null -ne $service -and -not $service.HasExited) {
    $service.Kill()
    [void]$service.WaitForExit(5000)
  }
  $env:VCOMTUNNEL_SERVICE_URL = $oldUrl
  $env:VCOMTUNNEL_HOME = $oldHome
  $env:VCOMTUNNEL_CONTROL_PIPE = $oldPipe
  if (Test-Path -LiteralPath $resolvedTest) {
    Remove-Item -LiteralPath $resolvedTest -Recurse -Force
  }
}
