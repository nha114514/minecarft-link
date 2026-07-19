param(
    [switch]$Elevated
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ProductFolderName = 'MCLink P2P Tester'
$ExecutableName = 'MCLink.P2p.Tester.exe'
$UninstallerName = 'Uninstall-MCLinkP2pTester.ps1'
$FirewallName = 'MCLink P2P Tester UDP'
$UninstallKey = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\MCLinkP2pTester'

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Show-InstallerMessage([string]$Message, [string]$Title) {
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show(
        $Message,
        $Title,
        [System.Windows.MessageBoxButton]::OK,
        [System.Windows.MessageBoxImage]::Information) | Out-Null
}

function Normalize-LocalAbsolutePath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path -notmatch '^[A-Za-z]:[\\/]') {
        throw '安装位置必须是本机磁盘上的完整路径。'
    }

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if ($fullPath.StartsWith('\\', [StringComparison]::Ordinal)) {
        throw '安装位置不能是网络共享。'
    }

    $normalized = $fullPath.TrimEnd([char[]]'\/')
    if ($normalized.Length -eq 2 -and $normalized[1] -eq ':') {
        $normalized += '\'
    }

    return $normalized
}

function Resolve-InstallDirectory([string]$SelectedPath) {
    $normalized = Normalize-LocalAbsolutePath $SelectedPath
    $leaf = Split-Path -Path $normalized -Leaf
    if (-not $leaf.Equals($ProductFolderName, [StringComparison]::OrdinalIgnoreCase)) {
        $normalized = Join-Path $normalized $ProductFolderName
    }

    return Normalize-LocalAbsolutePath $normalized
}

function Resolve-AutomaticInstallDirectory {
    $registered = Get-ItemProperty -LiteralPath $UninstallKey -ErrorAction SilentlyContinue
    if ($null -ne $registered) {
        $location = $registered.PSObject.Properties['InstallLocation']
        if ($null -ne $location -and $location.Value -is [string]) {
            try {
                $candidate = Normalize-LocalAbsolutePath $location.Value
                $candidateExe = Join-Path $candidate $ExecutableName
                $candidateUninstaller = Join-Path $candidate $UninstallerName
                if ((Test-Path -LiteralPath $candidateExe -PathType Leaf) -or
                    (Test-Path -LiteralPath $candidateUninstaller -PathType Leaf)) {
                    return $candidate
                }
            }
            catch {
            }
        }
    }

    return Resolve-InstallDirectory $env:ProgramFiles
}

$InstallDirectory = Resolve-AutomaticInstallDirectory

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
        if ($process.ExitCode -eq 0) {
            Start-Process -FilePath (Join-Path $InstallDirectory $ExecutableName)
        }
        exit $process.ExitCode
    }
    catch {
        Show-InstallerMessage '安装已取消，没有修改电脑。' 'MCLink P2P 测试器'
        exit 1223
    }
}

$InstalledExe = Join-Path $InstallDirectory $ExecutableName
$InstalledUninstaller = Join-Path $InstallDirectory $UninstallerName
$SourceExe = Join-Path $PSScriptRoot $ExecutableName
$SourceUninstaller = Join-Path $PSScriptRoot $UninstallerName
$DesktopShortcut = Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'MCLink P2P Tester.lnk'
$StartShortcut = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'MCLink P2P Tester.lnk'
$BackupDirectory = Join-Path $env:TEMP ('MCLinkP2pTester-backup-' + [Guid]::NewGuid().ToString('N'))

function Test-PathsEqual([string]$Left, [string]$Right) {
    try {
        return (Normalize-LocalAbsolutePath $Left).Equals(
            (Normalize-LocalAbsolutePath $Right),
            [StringComparison]::OrdinalIgnoreCase)
    }
    catch {
        return $false
    }
}

