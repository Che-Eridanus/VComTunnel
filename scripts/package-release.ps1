param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "dev",
    [string]$OutputRoot = "",
    [string]$DependencyArchiveRoot = "",
    [switch]$SkipBundledDependencies,
    [switch]$Restore,
    [switch]$FrameworkDependent
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$repoDependencyArchiveRoot = Join-Path $repoRoot "third_party\dependencies"
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "artifacts\release"
}

$packageFlavor = if ($FrameworkDependent) { "framework-dependent" } else { "portable" }
$packageName = "VComTunnel-$Version-$Runtime-$packageFlavor"
$packageRoot = Join-Path $OutputRoot $packageName
$zipPath = Join-Path $OutputRoot "$packageName.zip"
$selfContained = if ($FrameworkDependent) { "false" } else { "true" }

if (-not $Restore) {
    Write-Host "Using --no-restore. Ensure packages were restored for runtime '$Runtime', or pass -Restore."
}

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
        $selfContained,
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

$releaseDocs = @(
    "LICENSE",
    "README.md",
    "README.zh-CN.md",
    "SECURITY.md"
)

foreach ($doc in $releaseDocs) {
    $sourceDoc = Join-Path $repoRoot $doc
    if (Test-Path $sourceDoc) {
        Copy-Item -LiteralPath $sourceDoc -Destination (Join-Path $packageRoot $doc) -Force
    }
}

$launcher = @"
@echo off
setlocal
"%~dp0VComTunnel.Cli.exe" %*
"@
Set-Content -LiteralPath (Join-Path $packageRoot "vcomtunnelctl.cmd") -Value $launcher -Encoding ASCII

$startGui = @"
@echo off
setlocal
start "" "%~dp0VComTunnel.Gui.exe"
"@
Set-Content -LiteralPath (Join-Path $packageRoot "Start-VComTunnel.cmd") -Value $startGui -Encoding ASCII

$startPortable = @"
@echo off
setlocal
set "VCOMTUNNEL_HOME=%~dp0data"
if not exist "%VCOMTUNNEL_HOME%" mkdir "%VCOMTUNNEL_HOME%"
start "" "%~dp0VComTunnel.Gui.exe"
"@
Set-Content -LiteralPath (Join-Path $packageRoot "Start-VComTunnel-Portable.cmd") -Value $startPortable -Encoding ASCII

$setupDependencies = @"
@echo off
setlocal
set "VCOMTUNNEL_HOME=%~dp0data"
if not exist "%VCOMTUNNEL_HOME%" mkdir "%VCOMTUNNEL_HOME%"
"%~dp0VComTunnel.Cli.exe" deps install
set "install_exit=%ERRORLEVEL%"
echo.
if not "%install_exit%"=="0" (
    echo Dependency preparation reported errors. Review the output above.
    pause
    exit /b %install_exit%
)
echo com0com is a Windows driver and may require UAC approval.
choice /C YN /M "Launch the com0com driver installer now"
if errorlevel 2 goto done
"%~dp0VComTunnel.Cli.exe" deps launch-com0com
:done
echo.
pause
"@
Set-Content -LiteralPath (Join-Path $packageRoot "Setup-Dependencies-Portable.cmd") -Value $setupDependencies -Encoding ASCII

$startConsoleService = @"
@echo off
setlocal
set "VCOMTUNNEL_HOME=%~dp0data"
if not exist "%VCOMTUNNEL_HOME%" mkdir "%VCOMTUNNEL_HOME%"
"%~dp0VComTunnel.Service.exe" --console
"@
Set-Content -LiteralPath (Join-Path $packageRoot "Start-Service-Console-Portable.cmd") -Value $startConsoleService -Encoding ASCII

$installServiceCmd = @"
@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Windows-Service.ps1"
"@
Set-Content -LiteralPath (Join-Path $packageRoot "Install-Windows-Service.cmd") -Value $installServiceCmd -Encoding ASCII

$uninstallServiceCmd = @"
@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall-Windows-Service.ps1"
"@
Set-Content -LiteralPath (Join-Path $packageRoot "Uninstall-Windows-Service.cmd") -Value $uninstallServiceCmd -Encoding ASCII

