# Shared by build.ps1 and the release workflow. Dot-source it, then call
# Publish-AppResources after `dotnet publish` of src\ManageUsers.App.

if (-not (Get-Command Write-Log -ErrorAction SilentlyContinue)) {
    function Write-Log([string]$Message, [string]$Level = 'INFO') { Write-Host "[$Level] $Message" }
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
