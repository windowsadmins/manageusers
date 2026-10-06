#!/usr/bin/env pwsh
# ManageUsers Build Script
# Builds, signs, and packages ManageUsers binary for Cimian deployment
#
# Examples:
#   .\build.ps1                          # Build, sign, and create .msi (default)
#   .\build.ps1 -Msi                     # Package existing binaries into .msi (skip build)
#   .\build.ps1 -Nupkg                   # Also create .nupkg (Chocolatey) packages
#   .\build.ps1 -Thumbprint "ABC123..."  # Build with specific certificate
#   .\build.ps1 -AllowUnsigned           # Development build without signing (NOT for production)
#   .\build.ps1 -Architecture arm64      # Build single architecture
#   .\build.ps1 -ListCerts               # List available code signing certificates
#   .\build.ps1 -Clean                   # Clean build output first
#   .\build.ps1 -SkipApp                 # CLI only, without the Managed Users Cleanup app

[CmdletBinding()]
param(
    [string]$Thumbprint,
    [ValidateSet('x64', 'arm64', 'both')]
    [string]$Architecture = 'both',
    [switch]$Clean,
    [switch]$AllowUnsigned,
    [switch]$ListCerts,
    [string]$FindCertSubject,
    [switch]$Msi,
    [switch]$Nupkg,
    [switch]$SkipApp
)

$ErrorActionPreference = 'Stop'
$RootDir = $PSScriptRoot
$ProjectPath = Join-Path $RootDir 'src' 'ManageUsers' 'ManageUsers.csproj'
$AppProjectDir = Join-Path $RootDir 'src' 'ManageUsers.App'
$AppProjectPath = Join-Path $AppProjectDir 'ManageUsers.App.csproj'
# The app installs beside manageusers.exe in ManageUsers' own folder.
$AppExeName = 'Managed Users Cleanup.exe'
$OutputDir = Join-Path $RootDir 'release'
$Configuration = 'Release'
$TimeStampServer = 'http://timestamp.digicert.com'

# Timestamp-based versioning
$Version = Get-Date -Format 'yyyy.MM.dd.HHmm'

# --- Certificate management functions ---

function Find-CodeSigningCerts {
    param([string]$SubjectFilter = '')

    $certs = @()
    $stores = @('Cert:\CurrentUser\My', 'Cert:\LocalMachine\My')

    foreach ($store in $stores) {
        $storeCerts = Get-ChildItem $store -ErrorAction SilentlyContinue | Where-Object {
            ($_.EnhancedKeyUsageList -like '*Code Signing*' -or $_.HasPrivateKey) -and
            $_.NotAfter -gt (Get-Date) -and
            ($SubjectFilter -eq '' -or $_.Subject -like "*$SubjectFilter*")
        }
        if ($storeCerts) {
            $certs += $storeCerts | Select-Object *, @{Name='Store'; Expression={$store}}
        }
    }

    return $certs | Sort-Object NotAfter -Descending
}

function Show-CertificateList {
    $certs = Find-CodeSigningCerts
    if ($certs) {
        Write-Host 'Available code signing certificates:' -ForegroundColor Green
        for ($i = 0; $i -lt $certs.Count; $i++) {
            $cert = $certs[$i]
            Write-Host ''
            Write-Host "[$($i + 1)] Subject: $($cert.Subject)" -ForegroundColor Cyan
            Write-Host "    Issuer:  $($cert.Issuer)" -ForegroundColor Gray
            Write-Host "    Thumbprint: $($cert.Thumbprint)" -ForegroundColor Yellow
            Write-Host "    Valid Until: $($cert.NotAfter)" -ForegroundColor Gray
            Write-Host "    Store: $($cert.Store)" -ForegroundColor Gray
        }
        Write-Host ''
    } else {
        Write-Host 'No valid code signing certificates found' -ForegroundColor Yellow
    }
    return $certs
}

function Get-BestCertificate {
    $certs = Find-CodeSigningCerts

    # First priority: Enterprise certificate (configured via SIGNING_CERT_SUBJECT)
    $enterpriseCert = $certs | Where-Object { $_.Subject -like "*$(if ($env:SIGNING_CERT_SUBJECT) { $env:SIGNING_CERT_SUBJECT } else { 'unset-signing-cert-subject' })*" } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
    if ($enterpriseCert) { return $enterpriseCert }

    # Fallback: prefer CurrentUser, newest expiration
    return $certs | Sort-Object @{Expression={$_.Store -eq 'Cert:\CurrentUser\My'}; Descending=$true}, NotAfter -Descending | Select-Object -First 1
}