$installServicePs1 = @'
$ErrorActionPreference = "Stop"

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Administrator)) {
    $script = $PSCommandPath.Replace('"', '\"')
    Start-Process -FilePath "powershell.exe" -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$script`"" -Verb RunAs
    exit
}

$root = Split-Path -Parent $PSCommandPath
$cli = Join-Path $root "VComTunnel.Cli.exe"
$service = Join-Path $root "VComTunnel.Service.exe"

& $cli service install $service
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

& $cli service start
exit $LASTEXITCODE
'@
Set-Content -LiteralPath (Join-Path $packageRoot "Install-Windows-Service.ps1") -Value $installServicePs1 -Encoding UTF8

$uninstallServicePs1 = @'
$ErrorActionPreference = "Stop"

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Administrator)) {
    $script = $PSCommandPath.Replace('"', '\"')
    Start-Process -FilePath "powershell.exe" -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$script`"" -Verb RunAs
    exit
}

$root = Split-Path -Parent $PSCommandPath
$cli = Join-Path $root "VComTunnel.Cli.exe"

& $cli service stop
& $cli service uninstall
exit $LASTEXITCODE
'@
Set-Content -LiteralPath (Join-Path $packageRoot "Uninstall-Windows-Service.ps1") -Value $uninstallServicePs1 -Encoding UTF8

$dependencyArchives = @(
    @{
        Name = "hub4com-2.1.0.0-386.zip"
        Url = "https://sourceforge.net/projects/com0com/files/hub4com/2.1.0.0/hub4com-2.1.0.0-386.zip/download"
        Urls = @(
            "https://downloads.sourceforge.net/project/com0com/hub4com/2.1.0.0/hub4com-2.1.0.0-386.zip?use_mirror=cytranet",
            "https://downloads.sourceforge.net/project/com0com/hub4com/2.1.0.0/hub4com-2.1.0.0-386.zip",
            "https://sourceforge.net/projects/com0com/files/hub4com/2.1.0.0/hub4com-2.1.0.0-386.zip/download",
            "https://netix.dl.sourceforge.net/project/com0com/hub4com/2.1.0.0/hub4com-2.1.0.0-386.zip",
            "https://cytranet.dl.sourceforge.net/project/com0com/hub4com/2.1.0.0/hub4com-2.1.0.0-386.zip"
        )
        Description = "hub4com 2.1.0.0 RFC2217 bridge tools"
        ExpectedFiles = @("hub4com.exe", "com2tcp-rfc2217.bat")
        Sha256 = "24CCA36CCF0CAB0F988BB59851B5EC947667EFE53C4F43F290392AD308AC0E01"
    },
    @{
        Name = "com0com-3.0.0.0-i386-and-x64-signed.zip"
        Url = "https://sourceforge.net/projects/com0com/files/com0com/3.0.0.0/com0com-3.0.0.0-i386-and-x64-signed.zip/download"
        Urls = @(
            "https://downloads.sourceforge.net/project/com0com/com0com/3.0.0.0/com0com-3.0.0.0-i386-and-x64-signed.zip?use_mirror=psychz",
            "https://downloads.sourceforge.net/project/com0com/com0com/3.0.0.0/com0com-3.0.0.0-i386-and-x64-signed.zip",
            "https://sourceforge.net/projects/com0com/files/com0com/3.0.0.0/com0com-3.0.0.0-i386-and-x64-signed.zip/download",
            "https://netix.dl.sourceforge.net/project/com0com/com0com/3.0.0.0/com0com-3.0.0.0-i386-and-x64-signed.zip",
            "https://pilotfiber.dl.sourceforge.net/project/com0com/com0com/3.0.0.0/com0com-3.0.0.0-i386-and-x64-signed.zip"
        )
        Description = "com0com 3.0.0.0 signed installer package"
        ExpectedFiles = @("Setup_com0com_v3.0.0.0_W7_x64_signed.exe", "Setup_com0com_v3.0.0.0_W7_x86_signed.exe")
        Sha256 = "6E5D4359865277430D4AE88C73FB7E648A0ED8E81AEA5002478179CFCB0BB0E1"
    }
)

