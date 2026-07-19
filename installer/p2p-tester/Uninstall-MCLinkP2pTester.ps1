param(
    [switch]$Elevated
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

$InstallDirectory = $PSScriptRoot
$InstalledExe = Join-Path $InstallDirectory 'MCLink.P2p.Tester.exe'
$FirewallName = 'MCLink P2P Tester UDP'
$UninstallKey = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\MCLinkP2pTester'
$DesktopShortcut = Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'MCLink P2P Tester.lnk'
$StartShortcut = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'MCLink P2P Tester.lnk'

if (-not (Test-Path -LiteralPath $InstalledExe -PathType Leaf)) {
    exit 2
}

if (-not (Test-Administrator)) {
    try {
        $process = Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -Wait -PassThru -ArgumentList @(
            '-NoProfile',
            '-ExecutionPolicy',
            'Bypass',
            '-File',
            ('"' + $PSCommandPath + '"'),
            '-Elevated'
        )
        exit $process.ExitCode
    }
    catch {
        exit 1223
    }
}

$processes = @(Get-Process -Name 'MCLink.P2p.Tester' -ErrorAction SilentlyContinue |
    Where-Object {
        try {
            $_.Path.Equals($InstalledExe, [StringComparison]::OrdinalIgnoreCase)
        }
        catch {
            $false
        }
    })
foreach ($process in $processes) {
    $process | Stop-Process -Force -ErrorAction SilentlyContinue
    [void]$process.WaitForExit(5000)
}

Get-NetFirewallRule -DisplayName $FirewallName -ErrorAction SilentlyContinue |
    Remove-NetFirewallRule -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $DesktopShortcut -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $StartShortcut -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $UninstallKey -Recurse -Force -ErrorAction SilentlyContinue

for ($attempt = 1; $attempt -le 5; $attempt++) {
    try {
        Remove-Item -LiteralPath $InstallDirectory -Recurse -Force
        exit 0
    }
    catch {
        if ($attempt -eq 5) {
            exit 1
        }
        Start-Sleep -Milliseconds 300
    }
}
