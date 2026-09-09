[CmdletBinding()]
param(
    [string]$OutputName = "danmaku_bridge.exe"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$module = Join-Path $root "danmaku-bridge"
$go = if (Test-Path "C:\Program Files\Go\bin\go.exe") { "C:\Program Files\Go\bin\go.exe" } else { "go" }
$env:GOPROXY = "https://goproxy.cn,direct"
$env:GOSUMDB = "off"

Push-Location $module
try {
    & $go test ./...
    if ($LASTEXITCODE -ne 0) { throw "danmaku-bridge tests failed." }
    & $go build -ldflags "-s -w" -o $OutputName .
    if ($LASTEXITCODE -ne 0) { throw "danmaku-bridge build failed." }
    Write-Output (Join-Path $module $OutputName)
}
finally {
    Pop-Location
}