function Assert-DependencyArchive {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string[]]$ExpectedFiles,
        [string]$Sha256
    )

    try {
        if (-not [string]::IsNullOrWhiteSpace($Sha256)) {
            $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash
            if (-not [string]::Equals($actualHash, $Sha256, [StringComparison]::OrdinalIgnoreCase)) {
                throw "SHA256 mismatch: expected $Sha256, got $actualHash"
            }
        }

        $zip = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Path).Path)
        try {
            $entryNames = @(
                $zip.Entries |
                    ForEach-Object { [System.IO.Path]::GetFileName($_.FullName.Replace('\', '/')) } |
                    Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
            )

            $missing = @($ExpectedFiles | Where-Object { $entryNames -notcontains $_ })
            if ($missing.Count -eq 0) {
                return
            }

            throw "missing expected file(s): $($missing -join ', ')"
        } finally {
            $zip.Dispose()
        }
    } catch {
        throw "Dependency archive '$Path' is not a valid release zip or is missing required files. SourceForge may have returned an HTML download page. Details: $($_.Exception.Message)"
    }
}

function Save-DependencyArchive {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Archive,
        [Parameter(Mandatory = $true)]
        [string]$Target,
        [string]$Source
    )

    if ($Source -and (Test-Path $Source)) {
        Copy-Item -LiteralPath $Source -Destination $Target -Force
        Assert-DependencyArchive -Path $Target -ExpectedFiles $Archive.ExpectedFiles -Sha256 $Archive.Sha256
        return
    }

    if ($Source) {
        throw "Dependency archive source was specified but does not exist: $Source"
    }

    $repoSource = Join-Path $repoDependencyArchiveRoot $Archive.Name
    if (Test-Path -LiteralPath $repoSource) {
        Copy-Item -LiteralPath $repoSource -Destination $Target -Force
        Assert-DependencyArchive -Path $Target -ExpectedFiles $Archive.ExpectedFiles -Sha256 $Archive.Sha256
        return
    }

    $errors = @()
    foreach ($url in $Archive.Urls) {
        try {
            Invoke-WebRequest -Uri $url -OutFile $Target -UserAgent "Wget/1.21.4"
            Assert-DependencyArchive -Path $Target -ExpectedFiles $Archive.ExpectedFiles -Sha256 $Archive.Sha256
            return
        } catch {
            $errors += "$url => $($_.Exception.Message)"
            if (Test-Path -LiteralPath $Target) {
                Remove-Item -LiteralPath $Target -Force
            }
        }
    }

    throw "Could not download a valid dependency archive '$($Archive.Name)'. Tried: $($errors -join ' | ')"
}

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

        Save-DependencyArchive -Archive $archive -Target $target -Source $source
    }
}

$kmdfDriverPackage = Join-Path $repoRoot "drivers\VComTunnel.Serial\x64\Release\VComTunnel.Serial"
$kmdfDriverCertificate = Join-Path $repoRoot "drivers\VComTunnel.Serial\x64\Release\VComTunnel.Serial.cer"
$kmdfDriverInf = Join-Path $kmdfDriverPackage "VComTunnel.Serial.inf"
$kmdfDriverSys = Join-Path $kmdfDriverPackage "VComTunnel.Serial.sys"
$kmdfDriverCat = if (Test-Path -LiteralPath $kmdfDriverPackage) {
    Get-ChildItem -LiteralPath $kmdfDriverPackage -Filter "*.cat" -File | Select-Object -First 1
} else {
    $null
}

if ((Test-Path -LiteralPath $kmdfDriverInf) -and (Test-Path -LiteralPath $kmdfDriverSys) -and $kmdfDriverCat) {
    $targetKmdfDriverPackage = Join-Path $packageRoot "drivers\VComTunnel.Serial\x64\Release\VComTunnel.Serial"
    New-Item -ItemType Directory -Force -Path $targetKmdfDriverPackage | Out-Null
    Get-ChildItem -LiteralPath $kmdfDriverPackage -Force |
        Copy-Item -Destination $targetKmdfDriverPackage -Recurse -Force
    if (Test-Path -LiteralPath $kmdfDriverCertificate) {
        Copy-Item -LiteralPath $kmdfDriverCertificate -Destination $targetKmdfDriverPackage -Force
    }
} else {
    Write-Warning "KMDF driver package was not found under drivers\VComTunnel.Serial\x64\Release\VComTunnel.Serial. Build the KMDF driver before packaging if KMDF add/update should work from this package."
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
    $noticeLines += "  SHA256: $($archive.Sha256)"
}

