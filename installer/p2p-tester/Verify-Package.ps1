param(
    [Parameter(Mandatory = $true)]
    [string]$RepositoryRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = [System.IO.Path]::GetFullPath($RepositoryRoot)
$publishedExe = Join-Path $root 'artifacts\p2p-tester\MCLink.P2p.Tester.exe'
$setupExe = Join-Path $root 'artifacts\installer\MCLink-P2P-Tester-Setup.exe'
$installerDirectory = Join-Path $root 'installer\p2p-tester'
$installScript = Join-Path $installerDirectory 'Install-MCLinkP2pTester.ps1'
$uninstallScript = Join-Path $installerDirectory 'Uninstall-MCLinkP2pTester.ps1'
$buildScript = Join-Path $installerDirectory 'Build-Installer.ps1'
$testerIcon = Join-Path $root 'tools\MCLink.P2p.Tester\Assets\mclink-world.ico'
$issues = [System.Collections.Generic.List[string]]::new()

foreach ($requiredFile in @($publishedExe, $setupExe, $installScript, $uninstallScript, $buildScript, $testerIcon)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        $issues.Add("Missing required file: $requiredFile")
    }
}

foreach ($legacyPath in @(
    (Join-Path $root 'src\MCLink.App'),
    (Join-Path $root 'tools\MCLink.P2p.Probe'),
    (Join-Path $root 'artifacts\p2p-probe'),
    (Join-Path $root 'artifacts\publish'),
    (Join-Path $root 'artifacts\MCLink-P2P-Probe-win-x64.zip')
)) {
    if (Test-Path -LiteralPath $legacyPath) {
        $issues.Add("Legacy content still exists: $legacyPath")
    }
}

if (Test-Path -LiteralPath $publishedExe -PathType Leaf) {
    if ((Get-Item -LiteralPath $publishedExe).Length -le 0) {
        $issues.Add('Published tester executable is empty.')
    }
}

if (Test-Path -LiteralPath $setupExe -PathType Leaf) {
    if ((Get-Item -LiteralPath $setupExe).Length -le 1MB) {
        $issues.Add('Setup executable must be larger than 1 MiB.')
    }
}

$scriptFiles = @($installScript, $uninstallScript, $buildScript) |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }
$combinedScripts = ($scriptFiles | ForEach-Object { Get-Content -LiteralPath $_ -Raw }) -join "`n"

foreach ($dangerousPattern in @(
    'Add-MpPreference',
    'Set-MpPreference',
    'ExecutionPolicy\s+Unrestricted',
    'Remove-Item\s+[''\"]?D:\\(?:\s|[''\"]|$)'
)) {
    if ($combinedScripts -match $dangerousPattern) {
        $issues.Add("Dangerous installer operation found: $dangerousPattern")
    }
}

foreach ($requiredPattern in @(
    [regex]::Escape("MCLink P2P Tester UDP"),
    [regex]::Escape("MCLink P2P Tester"),
    [regex]::Escape("MCLinkP2pTester"),
    'New-NetFirewallRule',
    '-Direction\s+Inbound',
    '-Protocol\s+UDP',
    '-Program\s+\$program',
    'Resolve-AutomaticInstallDirectory',
    '\$InstallDirectory\s*=\s*Resolve-AutomaticInstallDirectory',
    'Test-ManagedInstallDirectory',
    'InstallLocation',
    '\$InstallDirectory\s*=\s*\$PSScriptRoot'
)) {
    if ($combinedScripts -notmatch $requiredPattern) {
        $issues.Add("Required installer behavior missing: $requiredPattern")
    }
}

if ($issues.Count -gt 0) {
    foreach ($issue in $issues) {
        Write-Host "ERROR: $issue" -ForegroundColor Red
    }

    exit 1
}

$publishedHash = (Get-FileHash -LiteralPath $publishedExe -Algorithm SHA256).Hash
$setupHash = (Get-FileHash -LiteralPath $setupExe -Algorithm SHA256).Hash
Write-Host "PublishedExe=$publishedExe"
Write-Host "PublishedSHA256=$publishedHash"
Write-Host "SetupExe=$setupExe"
Write-Host "SetupSHA256=$setupHash"
Write-Host 'Package verification passed.'
