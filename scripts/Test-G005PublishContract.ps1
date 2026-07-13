[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = "Stop"
$validator = Join-Path $RepositoryRoot "scripts/Test-SidecarPackage.ps1"
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("aivtuber-g005-" + [Guid]::NewGuid())
$sidecarRoot = Join-Path $tempRoot "sidecar"

function Assert-Contains([string]$Text, [string]$Expected, [string]$Context) {
    if ($Text.IndexOf($Expected, [StringComparison]::Ordinal) -lt 0) {
        throw "$Context did not contain '$Expected'. Actual: $Text"
    }
}

function Invoke-FailingCase([string]$ExpectedCode, [scriptblock]$Mutate, [switch]$RequireRuntime) {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $sidecarRoot -Force | Out-Null
    Copy-Item (Join-Path $RepositoryRoot "asr_server.py") $sidecarRoot
    Copy-Item (Join-Path $RepositoryRoot "sidecar/asr-sidecar.manifest.json") $sidecarRoot
    Copy-Item (Join-Path $RepositoryRoot "sidecar/requirements.lock") $sidecarRoot
    & $Mutate

    $arguments = @("-NoProfile", "-File", $validator, "-PackageRoot", $tempRoot)
    if ($RequireRuntime) { $arguments += "-RequireRuntime" }
    $previousErrorAction = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = & powershell @arguments 2>&1 | Out-String
    } finally {
        $ErrorActionPreference = $previousErrorAction
    }
    if ($LASTEXITCODE -eq 0) { throw "Expected $ExpectedCode failure but validation passed." }
    Assert-Contains $output $ExpectedCode "Hostile package case"
}

try {
    New-Item -ItemType Directory -Path $sidecarRoot -Force | Out-Null
    Copy-Item (Join-Path $RepositoryRoot "asr_server.py") $sidecarRoot
    Copy-Item (Join-Path $RepositoryRoot "sidecar/asr-sidecar.manifest.json") $sidecarRoot
    Copy-Item (Join-Path $RepositoryRoot "sidecar/requirements.lock") $sidecarRoot
    & $validator -PackageRoot $tempRoot | Out-Null

    Invoke-FailingCase "ASR-SIDECAR-001" {} -RequireRuntime
    Invoke-FailingCase "ASR-SIDECAR-002" { Remove-Item (Join-Path $sidecarRoot "requirements.lock") }
    Invoke-FailingCase "ASR-SIDECAR-003" { Add-Content (Join-Path $sidecarRoot "asr_server.py") "# tampered" }
    Invoke-FailingCase "ASR-SIDECAR-004" { Set-Content (Join-Path $sidecarRoot "asr-sidecar.manifest.json") "{" }

    $project = Get-Content (Join-Path $RepositoryRoot "App/App.csproj") -Raw
    foreach ($path in @("asr_server.py", "asr-sidecar.manifest.json", "requirements.lock", "Test-SidecarPackage.ps1")) {
        Assert-Contains $project $path "App publish graph"
    }

    $attributes = Get-Content (Join-Path $RepositoryRoot ".gitattributes") -Raw
    foreach ($path in @("asr_server.py", "sidecar/asr-sidecar.manifest.json", "sidecar/requirements.lock")) {
        Assert-Contains $attributes "$path text eol=lf" "Sidecar checkout normalization"
    }

    $config = Get-Content (Join-Path $RepositoryRoot "config.json.template") -Raw
    Assert-Contains $config '"python_path": "sidecar/python/python.exe"' "Config template"
    $readme = Get-Content (Join-Path $RepositoryRoot "README.txt") -Raw
    Assert-Contains $readme "ASR-SIDECAR-001" "README"
    Assert-Contains $readme "ASR-SIDECAR-005" "README"

    Write-Output "G005 publish contract hostile verification passed."
} finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