# --- Signing functions ---

function Test-SignTool {
    $c = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($c) {
        Write-Log "Found signtool.exe: $($c.Source)" 'SUCCESS'
        return
    }

    Write-Log 'signtool.exe not in PATH, searching Windows SDK...' 'INFO'

    $roots = @(
        "$env:ProgramFiles\Windows Kits\10\bin",
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    ) | Where-Object { Test-Path $_ }

    try {
        $kitsRoot = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots' -EA Stop).KitsRoot10
        if ($kitsRoot) {
            $binPath = Join-Path $kitsRoot 'bin'
            if (Test-Path $binPath) { $roots += $binPath }
        }
    } catch { }

    foreach ($root in $roots) {
        $patterns = @(
            (Join-Path $root '*\x64\signtool.exe'),
            (Join-Path $root '*\arm64\signtool.exe')
        )
        foreach ($pattern in $patterns) {
            $found = Get-ChildItem -Path $pattern -EA SilentlyContinue | Sort-Object LastWriteTime -Desc | Select-Object -First 1
            if ($found) {
                $env:Path = "$($found.Directory.FullName);$env:Path"
                Write-Log "Found signtool.exe: $($found.FullName)" 'SUCCESS'
                return
            }
        }
    }

    throw 'signtool.exe not found. Install Windows 10/11 SDK with Signing Tools.'
}

function Invoke-SignArtifact {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$CertThumbprint,
        [string]$Store = 'Cert:\CurrentUser\My',
        [int]$MaxAttempts = 3
    )

    if (-not (Test-Path -LiteralPath $Path)) { throw "File not found: $Path" }

    $fileName = [System.IO.Path]::GetFileName($Path)
    Write-Log "Signing: $fileName" 'INFO'

    $tsas = @(
        'http://timestamp.digicert.com',
        'http://timestamp.sectigo.com',
        'http://timestamp.entrust.net/TSS/RFC3161sha2TS',
        'http://timestamp.comodoca.com/authenticode'
    )

    $storeArgs = if ($Store -match 'LocalMachine') { @('/s', 'My', '/sm') } else { @('/s', 'My') }

    $attempt = 0
    $lastError = $null

    while ($attempt -lt $MaxAttempts) {
        $attempt++

        foreach ($tsa in $tsas) {
            try {
                $signArgs = @('sign') + $storeArgs + @(
                    '/sha1', $CertThumbprint,
                    '/fd', 'SHA256',
                    '/td', 'SHA256',
                    '/tr', $tsa,
                    $Path
                )

                & signtool.exe @signArgs 2>&1 | Out-Null
                if ($LASTEXITCODE -eq 0) {
                    # Verify
                    & signtool.exe verify /pa $Path 2>&1 | Out-Null
                    if ($LASTEXITCODE -eq 0) {
                        Write-Log "Signed and verified: $fileName" 'SUCCESS'
                        return
                    }
                    Write-Log 'Signature verification failed' 'WARN'
                }

                $lastError = "signtool exit code: $LASTEXITCODE"
                Write-Log "TSA $tsa failed: $lastError" 'WARN'
                Start-Sleep -Seconds 2

            } catch {
                $lastError = $_.Exception.Message
                Write-Log "Exception with TSA ${tsa}: $lastError" 'WARN'
                Start-Sleep -Seconds 2
            }
        }

        if ($attempt -lt $MaxAttempts) {
            $wait = 4 * $attempt
            Write-Log "Retrying in ${wait}s..." 'WARN'
            Start-Sleep -Seconds $wait
        }
    }

    throw "Signing failed after $MaxAttempts attempts. Last error: $lastError"
}

# --- Logging ---

function Write-Log {
    param(
        [string]$Message,
        [ValidateSet('INFO', 'WARN', 'ERROR', 'SUCCESS')]
        [string]$Level = 'INFO'
    )
    $color = switch ($Level) {
        'SUCCESS' { 'Green' }
        'WARN'    { 'Yellow' }
        'ERROR'   { 'Red' }
        default   { 'Cyan' }
    }
    Write-Host "[$Level] $Message" -ForegroundColor $color
}

# --- Handle cert management commands ---

if ($ListCerts) {
    Show-CertificateList | Out-Null
    return
}

