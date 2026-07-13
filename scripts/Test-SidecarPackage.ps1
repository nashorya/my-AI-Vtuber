[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageRoot,
    [switch]$RequireRuntime
)

$ErrorActionPreference = "Stop"
$sidecarRoot = Join-Path $PackageRoot "sidecar"
$manifestPath = Join-Path $sidecarRoot "asr-sidecar.manifest.json"

function Fail([string]$Code, [string]$Message) {
    Write-Error "$Code $Message"
    exit 1
}

if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    Fail "ASR-SIDECAR-004" "Manifest is missing: $manifestPath"
}

try {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
} catch {
    Fail "ASR-SIDECAR-004" "Manifest is invalid JSON: $($_.Exception.Message)"
}

if ($manifest.schema_version -ne 1 -or
    $manifest.delivery_mode -ne "managed-embedded-python" -or
    [string]::IsNullOrWhiteSpace($manifest.runtime.executable) -or
    $null -eq $manifest.files) {
    Fail "ASR-SIDECAR-004" "Manifest contract fields are missing or unsupported."
}

foreach ($file in $manifest.files) {
    $path = Join-Path $sidecarRoot $file.path
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Fail "ASR-SIDECAR-002" "Required payload is missing: $($file.path)"
    }

    $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $file.sha256.ToLowerInvariant()) {
        Fail "ASR-SIDECAR-003" "Hash mismatch for $($file.path)"
    }
}

if ($RequireRuntime) {
    $runtimePath = Join-Path $sidecarRoot $manifest.runtime.executable
    if (-not (Test-Path -LiteralPath $runtimePath -PathType Leaf)) {
        Fail "ASR-SIDECAR-001" "Managed Python runtime is missing: $($manifest.runtime.executable)"
    }
}

Write-Output "ASR sidecar package valid (version $($manifest.sidecar_version))."