Set-Content -LiteralPath (Join-Path $packageRoot "THIRD-PARTY-NOTICES.txt") -Value $noticeLines -Encoding UTF8

$runtimeLine = if ($FrameworkDependent) {
    "This is a framework-dependent package. Install the .NET 8 Desktop Runtime and ASP.NET Core Runtime before running it."
} else {
    "This is a self-contained package. It does not require a separate .NET runtime installation."
}

$firstReadme = @(
    "VComTunnel release package",
    "",
    $runtimeLine,
    "Supported OS: Windows 10/11 x64. Windows 7, Windows 8, and Windows 8.1 are not supported by this .NET 8 / WPF release line.",
    "Self-contained packaging only bundles the .NET runtime; it does not expand OS support.",
    "",
    "Portable use:",
    "1. Extract this folder to a writable location.",
    "2. Run Start-VComTunnel-Portable.cmd.",
    "3. Use Setup deps in the GUI, or run Setup-Dependencies-Portable.cmd.",
    "4. Approve the com0com driver installer when Windows asks. The app files are portable, but the virtual COM driver is still a system-level install.",
    "",
    "Installed service use:",
    "1. Run Install-Windows-Service.cmd and approve UAC.",
    "2. Start VComTunnel.Gui.exe or Start-VComTunnel.cmd to manage mappings.",
    "3. Run Uninstall-Windows-Service.cmd to remove the Windows service.",
    "",
    "Safety notes:",
    "- The stable release path is com0comHub4com.",
    "- The KMDF backend in this package is test-signed and intended for authorized evaluation or internal validation.",
    "- Creating or updating a KMDF port may add the bundled VComTunnel.Serial test certificate to the local machine certificate stores, install or update the driver, and require a reboot.",
    "- Keep the local API on 127.0.0.1 and test DTR/RTS/BREAK behavior on safe hardware first."
)
Set-Content -LiteralPath (Join-Path $packageRoot "README-FIRST.txt") -Value $firstReadme -Encoding UTF8

function ConvertFrom-Utf8Base64 {
    param([Parameter(Mandatory = $true)][string]$Value)
    return [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($Value))
}

