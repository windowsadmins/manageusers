#Requires -Version 7.0
<#
.SYNOPSIS
    Builds the ManageUsers binary with enterprise code signing.

.DESCRIPTION
    Builds the .NET ManageUsers console application as a self-contained single-file executable for
    x64 and arm64, then signs with the enterprise certificate.

.PARAMETER Sign
    Sign binaries with code signing certificate.

.PARAMETER Architecture
    Target architecture: x64, arm64, or both (default: both).

.PARAMETER Clean
    Clean build output before building.

.EXAMPLE
    .\build.ps1 -Sign
    # Build and sign for both architectures

.EXAMPLE
    .\build.ps1 -Sign -Architecture arm64
    # Build and sign arm64 only
#>

param(
    [switch]$Sign,
    [ValidateSet('x64', 'arm64', 'both')]
    [string]$Architecture = 'both',
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
$RootDir = $PSScriptRoot
$ProjectPath = Join-Path $RootDir 'src' 'ManageUsers' 'ManageUsers.csproj'
$OutputDir = Join-Path $RootDir 'release'
$Configuration = 'Release'

# Timestamp-based versioning
$version = Get-Date -Format 'yyyy.MM.dd.HHmm'

function Write-BuildLog {
    param([string]$Message, [string]$Level = 'INFO')
    $color = switch ($Level) {
        'SUCCESS' { 'Green' }
        'WARNING' { 'Yellow' }
        'ERROR'   { 'Red' }
        default   { 'Cyan' }
    }
    Write-Host "[$Level] $Message" -ForegroundColor $color
}

function Test-SignTool {
    if (-not (Get-Command 'signtool.exe' -ErrorAction SilentlyContinue)) {
        $sdkPaths = @(
            "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe",
            "${env:ProgramFiles(x86)}\Windows Kits\10\App Certification Kit\signtool.exe"
        )
        foreach ($pattern in $sdkPaths) {
            $found = Get-ChildItem $pattern -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1
            if ($found) {
                $env:PATH += ";$($found.DirectoryName)"
                return
            }
        }
        throw "signtool.exe not found. Install Windows SDK."
    }
}

function Find-SigningCertificate {
    $certName = "EmilyCarrU Intune Windows Enterprise Certificate"
    $stores = @('Cert:\CurrentUser\My', 'Cert:\LocalMachine\My')

    foreach ($store in $stores) {
        $cert = Get-ChildItem $store -ErrorAction SilentlyContinue |
            Where-Object { $_.Subject -match $certName -and $_.NotAfter -gt (Get-Date) } |
            Sort-Object NotAfter -Descending |
            Select-Object -First 1

        if ($cert) {
            Write-BuildLog "Found signing certificate: $($cert.Thumbprint) (expires $($cert.NotAfter.ToString('yyyy-MM-dd')))"
            return @{ Thumbprint = $cert.Thumbprint; Store = $store }
        }
    }

    return $null
}

function Invoke-SignArtifact {
    param([string]$Path, [string]$Thumbprint, [string]$Store)

    $storeFlag = if ($Store -match 'LocalMachine') { '/sm' } else { '' }
    $args = @('sign', '/sha1', $Thumbprint, '/fd', 'SHA256', '/td', 'SHA256', '/tr', 'http://timestamp.digicert.com')
    if ($storeFlag) { $args += $storeFlag }
    $args += $Path

    & signtool.exe @args 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to sign: $Path"
    }
    Write-BuildLog "Signed: $(Split-Path $Path -Leaf)" -Level 'SUCCESS'
}

# Main build logic
Write-BuildLog "ManageUsers Build — v$version"
Write-BuildLog "Architecture: $Architecture"

if ($Clean -and (Test-Path $OutputDir)) {
    Remove-Item $OutputDir -Recurse -Force
    Write-BuildLog "Cleaned release directory"
}

# Resolve architectures
$archs = if ($Architecture -eq 'both') { @('x64', 'arm64') } else { @($Architecture) }
$runtimeMap = @{ 'x64' = 'win-x64'; 'arm64' = 'win-arm64' }

# Build
foreach ($arch in $archs) {
    $runtime = $runtimeMap[$arch]
    $archOutput = Join-Path $OutputDir $arch

    if (-not (Test-Path $archOutput)) {
        New-Item -ItemType Directory -Path $archOutput -Force | Out-Null
    }

    Write-BuildLog "Building for $runtime..."

    $publishArgs = @(
        'publish', $ProjectPath,
        '--configuration', $Configuration,
        '--runtime', $runtime,
        '--self-contained', 'true',
        '--output', $archOutput,
        '-p:PublishSingleFile=true',
        '-p:EnableCompressionInSingleFile=true',
        '-p:IncludeSourceRevisionInInformationalVersion=false',
        "-p:Version=$version",
        "-p:AssemblyVersion=$version",
        "-p:FileVersion=$version",
        '--verbosity', 'minimal'
    )

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed for $runtime"
    }

    Write-BuildLog "Built manageusers ($runtime)" -Level 'SUCCESS'
}

# Sign
if ($Sign) {
    $certInfo = Find-SigningCertificate
    if (-not $certInfo) {
        throw "No signing certificate found. Cannot build without signing."
    }

    Test-SignTool

    foreach ($arch in $archs) {
        $archDir = Join-Path $OutputDir $arch
        $exeFiles = Get-ChildItem -Path $archDir -Filter '*.exe' -File -ErrorAction SilentlyContinue
        foreach ($exe in $exeFiles) {
            Invoke-SignArtifact -Path $exe.FullName -Thumbprint $certInfo.Thumbprint -Store $certInfo.Store
        }
    }
}

Write-BuildLog "Build complete — output in $OutputDir" -Level 'SUCCESS'
