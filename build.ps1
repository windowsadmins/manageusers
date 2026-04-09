#!/usr/bin/env pwsh
# ManageUsers Build Script
# Builds, signs, and packages ManageUsers binary for Cimian deployment
#
# Examples:
#   .\build.ps1                          # Build + sign for both architectures (default)
#   .\build.ps1 -Msi                     # Build, sign, and create .msi with cimipkg
#   .\build.ps1 -Thumbprint "ABC123..."  # Build with specific certificate
#   .\build.ps1 -AllowUnsigned           # Development build without signing (NOT for production)
#   .\build.ps1 -Architecture arm64      # Build single architecture
#   .\build.ps1 -ListCerts               # List available code signing certificates
#   .\build.ps1 -Clean                   # Clean build output first

[CmdletBinding()]
param(
    [string]$Thumbprint,
    [ValidateSet('x64', 'arm64', 'both')]
    [string]$Architecture = 'both',
    [switch]$Clean,
    [switch]$AllowUnsigned,
    [switch]$ListCerts,
    [string]$FindCertSubject,
    [switch]$Msi
)

$ErrorActionPreference = 'Stop'
$RootDir = $PSScriptRoot
$ProjectPath = Join-Path $RootDir 'src' 'ManageUsers' 'ManageUsers.csproj'
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

    # First priority: Enterprise certificate (EmilyCarrU Intune)
    $enterpriseCert = $certs | Where-Object { $_.Subject -like '*EmilyCarrU Intune*' } |
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

# --- Main build ---

Write-Host ''
Write-Host '=== ManageUsers Build ===' -ForegroundColor Magenta
Write-Host "Version:       $Version" -ForegroundColor Yellow
Write-Host "Architecture:  $Architecture" -ForegroundColor Yellow
Write-Host "Code Signing:  $(if ($AllowUnsigned) { 'DISABLED (dev only)' } else { 'REQUIRED' })" -ForegroundColor $(if ($AllowUnsigned) { 'Red' } else { 'Green' })
Write-Host "Package:       $(if ($Msi) { 'YES (.msi via cimipkg)' } else { 'No' })" -ForegroundColor $(if ($Msi) { 'Green' } else { 'Gray' })
if ($AllowUnsigned) {
    Write-Host ''
    Write-Host 'WARNING: Unsigned build - NOT suitable for production deployment' -ForegroundColor Red
}
Write-Host ''

# Auto-detect signing certificate
$SigningCert = $null
if (-not $AllowUnsigned) {
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

# Clean
if ($Clean) {
    if (Test-Path $OutputDir) {
        Remove-Item $OutputDir -Recurse -Force
        Write-Log 'Cleaned release directory' 'INFO'
    }
    # Also clean intermediate build artifacts
    $cleanPaths = @(
        (Join-Path $RootDir 'src' 'ManageUsers' 'bin'),
        (Join-Path $RootDir 'src' 'ManageUsers' 'obj')
    )
    foreach ($p in $cleanPaths) {
        if (Test-Path $p) { Remove-Item $p -Recurse -Force }
    }
}

# Resolve architectures
$archs = if ($Architecture -eq 'both') { @('x64', 'arm64') } else { @($Architecture) }
$runtimeMap = @{ 'x64' = 'win-x64'; 'arm64' = 'win-arm64' }

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
}

# Sign
if ($SigningCert) {
    Write-Host ''
    foreach ($arch in $archs) {
        $archDir = Join-Path $OutputDir $arch
        $exeFiles = Get-ChildItem -Path $archDir -Filter '*.exe' -File -ErrorAction SilentlyContinue
        foreach ($exe in $exeFiles) {
            Invoke-SignArtifact -Path $exe.FullName -CertThumbprint $SigningCert.Thumbprint -Store $SigningCert.Store
        }
    }
}

# Package with cimipkg (new cimipkg defaults to .msi output)
if ($Msi) {
    Write-Host ''
    Write-Log 'Building .msi packages with cimipkg...' 'INFO'

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
    $msiStaging = Join-Path $env:TEMP "manageusers_msi_$(Get-Date -Format 'yyyyMMddHHmmss')"
    New-Item -ItemType Directory -Path $msiStaging -Force | Out-Null

    foreach ($arch in $archs) {
        $sourceExe = Join-Path $OutputDir $arch 'manageusers.exe'
        if (-not (Test-Path $sourceExe)) {
            Write-Log "Binary not found for ${arch}: $sourceExe — skipping" 'WARN'
            continue
        }

        # Stamp build-info.yaml with concrete architecture
        $buildInfoContent = $buildInfoTemplate -replace '\$\{ARCH\}', $arch
        Set-Content -Path $buildInfoFile -Value $buildInfoContent -Encoding UTF8 -NoNewline

        # Stage payload — only the signed binary
        if (Test-Path $payloadDir) { Remove-Item $payloadDir -Recurse -Force }
        New-Item -ItemType Directory -Path $payloadDir -Force | Out-Null
        Copy-Item -Path $sourceExe -Destination (Join-Path $payloadDir 'manageusers.exe') -Force

        # Run cimipkg — defaults to .msi in 2026.04.09+
        Write-Log "Building .msi for $arch..." 'INFO'
        & cimipkg $RootDir
        if ($LASTEXITCODE -ne 0) {
            Write-Log "cimipkg failed for $arch" 'ERROR'
        } else {
            # Rescue .msi from build/ before next cimipkg run wipes it
            # cimipkg names it ManageUsers-{version}.msi; rename to include arch
            $msiFile = Get-ChildItem -Path $buildDir -Filter 'ManageUsers-*.msi' -File |
                Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if ($msiFile) {
                $archName = $msiFile.Name -replace '^ManageUsers-', "ManageUsers-${arch}-"
                $archPath = Join-Path $buildDir $archName
                Rename-Item -Path $msiFile.FullName -NewName $archName -Force
                Move-Item -Path $archPath -Destination $msiStaging -Force
                $stagedFile = Get-Item (Join-Path $msiStaging $archName)
                Write-Log "Created: $archName ($([math]::Round($stagedFile.Length / 1MB, 2)) MB)" 'SUCCESS'
            }
        }

        # Clean up staged payload
        Remove-Item $payloadDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    # Move staged .msi files back to build/
    Get-ChildItem -Path $msiStaging -Filter '*.msi' -File | Move-Item -Destination $buildDir -Force
    Remove-Item $msiStaging -Recurse -Force -ErrorAction SilentlyContinue

    # Restore build-info.yaml template with placeholders
    Set-Content -Path $buildInfoFile -Value $buildInfoTemplate -Encoding UTF8 -NoNewline
}

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
if ($Msi) {
    $msiFiles = Get-ChildItem -Path (Join-Path $RootDir 'build') -Filter '*.msi' -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 2
    foreach ($msiItem in $msiFiles) {
        Write-Host "  msi  : $($msiItem.FullName) ($([math]::Round($msiItem.Length / 1MB, 2)) MB)" -ForegroundColor Green
    }
}
Write-Host "  Version: $Version" -ForegroundColor Gray
Write-Host ''