function Test-ManagedInstallDirectory([string]$Path) {
    try {
        $normalized = Normalize-LocalAbsolutePath $Path
        return (Test-Path -LiteralPath (Join-Path $normalized $ExecutableName) -PathType Leaf) `
            -or (Test-Path -LiteralPath (Join-Path $normalized $UninstallerName) -PathType Leaf)
    }
    catch {
        return $false
    }
}

function Test-DirectoryEmpty([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        return $true
    }

    return $null -eq (Get-ChildItem -LiteralPath $Path -Force | Select-Object -First 1)
}

function Stop-TesterProcess([string[]]$Directories) {
    $normalizedDirectories = @($Directories |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { Normalize-LocalAbsolutePath $_ } |
        Select-Object -Unique)
    $processes = @(Get-Process -Name 'MCLink.P2p.Tester' -ErrorAction SilentlyContinue |
        Where-Object {
            try {
                $processPath = Normalize-LocalAbsolutePath $_.Path
                $normalizedDirectories | Where-Object {
                    Test-PathsEqual (Split-Path -Parent $processPath) $_
                }
            }
            catch {
                $false
            }
        })

    foreach ($process in $processes) {
        $process | Stop-Process -Force -ErrorAction SilentlyContinue
        [void]$process.WaitForExit(5000)
    }
}

function Remove-InstallDirectoryWithRetry([string]$Path) {
    $normalized = Normalize-LocalAbsolutePath $Path
    $root = [System.IO.Path]::GetPathRoot($normalized)
    if ($normalized.TrimEnd([char[]]'\/').Equals($root.TrimEnd([char[]]'\/'), [StringComparison]::OrdinalIgnoreCase)) {
        throw '拒绝删除磁盘根目录。'
    }

    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try {
            if (Test-Path -LiteralPath $normalized) {
                Remove-Item -LiteralPath $normalized -Recurse -Force
            }
            return
        }
        catch {
            if ($attempt -eq 5) {
                throw
            }
            Start-Sleep -Milliseconds 300
        }
    }
}

function Set-TesterFirewallRule([string]$Directory) {
    $program = Join-Path $Directory $ExecutableName
    Get-NetFirewallRule -DisplayName $FirewallName -ErrorAction SilentlyContinue |
        Remove-NetFirewallRule -ErrorAction SilentlyContinue
    New-NetFirewallRule -DisplayName $FirewallName -Direction Inbound -Action Allow `
        -Protocol UDP -Program $program -Profile Any -Enabled True | Out-Null
}

function New-TesterShortcut([string]$Path, [string]$Directory) {
    $program = Join-Path $Directory $ExecutableName
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($Path)
    $shortcut.TargetPath = $program
    $shortcut.WorkingDirectory = $Directory
    $shortcut.IconLocation = "$program,0"
    $shortcut.Save()
}

function Register-TesterUninstall([string]$Directory) {
    $program = Join-Path $Directory $ExecutableName
    $uninstaller = Join-Path $Directory $UninstallerName
    $uninstallCommand = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "' + $uninstaller + '"'
    New-Item -Path $UninstallKey -Force | Out-Null
    New-ItemProperty -Path $UninstallKey -Name DisplayName -Value 'MCLink P2P Tester' -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $UninstallKey -Name DisplayVersion -Value '0.1.0' -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $UninstallKey -Name Publisher -Value 'MCLink Project' -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $UninstallKey -Name InstallLocation -Value $Directory -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $UninstallKey -Name DisplayIcon -Value $program -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $UninstallKey -Name UninstallString -Value $uninstallCommand -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $UninstallKey -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -Path $UninstallKey -Name NoRepair -Value 1 -PropertyType DWord -Force | Out-Null
}

function Set-TesterIntegration([string]$Directory) {
    Set-TesterFirewallRule $Directory
    New-TesterShortcut $DesktopShortcut $Directory
    New-TesterShortcut $StartShortcut $Directory
    Register-TesterUninstall $Directory
}

function Remove-TesterIntegration {
    Get-NetFirewallRule -DisplayName $FirewallName -ErrorAction SilentlyContinue |
        Remove-NetFirewallRule -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $DesktopShortcut -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $StartShortcut -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $UninstallKey -Recurse -Force -ErrorAction SilentlyContinue
}

$sourceExeExists = Test-Path -LiteralPath $SourceExe -PathType Leaf
$sourceUninstallerExists = Test-Path -LiteralPath $SourceUninstaller -PathType Leaf
if (-not $sourceExeExists -or -not $sourceUninstallerExists) {
    Show-InstallerMessage '安装文件不完整，请重新获取安装程序。' 'MCLink P2P 测试器'
    exit 2
}

$RegisteredInstallDirectory = $null
$registered = Get-ItemProperty -LiteralPath $UninstallKey -ErrorAction SilentlyContinue
if ($null -ne $registered) {
    $installLocationProperty = $registered.PSObject.Properties['InstallLocation']
    if ($null -ne $installLocationProperty -and $installLocationProperty.Value -is [string]) {
        if (Test-ManagedInstallDirectory $installLocationProperty.Value) {
            $RegisteredInstallDirectory = Normalize-LocalAbsolutePath $installLocationProperty.Value
        }
    }
}

$ExistingInstallDirectory = $RegisteredInstallDirectory
if ([string]::IsNullOrWhiteSpace($ExistingInstallDirectory) -and (Test-ManagedInstallDirectory $InstallDirectory)) {
    $ExistingInstallDirectory = $InstallDirectory
}

$selectedHasContent = -not (Test-DirectoryEmpty $InstallDirectory)
$selectedIsExistingInstall = -not [string]::IsNullOrWhiteSpace($ExistingInstallDirectory) `
    -and (Test-PathsEqual $ExistingInstallDirectory $InstallDirectory)
if ($selectedHasContent -and -not $selectedIsExistingInstall) {
    Show-InstallerMessage '所选安装文件夹已经包含其他文件，请选择它的上一级目录或空文件夹。' 'MCLink P2P 测试器'
    exit 3
}

$HadExistingInstall = -not [string]::IsNullOrWhiteSpace($ExistingInstallDirectory)
$SelectedDirectoryOwned = $false

try {
    if ($HadExistingInstall) {
        New-Item -ItemType Directory -Path $BackupDirectory -Force | Out-Null
        Get-ChildItem -LiteralPath $ExistingInstallDirectory -Force |
            Copy-Item -Destination $BackupDirectory -Recurse -Force
    }

    Stop-TesterProcess @($ExistingInstallDirectory, $InstallDirectory)
    if (Test-Path -LiteralPath $InstallDirectory) {
        if (-not $selectedIsExistingInstall -and -not (Test-DirectoryEmpty $InstallDirectory)) {
            throw '所选安装文件夹不再为空。'
        }
        Remove-InstallDirectoryWithRetry $InstallDirectory
    }

    $SelectedDirectoryOwned = $true
    New-Item -ItemType Directory -Path $InstallDirectory -Force | Out-Null
    Copy-Item -LiteralPath $SourceExe -Destination $InstalledExe -Force
    Copy-Item -LiteralPath $SourceUninstaller -Destination $InstalledUninstaller -Force
    Set-TesterIntegration $InstallDirectory

    if ($HadExistingInstall -and -not (Test-PathsEqual $ExistingInstallDirectory $InstallDirectory)) {
        if (-not (Test-ManagedInstallDirectory $ExistingInstallDirectory)) {
            throw '旧安装位置未通过安全校验。'
        }
        Remove-InstallDirectoryWithRetry $ExistingInstallDirectory
    }

    if (-not $Elevated) {
        Start-Process -FilePath $InstalledExe
    }
    exit 0
}
catch {
    Remove-TesterIntegration
    if ($SelectedDirectoryOwned -and (Test-Path -LiteralPath $InstallDirectory)) {
        if ((Test-ManagedInstallDirectory $InstallDirectory) -or (Test-DirectoryEmpty $InstallDirectory)) {
            Remove-InstallDirectoryWithRetry $InstallDirectory
        }
    }

    if ($HadExistingInstall -and (Test-Path -LiteralPath $BackupDirectory)) {
        New-Item -ItemType Directory -Path $ExistingInstallDirectory -Force | Out-Null
        Get-ChildItem -LiteralPath $BackupDirectory -Force |
            Copy-Item -Destination $ExistingInstallDirectory -Recurse -Force
        Set-TesterIntegration $ExistingInstallDirectory
    }

    Show-InstallerMessage '安装失败，已撤销本次修改。' 'MCLink P2P 测试器'
    exit 1
}
finally {
    if (Test-Path -LiteralPath $BackupDirectory) {
        Remove-Item -LiteralPath $BackupDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}
