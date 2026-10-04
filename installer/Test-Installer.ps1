param([Parameter(Mandatory)][string]$Installer, [Parameter(Mandatory)][string]$PreviousInstaller)
$ErrorActionPreference = 'Stop'
$target = Join-Path $env:LOCALAPPDATA 'Programs\AIVTuber-Installer-Test'
$desktop = Join-Path ([Environment]::GetFolderPath('Desktop')) 'AIVTuber.lnk'
$menu = Join-Path ([Environment]::GetFolderPath('Programs')) 'AIVTuber\AIVTuber.lnk'
$reg = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AIVTuber'
function Require($condition, $message) { if (-not $condition) { throw $message } }
function Install($Package = $Installer) {
    $p = Start-Process -FilePath $Package -ArgumentList @('/S', "/D=$target") -Wait -PassThru
    Require ($p.ExitCode -eq 0) "Install failed: $($p.ExitCode)"
}
Require (-not (Test-Path $target)) 'Test install target already exists'
Require (-not (Test-Path $reg)) 'AIVTuber is already installed for this test user'
Install $PreviousInstaller
Require (Test-Path (Join-Path $target "obsolete-installer-fixture.txt")) "Old version fixture missing"
foreach ($rel in @('AIVTuber.exe','WebRtcVad.dll','distribution\profile.json','Uninstall.exe','WebUi\wwwroot\streamer.html')) {
    Require (Test-Path (Join-Path $target $rel)) "Missing installed file: $rel"
}
Require (Test-Path $desktop) 'Desktop shortcut absent'
Require (Test-Path $menu) 'Start menu shortcut absent'
Require (Test-Path $reg) 'Uninstall registration absent'
$profile = Get-Content (Join-Path $target 'distribution\profile.json') -Raw | ConvertFrom-Json
Require ($profile.profile_id -eq 'shared-001') 'Wrong invite profile'
Require (-not $profile.account) 'Shared package prefilled an account'
$manifest = Get-Content (Join-Path $target 'distribution-manifest.json') -Raw | ConvertFrom-Json
foreach ($file in $manifest.files.PSObject.Properties) {
    $hash = (Get-FileHash (Join-Path $target $file.Name) -Algorithm SHA256).Hash.ToLowerInvariant()
    Require ($hash -eq $file.Value) "Installed binary mismatch: $($file.Name)"
}
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Test-InstalledUi.ps1') -AppDirectory $target
Require ($LASTEXITCODE -eq 0) 'Installed application UI check failed'
# Sharing violation must fail before changing files or the recovery registration.
$locked = [IO.File]::Open((Join-Path $target 'AIVTuber.exe'), 'Open', 'Read', 'Read')
try {
    $blocked = Start-Process (Join-Path $target 'Uninstall.exe') -ArgumentList @('/S', "_?=$target") -Wait -PassThru
    Require ($blocked.ExitCode -ne 0) 'Locked executable uninstall unexpectedly succeeded'
    Require (Test-Path $reg) 'Blocked uninstall removed recovery registration'
    Require (Test-Path (Join-Path $target 'distribution\profile.json')) 'Blocked uninstall changed payload'
    $blocked = Start-Process $Installer -ArgumentList @('/S', "/D=$target") -Wait -PassThru
    Require ($blocked.ExitCode -ne 0) 'Locked executable upgrade unexpectedly succeeded'
} finally { $locked.Dispose() }
$other = "$target-Other"
$blocked = Start-Process $Installer -ArgumentList @('/S', "/D=$other") -Wait -PassThru
Require ($blocked.ExitCode -ne 0) 'Upgrade accepted a different directory'
Require (-not (Test-Path $other)) 'Blocked relocation left a second installation'
Require (Test-Path $reg) 'Blocked relocation removed old registration'
# User settings and data must survive an upgrade and uninstall; never RMDir /r $INSTDIR.
Set-Content (Join-Path $target 'config.json') '{"installer_test":true}'
Set-Content (Join-Path $target 'memory.db') 'user-memory-sentinel'
Install
Require (-not (Test-Path (Join-Path $target 'obsolete-installer-fixture.txt'))) 'Upgrade left retired owned file'
Require ((Get-Content (Join-Path $target 'memory.db') -Raw).Trim() -eq 'user-memory-sentinel') 'Upgrade overwrote memory'
Require ((Get-Content (Join-Path $target 'config.json') -Raw) -match 'installer_test') 'Upgrade overwrote settings'
$u = Start-Process -FilePath (Join-Path $target 'Uninstall.exe') -ArgumentList @('/S', "_?=$target") -PassThru -Wait
Require ($u.ExitCode -eq 0) "Uninstall failed: $($u.ExitCode)"
Require (-not (Test-Path (Join-Path $target 'AIVTuber.exe'))) 'Uninstall left app executable'
Require (-not (Test-Path (Join-Path $target 'distribution\profile.json'))) 'Uninstall left provider credentials'
Require (-not (Test-Path $desktop)) 'Uninstall left desktop shortcut'
Require (-not (Test-Path $reg)) 'Uninstall left registry entry'
Require ((Get-Content (Join-Path $target 'memory.db') -Raw).Trim() -eq 'user-memory-sentinel') 'Uninstall removed user memory'
Require ((Get-Content (Join-Path $target 'config.json') -Raw) -match 'installer_test') 'Uninstall removed settings'
Write-Host 'PASS: install, binary integrity, shared profile, shortcuts, upgrade preservation, retired-file cleanup, locked-file refusal, relocation refusal, uninstall and data preservation'