$firstReadmeZh = @(
    (ConvertFrom-Utf8Base64 "VkNvbVR1bm5lbCDlj5HluIPljIU="),
    "",
    $(if ($FrameworkDependent) { ConvertFrom-Utf8Base64 "6L+Z5pivIGZyYW1ld29yay1kZXBlbmRlbnQg5YyF77yM6L+Q6KGM5YmN6ZyA6KaB5a6J6KOFIC5ORVQgOCBEZXNrdG9wIFJ1bnRpbWUg5ZKMIEFTUC5ORVQgQ29yZSBSdW50aW1l44CC" } else { ConvertFrom-Utf8Base64 "6L+Z5pivIHNlbGYtY29udGFpbmVkIOWMhe+8jOS4jemcgOimgeeUqOaIt+WNleeLrOWuieijhSAuTkVUIOi/kOihjOaXtuOAgg==" }),
    (ConvertFrom-Utf8Base64 "5pSv5oyB57O757uf77yaV2luZG93cyAxMC8xMSB4NjTjgIJXaW4344CBV2luOOOAgVdpbjguMSDkuI3lsZ7kuo7lvZPliY0gLk5FVCA4IC8gV1BGIOWPkeW4g+e6v+eahOaUr+aMgeiMg+WbtOOAgg=="),
    (ConvertFrom-Utf8Base64 "c2VsZi1jb250YWluZWQg5Y+q6KGo56S65Y+R5biD5YyF5YaF572uIC5ORVQg6L+Q6KGM5pe277yM5LiN5Lya5omp5aSn5pON5L2c57O757uf5YW85a656IyD5Zu044CC"),
    "",
    (ConvertFrom-Utf8Base64 "5L6/5pC65L2/55So77ya"),
    (ConvertFrom-Utf8Base64 "MS4g6Kej5Y6L5Yiw5LiA5Liq5b2T5YmN55So5oi35Y+v5YaZ55qE55uu5b2V44CC"),
    (ConvertFrom-Utf8Base64 "Mi4g6L+Q6KGMIFN0YXJ0LVZDb21UdW5uZWwtUG9ydGFibGUuY21k44CC"),
    (ConvertFrom-Utf8Base64 "My4g5ZyoIEdVSSDph4zngrnlh7sgU2V0dXAgZGVwc++8jOaIlui/kOihjCBTZXR1cC1EZXBlbmRlbmNpZXMtUG9ydGFibGUuY21k44CC"),
    (ConvertFrom-Utf8Base64 "NC4gV2luZG93cyDor7fmsYLlronoo4UgY29tMGNvbSDpqbHliqjml7bpnIDopoHnlKjmiLfnoa7orqTjgILlupTnlKjjgIHphY3nva7jgIHml6Xlv5flkozlt6XlhbfnvJPlrZjlj6/ku6Xkvr/mkLrvvIzkvYbomZrmi5/kuLLlj6PpqbHliqjku43nhLbmmK/ns7vnu5/nuqflronoo4XjgII="),
    "",
    (ConvertFrom-Utf8Base64 "5a6J6KOF5Li65ZCO5Y+w5pyN5Yqh77ya"),
    (ConvertFrom-Utf8Base64 "MS4g6L+Q6KGMIEluc3RhbGwtV2luZG93cy1TZXJ2aWNlLmNtZCDlubbnoa7orqQgVUFD44CC"),
    (ConvertFrom-Utf8Base64 "Mi4g55SoIFZDb21UdW5uZWwuR3VpLmV4ZSDmiJYgU3RhcnQtVkNvbVR1bm5lbC5jbWQg566h55CG5pig5bCE44CC"),
    (ConvertFrom-Utf8Base64 "My4g6L+Q6KGMIFVuaW5zdGFsbC1XaW5kb3dzLVNlcnZpY2UuY21kIOWIoOmZpCBXaW5kb3dzIFNlcnZpY2XjgII="),
    "",
    (ConvertFrom-Utf8Base64 "5a6J5YWo6L6555WM77ya"),
    (ConvertFrom-Utf8Base64 "LSDnqLPlrprlj5HluIPot6/lvoTmmK8gY29tMGNvbUh1YjRjb23jgII="),
    (ConvertFrom-Utf8Base64 "LSDmnKzljIXlhoUgS01ERiDlkI7nq6/kuLrmtYvor5Xnrb7lkI3niYjmnKzvvIzku4XnlKjkuo7mjojmnYPmtYvor5XmiJblhoXpg6jpqozor4HjgII="),
    (ConvertFrom-Utf8Base64 "LSDliJvlu7rmiJbmm7TmlrAgS01ERiDnq6/lj6Pml7bvvIznqIvluo/lj6/og73kvJrlsIbpmo/ljIUgVkNvbVR1bm5lbC5TZXJpYWwg5rWL6K+V6K+B5Lmm5YaZ5YWl5pys5py66K+B5Lmm5a2Y5YKo5Yy677yM5a6J6KOF5oiW5pu05paw6amx5Yqo77yM5bm26KaB5rGC6YeN5ZCv44CC"),
    (ConvertFrom-Utf8Base64 "LSDmnKzmnLogQVBJIOWPquW6lOS9v+eUqCAxMjcuMC4wLjHvvIxEVFIvUlRTL0JSRUFLIOetieaOp+WItue6v+ihjOS4uuWFiOWcqOWuieWFqOehrOS7tuS4iumqjOivgeOAgg==")
)
Set-Content -LiteralPath (Join-Path $packageRoot "README-FIRST.zh-CN.txt") -Value $firstReadmeZh -Encoding UTF8

$packageRootFull = (Resolve-Path -LiteralPath $packageRoot).Path
$hashLines = Get-ChildItem -LiteralPath $packageRootFull -Recurse -File |
    Sort-Object FullName |
    ForEach-Object {
        $relative = $_.FullName.Substring($packageRootFull.Length).TrimStart(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar)
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
