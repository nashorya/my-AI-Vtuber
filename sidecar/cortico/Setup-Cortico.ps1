param(
    [string]$AppDirectory = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path,
    [string]$Live2dDir = '',
    [string]$ModelProfile = 'auto',
    [string]$AudioDevice = ''
)
$ErrorActionPreference = 'Stop'
$node = (Get-Command node -ErrorAction Stop).Source
$major = [int]((& $node --version).TrimStart('v').Split('.')[0])
if ($major -lt 22) { throw 'Cortico requires Node.js 22 or newer.' }
Push-Location $PSScriptRoot
try {
    & npm.cmd ci
    if ($LASTEXITCODE -ne 0) { throw 'npm ci failed; the native audify module must install successfully.' }
    & $node -e "require('audify')"
    if ($LASTEXITCODE -ne 0) { throw 'Native audio preflight failed.' }
} finally { Pop-Location }
$destination = Join-Path $AppDirectory 'cortico.json'
if (Test-Path $destination) {
    Write-Host "Dependencies ready. Existing configuration preserved: $destination"
} else {
    $config = Get-Content (Join-Path $PSScriptRoot 'cortico.json.example') -Raw | ConvertFrom-Json
    $config.nodePath = $node
    $config.sidecarPath = $PSScriptRoot
    $config.live2dDir = $Live2dDir
    $config.modelProfile = $ModelProfile
    $config.audioDevice = $AudioDevice
    [System.IO.File]::WriteAllText($destination, ($config | ConvertTo-Json), [System.Text.UTF8Encoding]::new($false))
    Write-Host "Cortico enabled for next app launch: $destination"
}
Write-Host 'Load the calibrated model copy in VTube Studio, then restart AIVTuber and approve its plugin connection.'
