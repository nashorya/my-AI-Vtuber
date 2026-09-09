[CmdletBinding()]
param(
    [string]$Config = "",
    [switch]$NoWrite
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$module = Join-Path $root "danmaku-bridge"
$go = if (Test-Path "C:\Program Files\Go\bin\go.exe") { "C:\Program Files\Go\bin\go.exe" } else { "go" }
$env:GOPROXY = "https://goproxy.cn,direct"
$env:GOSUMDB = "off"

$argsList = @()
if ($Config) { $argsList += @("-config", $Config) }
if ($NoWrite) { $argsList += "-no-write" }

Push-Location $module
try {
    & $go run ./cmd/bili-login @argsList
    if ($LASTEXITCODE -ne 0) { throw "B 站扫码登录失败。" }
}
finally {
    Pop-Location
}
