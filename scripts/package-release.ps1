param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "dev",
    [string]$OutputRoot = "",
    [string]$DependencyArchiveRoot = "",
    [switch]$SkipBundledDependencies,
    [switch]$Restore
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "artifacts\release"
}

$packageName = "VComTunnel-$Version-$Runtime"
$packageRoot = Join-Path $OutputRoot $packageName
$zipPath = Join-Path $OutputRoot "$packageName.zip"

if (Test-Path $packageRoot) {
    Remove-Item -LiteralPath $packageRoot -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null

$publishProjects = @(
    "src\VComTunnel.Gui\VComTunnel.Gui.csproj",
    "src\VComTunnel.Service\VComTunnel.Service.csproj",
    "src\VComTunnel.Cli\VComTunnel.Cli.csproj"
)

foreach ($project in $publishProjects) {
    $publishArgs = @(
        "publish",
        (Join-Path $repoRoot $project),
        "-c",
        $Configuration,
        "-r",
        $Runtime,
        "--self-contained",
        "false",
        "-o",
        $packageRoot
    )
    if (-not $Restore) {
        $publishArgs += "--no-restore"
    }

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $project with exit code $LASTEXITCODE."
    }
}

$launcher = @"
@echo off
setlocal
"%~dp0VComTunnel.Cli.exe" %*
"@
Set-Content -LiteralPath (Join-Path $packageRoot "vcomtunnelctl.cmd") -Value $launcher -Encoding ASCII

$dependencyArchives = @(
    @{
        Name = "hub4com-2.1.0.0-386.zip"
        Url = "https://sourceforge.net/projects/com0com/files/hub4com/2.1.0.0/hub4com-2.1.0.0-386.zip/download"
        Description = "hub4com 2.1.0.0 RFC2217 bridge tools"
    },
    @{
        Name = "com0com-3.0.0.0-i386-and-x64-signed.zip"
        Url = "https://sourceforge.net/projects/com0com/files/com0com/3.0.0.0/com0com-3.0.0.0-i386-and-x64-signed.zip/download"
        Description = "com0com 3.0.0.0 signed installer package"
    }
)

if (-not $SkipBundledDependencies) {
    $dependenciesDir = Join-Path $packageRoot "dependencies"
    New-Item -ItemType Directory -Force -Path $dependenciesDir | Out-Null

    foreach ($archive in $dependencyArchives) {
        $target = Join-Path $dependenciesDir $archive.Name
        $source = if ([string]::IsNullOrWhiteSpace($DependencyArchiveRoot)) {
            $null
        } else {
            Join-Path $DependencyArchiveRoot $archive.Name
        }

        if ($source -and (Test-Path $source)) {
            Copy-Item -LiteralPath $source -Destination $target -Force
        } else {
            Invoke-WebRequest -Uri $archive.Url -OutFile $target
        }
    }
}

$noticeLines = @(
    "VComTunnel third-party dependency archives",
    "",
    "This release package may include unmodified upstream archives used by VComTunnel dependency setup.",
    "hub4com is extracted into the VComTunnel tools cache at first setup.",
    "com0com is a Windows driver package and still requires an interactive elevated installer run.",
    ""
)

foreach ($archive in $dependencyArchives) {
    $noticeLines += "- $($archive.Description)"
    $noticeLines += "  Archive: $($archive.Name)"
    $noticeLines += "  Source: $($archive.Url)"
}

Set-Content -LiteralPath (Join-Path $packageRoot "THIRD-PARTY-NOTICES.txt") -Value $noticeLines -Encoding UTF8

$hashLines = Get-ChildItem -LiteralPath $packageRoot -Recurse -File |
    Sort-Object FullName |
    ForEach-Object {
        $relative = $_.FullName.Substring($packageRoot.Length).TrimStart("\", "/")
        $hash = Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName
        "$($hash.Hash)  $relative"
    }
Set-Content -LiteralPath (Join-Path $packageRoot "SHA256SUMS.txt") -Value $hashLines -Encoding ASCII

if (Test-Path $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Compress-Archive -Path (Join-Path $packageRoot "*") -DestinationPath $zipPath -Force

Write-Host "Release package: $packageRoot"
Write-Host "Archive: $zipPath"
