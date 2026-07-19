param(
    [Parameter(Mandatory = $true)]
    [string]$RepositoryRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = [System.IO.Path]::GetFullPath($RepositoryRoot)
$project = Join-Path $root 'tools\MCLink.P2p.Tester\MCLink.P2p.Tester.csproj'
$installerSource = Join-Path $root 'installer\p2p-tester'
$publishDirectory = Join-Path $root 'artifacts\p2p-tester'
$installerOutput = Join-Path $root 'artifacts\installer'
$staging = Join-Path $installerOutput 'staging'
$setupPath = Join-Path $installerOutput 'MCLink-P2P-Tester-Setup.exe'
$sedPath = Join-Path $installerOutput 'MCLink-P2P-Tester.sed'
$iexpress = Join-Path $env:SystemRoot 'System32\iexpress.exe'
$env:NUGET_PACKAGES = Join-Path $root '.nuget\packages'

if (-not (Test-Path -LiteralPath $iexpress -PathType Leaf)) {
    throw 'Windows IExpress is not available.'
}

New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $installerOutput -Force | Out-Null
Remove-Item -LiteralPath $publishDirectory -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $setupPath -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $staging -Force | Out-Null

& dotnet publish $project -c Release -r win-x64 --self-contained true -o $publishDirectory
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

Get-ChildItem -LiteralPath $publishDirectory -Filter '*.pdb' -File -ErrorAction SilentlyContinue |
    Remove-Item -Force

$publishedExe = Join-Path $publishDirectory 'MCLink.P2p.Tester.exe'
if (-not (Test-Path -LiteralPath $publishedExe -PathType Leaf)) {
    throw 'Published tester executable is missing.'
}

Copy-Item -LiteralPath $publishedExe -Destination (Join-Path $staging 'MCLink.P2p.Tester.exe') -Force
Copy-Item -LiteralPath (Join-Path $installerSource 'Install-MCLinkP2pTester.ps1') -Destination $staging -Force
Copy-Item -LiteralPath (Join-Path $installerSource 'Uninstall-MCLinkP2pTester.ps1') -Destination $staging -Force

$installCommand = @'
@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-MCLinkP2pTester.ps1"
exit /b %ERRORLEVEL%
'@
[System.IO.File]::WriteAllText(
    (Join-Path $staging 'install.cmd'),
    $installCommand,
    [System.Text.Encoding]::ASCII)

$sourcePath = $staging.TrimEnd('\') + '\'
$sed = @"
[Version]
Class=IEXPRESS
SEDVersion=3

[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=0
HideExtractAnimation=1
UseLongFileName=1
InsideCompressed=0
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=
DisplayLicense=
FinishMessage=
TargetName=$setupPath
FriendlyName=MCLink P2P Tester Setup
AppLaunched=install.cmd
PostInstallCmd=<None>
AdminQuietInstCmd=install.cmd
UserQuietInstCmd=install.cmd
SourceFiles=SourceFiles

[Strings]
FILE0="MCLink.P2p.Tester.exe"
FILE1="Install-MCLinkP2pTester.ps1"
FILE2="Uninstall-MCLinkP2pTester.ps1"
FILE3="install.cmd"

[SourceFiles]
SourceFiles0=$sourcePath

[SourceFiles0]
%FILE0%=
%FILE1%=
%FILE2%=
%FILE3%=
"@
[System.IO.File]::WriteAllText($sedPath, $sed, [System.Text.Encoding]::ASCII)

$iexpressProcess = Start-Process -FilePath $iexpress -ArgumentList @('/N', '/Q', $sedPath) -Wait -PassThru -WindowStyle Hidden
if ($iexpressProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
    throw 'IExpress failed.'
}

Remove-Item -LiteralPath $staging -Recurse -Force
Remove-Item -LiteralPath $sedPath -Force
Write-Host "SetupExe=$setupPath"