if ($FindCertSubject) {
    Write-Host "Searching for certificates containing: $FindCertSubject" -ForegroundColor Green
    $certs = Find-CodeSigningCerts -SubjectFilter $FindCertSubject
    if ($certs) {
        for ($i = 0; $i -lt $certs.Count; $i++) {
            $cert = $certs[$i]
            Write-Host ''
            Write-Host "[$($i + 1)] Subject: $($cert.Subject)" -ForegroundColor Cyan
            Write-Host "    Thumbprint: $($cert.Thumbprint)" -ForegroundColor Yellow
            Write-Host "    Valid Until: $($cert.NotAfter)" -ForegroundColor Gray
            Write-Host "    Store: $($cert.Store)" -ForegroundColor Gray
        }
    } else {
        Write-Host "No certificates found matching: $FindCertSubject" -ForegroundColor Yellow
    }
    return
}

# Generates resources.pri and copies XBF binary XAML files to the app's publish output.
#
# EnableCoreMrtTooling=false is set in ManageUsers.App.csproj because the standard PriGen
# step needs the Visual Studio UWP workload. This replicates it: copy the XBF files from
# obj\ beside the exe, then run makepri.exe over them plus the WinUI framework PRI files,
# whose embedded resources (themeresources.xbf and so on) it merges into resources.pri.
function Publish-AppResources {
    param(
        [Parameter(Mandatory)][string]$Arch,
        [Parameter(Mandatory)][string]$OutputDir,
        [Parameter(Mandatory)][string]$AppProjectDir
    )

    Write-Log "Generating XAML resources (XBF + resources.pri) for the app ($Arch)..." 'INFO'

    $makepri = $null
    $sdkBinRoots = @(
        "$env:ProgramFiles\Windows Kits\10\bin",
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    ) | Where-Object { Test-Path $_ }

    # makepri.exe runs on the build host, so prefer the host's architecture.
    $hostArch = switch ($env:PROCESSOR_ARCHITECTURE) {
        'AMD64' { 'x64' }
        'ARM64' { 'arm64' }
        default { 'x86' }
    }
    $toolArchOrder = @($hostArch) + (@('x64', 'arm64', 'x86') | Where-Object { $_ -ne $hostArch })

    foreach ($root in $sdkBinRoots) {
        foreach ($toolArch in $toolArchOrder) {
            $candidates = Get-ChildItem "$root\*\$toolArch\makepri.exe" -ErrorAction SilentlyContinue |
                Sort-Object { [version]($_.FullName -replace '.*\\(\d+\.\d+\.\d+\.\d+)\\.*', '$1') } -Descending |
                Select-Object -First 1
            if ($candidates) { $makepri = $candidates.FullName; break }
        }
        if ($makepri) { break }
    }

    if (-not $makepri) {
        Write-Log 'makepri.exe not found in the Windows SDK: install the Windows 10/11 SDK' 'ERROR'
        return $false
    }

    $xbfFiles = Get-ChildItem "$AppProjectDir\obj\Release" -Recurse -Filter '*.xbf' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match [regex]::Escape("\win-$Arch\") }
    if (-not $xbfFiles) {
        Write-Log "No XBF files found in obj\Release for win-$Arch" 'ERROR'
        return $false
    }
    $xbfRootPath = ($xbfFiles[0].FullName -split [regex]::Escape("\win-$Arch\"))[0] + "\win-$Arch"

    $stagingDir = Join-Path ([System.IO.Path]::GetTempPath()) "manageusers-pri-$Arch"
    if (Test-Path $stagingDir) { Remove-Item $stagingDir -Recurse -Force }
    New-Item -ItemType Directory $stagingDir | Out-Null

    # MRT resolves the XBF paths in resources.pri relative to the exe, so they are needed
    # in the output as well as in the staging folder makepri indexes.
    foreach ($xbf in $xbfFiles) {
        $relativePath = $xbf.FullName.Substring($xbfRootPath.Length).TrimStart('\')
        foreach ($dest in @((Join-Path $stagingDir $relativePath), (Join-Path $OutputDir $relativePath))) {
            $destDir = Split-Path $dest
            if (-not (Test-Path $destDir)) { New-Item -ItemType Directory $destDir | Out-Null }
            Copy-Item $xbf.FullName $dest -Force
        }
    }

    foreach ($pri in Get-ChildItem $OutputDir -Filter 'Microsoft.*.pri') {
        Copy-Item $pri.FullName (Join-Path $stagingDir $pri.Name) -Force
    }

    $priconfigPath = Join-Path $stagingDir 'priconfig.xml'
    & $makepri createconfig /cf $priconfigPath /dq 'en-US' /pv '10.0.0' /o 2>&1 | Out-Null
    $outPriPath = Join-Path $OutputDir 'resources.pri'
    $priOutput = & $makepri new /pr $stagingDir /cf $priconfigPath /in 'ManageUsers' /of $outPriPath /o 2>&1
    $ok = $LASTEXITCODE -eq 0
    if (-not $ok) { Write-Log "makepri.exe failed (exit $LASTEXITCODE): $priOutput" 'ERROR' }
    else { Write-Log "Generated resources.pri ($($xbfFiles.Count) XBF file(s))" 'SUCCESS' }

    Remove-Item $stagingDir -Recurse -Force -ErrorAction SilentlyContinue
    return $ok
}

# --- Main build ---

Write-Host ''
Write-Host '=== ManageUsers Build ===' -ForegroundColor Magenta
Write-Host "Version:       $Version" -ForegroundColor Yellow
Write-Host "Architecture:  $Architecture" -ForegroundColor Yellow
Write-Host "Code Signing:  $(if ($Msi -or $AllowUnsigned) { 'DISABLED' } else { 'REQUIRED' })" -ForegroundColor $(if ($Msi -or $AllowUnsigned) { 'Gray' } else { 'Green' })
Write-Host "Package:       .msi$(if ($Nupkg) { ' + .nupkg' } else { '' }) via cimipkg" -ForegroundColor Green
if ($Msi) {
    Write-Host ''
    Write-Host 'Package-only mode — using existing binaries from release/' -ForegroundColor Yellow
}
if ($AllowUnsigned -and -not $Msi) {
    Write-Host ''
    Write-Host 'WARNING: Unsigned build - NOT suitable for production deployment' -ForegroundColor Red
}
Write-Host ''

# Auto-detect signing certificate
$SigningCert = $null
if (-not $AllowUnsigned -and -not $Msi) {
    if ($Thumbprint) {
        $stores = @('Cert:\CurrentUser\My', 'Cert:\LocalMachine\My')
        foreach ($store in $stores) {
            $cert = Get-ChildItem "$store\$Thumbprint" -ErrorAction SilentlyContinue
            if ($cert) {
                $SigningCert = @{ Thumbprint = $cert.Thumbprint; Store = $store }
                Write-Log "Using specified certificate: $($cert.Subject)" 'SUCCESS'
                break
            }
        }
        if (-not $SigningCert) { throw "Certificate with thumbprint $Thumbprint not found" }
    } else {
        $bestCert = Get-BestCertificate
        if ($bestCert) {
            $SigningCert = @{ Thumbprint = $bestCert.Thumbprint; Store = $bestCert.Store }
            Write-Log "Auto-detected certificate: $($bestCert.Subject)" 'SUCCESS'
            Write-Log "Thumbprint: $($bestCert.Thumbprint)" 'INFO'
        } else {
            throw 'No signing certificate found. Use -AllowUnsigned for dev builds or install enterprise certificate.'
        }
    }
    Test-SignTool
}

# Resolve architectures
$archs = if ($Architecture -eq 'both') { @('x64', 'arm64') } else { @($Architecture) }
$runtimeMap = @{ 'x64' = 'win-x64'; 'arm64' = 'win-arm64' }

# Skip build + sign when -Msi (package-only mode)
if (-not $Msi) {

# Clean
if ($Clean) {
    if (Test-Path $OutputDir) {
        Remove-Item $OutputDir -Recurse -Force
        Write-Log 'Cleaned release directory' 'INFO'
    }
    # Also clean intermediate build artifacts
    $cleanPaths = @(
        (Join-Path $RootDir 'src' 'ManageUsers' 'bin'),
        (Join-Path $RootDir 'src' 'ManageUsers' 'obj'),
        (Join-Path $RootDir 'src' 'ManageUsers.Core' 'bin'),
        (Join-Path $RootDir 'src' 'ManageUsers.Core' 'obj'),
        (Join-Path $AppProjectDir 'bin'),
        (Join-Path $AppProjectDir 'obj')
    )
    foreach ($p in $cleanPaths) {
        if (Test-Path $p) { Remove-Item $p -Recurse -Force }
    }
}

# Build each architecture
foreach ($arch in $archs) {
    $runtime = $runtimeMap[$arch]
    $archOutput = Join-Path $OutputDir $arch

    if (-not (Test-Path $archOutput)) {
        New-Item -ItemType Directory -Path $archOutput -Force | Out-Null
    }

    Write-Log "Publishing for $runtime..." 'INFO'

    $publishArgs = @(
        'publish', $ProjectPath,
        '--configuration', $Configuration,
        '--runtime', $runtime,
        '--self-contained', 'true',
        '--output', $archOutput,
        '-p:PublishSingleFile=true',
        '-p:EnableCompressionInSingleFile=true',
        '-p:IncludeSourceRevisionInInformationalVersion=false',
        "-p:Version=$Version",
        "-p:AssemblyVersion=$Version",
        "-p:FileVersion=$Version",
        '--verbosity', 'minimal'
    )

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "Build failed for $runtime" }

    $exePath = Join-Path $archOutput 'manageusers.exe'
    if (-not (Test-Path $exePath)) { throw "Expected output not found: $exePath" }

    $exeSize = [math]::Round((Get-Item $exePath).Length / 1MB, 2)
    Write-Log "Built manageusers.exe ($runtime) - ${exeSize} MB" 'SUCCESS'

    if (-not $SkipApp) {
        # The app is a self-contained WinUI 3 folder, published to release\<arch>\app.
        $appOutput = Join-Path $archOutput 'app'
        if (Test-Path $appOutput) { Remove-Item $appOutput -Recurse -Force }
        Write-Log "Publishing $AppExeName for $runtime..." 'INFO'
        & dotnet publish $AppProjectPath --configuration $Configuration --runtime $runtime --self-contained true `
            --output $appOutput "-p:Version=$Version" "-p:AssemblyVersion=$Version" "-p:FileVersion=$Version" --verbosity minimal
        if ($LASTEXITCODE -ne 0) { throw "App build failed for $runtime" }
        if (-not (Test-Path (Join-Path $appOutput $AppExeName))) { throw "Expected app output not found: $AppExeName" }
        if (-not (Publish-AppResources -Arch $arch -OutputDir (Resolve-Path $appOutput).Path -AppProjectDir $AppProjectDir)) {
            throw "XAML resource generation failed for $runtime; the app would not start without resources.pri"
        }
        Write-Log "Built $AppExeName ($runtime)" 'SUCCESS'
    }
}

# Sign
if ($SigningCert) {
    Write-Host ''
    foreach ($arch in $archs) {
        $archDir = Join-Path $OutputDir $arch
        $exeFiles = @(Get-ChildItem -Path $archDir -Filter '*.exe' -File -ErrorAction SilentlyContinue)
        $appExe = Join-Path $archDir 'app' $AppExeName
        if (Test-Path $appExe) { $exeFiles += Get-Item $appExe }
        foreach ($exe in $exeFiles) {
            Invoke-SignArtifact -Path $exe.FullName -CertThumbprint $SigningCert.Thumbprint -Store $SigningCert.Store
        }
    }
}

} # end if (-not $Msi)

# Package with cimipkg (always builds .msi, optionally .nupkg with -Nupkg)
Write-Host ''
Write-Log "Building .msi$(if ($Nupkg) { ' + .nupkg' }) packages with cimipkg..." 'INFO'

$cimipkg = Get-Command cimipkg -ErrorAction SilentlyContinue
if (-not $cimipkg) {
    throw 'cimipkg not found in PATH. Install CimianTools first.'
}

$buildDir = Join-Path $RootDir 'build'
if (-not (Test-Path $buildDir)) {
    New-Item -ItemType Directory -Path $buildDir -Force | Out-Null
}

$buildInfoFile = Join-Path $RootDir 'build-info.yaml'
$buildInfoTemplate = Get-Content -Path $buildInfoFile -Raw
$payloadDir = Join-Path $RootDir 'payload'
$pkgStaging = Join-Path $env:TEMP "manageusers_pkg_$(Get-Date -Format 'yyyyMMddHHmmss')"
New-Item -ItemType Directory -Path $pkgStaging -Force | Out-Null

foreach ($arch in $archs) {
    $sourceExe = Join-Path $OutputDir $arch 'manageusers.exe'
    if (-not (Test-Path $sourceExe)) {
        Write-Log "Binary not found for ${arch}: $sourceExe — skipping" 'WARN'
        continue
    }

    # Stamp build-info.yaml with concrete architecture
    $buildInfoContent = $buildInfoTemplate -replace '\$\{ARCH\}', $arch
    Set-Content -Path $buildInfoFile -Value $buildInfoContent -Encoding UTF8 -NoNewline

    # Stage payload: the signed CLI, and the app's files beside it
    if (Test-Path $payloadDir) { Remove-Item $payloadDir -Recurse -Force }
    New-Item -ItemType Directory -Path $payloadDir -Force | Out-Null
    Copy-Item -Path $sourceExe -Destination (Join-Path $payloadDir 'manageusers.exe') -Force
    $appSource = Join-Path $OutputDir $arch 'app'
    if (-not $SkipApp) {
        if (-not (Test-Path (Join-Path $appSource $AppExeName))) { throw "App not built for ${arch}: $appSource" }
        Copy-Item -Path (Join-Path $appSource '*') -Destination $payloadDir -Recurse -Force
    }

    # Build .msi
    Write-Log "Building .msi for $arch..." 'INFO'
    & cimipkg $RootDir
    if ($LASTEXITCODE -ne 0) {
        Write-Log "cimipkg .msi failed for $arch" 'ERROR'
    } else {
        $msiFile = Get-ChildItem -Path $buildDir -Filter 'ManageUsers-*.msi' -File |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($msiFile) {
            $archName = $msiFile.Name -replace '^ManageUsers-', "ManageUsers-${arch}-"
            Rename-Item -Path $msiFile.FullName -NewName $archName -Force
            Move-Item -Path (Join-Path $buildDir $archName) -Destination $pkgStaging -Force
            $stagedFile = Get-Item (Join-Path $pkgStaging $archName)
            Write-Log "Created: $archName ($([math]::Round($stagedFile.Length / 1MB, 2)) MB)" 'SUCCESS'
        }
    }

    # Build .nupkg if requested
    if ($Nupkg) {
        Write-Log "Building .nupkg for $arch..." 'INFO'
        & cimipkg --nupkg $RootDir
        if ($LASTEXITCODE -ne 0) {
            Write-Log "cimipkg .nupkg failed for $arch" 'ERROR'
        } else {
            $nupkgFile = Get-ChildItem -Path $buildDir -Filter 'ManageUsers-*.nupkg' -File |
                Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if ($nupkgFile) {
                $archName = $nupkgFile.Name -replace '^ManageUsers-', "ManageUsers-${arch}-"
                Rename-Item -Path $nupkgFile.FullName -NewName $archName -Force
                Move-Item -Path (Join-Path $buildDir $archName) -Destination $pkgStaging -Force
                $stagedFile = Get-Item (Join-Path $pkgStaging $archName)
                Write-Log "Created: $archName ($([math]::Round($stagedFile.Length / 1MB, 2)) MB)" 'SUCCESS'
            }
        }
    }

    # Clean up staged payload
    Remove-Item $payloadDir -Recurse -Force -ErrorAction SilentlyContinue
}

# Move staged packages back to build/
Get-ChildItem -Path $pkgStaging -File | Move-Item -Destination $buildDir -Force
Remove-Item $pkgStaging -Recurse -Force -ErrorAction SilentlyContinue

# Restore build-info.yaml template with placeholders
Set-Content -Path $buildInfoFile -Value $buildInfoTemplate -Encoding UTF8 -NoNewline

# Summary
Write-Host ''
Write-Host '=== Build Complete ===' -ForegroundColor Green
foreach ($arch in $archs) {
    $exe = Join-Path $OutputDir $arch 'manageusers.exe'
    if (Test-Path $exe) {
        $size = [math]::Round((Get-Item $exe).Length / 1MB, 2)
        $signed = if ($SigningCert) { 'signed' } else { 'unsigned' }
        Write-Host "  $arch : $exe ($size MB, $signed)" -ForegroundColor Cyan
    }
}
$msiFiles = Get-ChildItem -Path (Join-Path $RootDir 'build') -Filter '*.msi' -File -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 2
foreach ($msiItem in $msiFiles) {
    Write-Host "  msi  : $($msiItem.FullName) ($([math]::Round($msiItem.Length / 1MB, 2)) MB)" -ForegroundColor Green
}
if ($Nupkg) {
    $nupkgFiles = Get-ChildItem -Path (Join-Path $RootDir 'build') -Filter '*.nupkg' -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 2
    foreach ($nupkgItem in $nupkgFiles) {
        Write-Host "  nupkg: $($nupkgItem.FullName) ($([math]::Round($nupkgItem.Length / 1MB, 2)) MB)" -ForegroundColor Green
    }
}
Write-Host "  Version: $Version" -ForegroundColor Gray
Write-Host ''
