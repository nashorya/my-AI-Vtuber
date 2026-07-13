[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = "Stop"
$workflowPath = Join-Path $RepositoryRoot ".github/workflows/build-windows.yml"
$batchPath = Join-Path $RepositoryRoot "scripts/Invoke-VerificationBatch.ps1"
$workflow = Get-Content $workflowPath -Raw
$batch = Get-Content $batchPath -Raw

function Assert-Match([string]$Text, [string]$Pattern, [string]$Message) {
    if ($Text -notmatch $Pattern) { throw $Message }
}

function Assert-NotMatch([string]$Text, [string]$Pattern, [string]$Message) {
    if ($Text -match $Pattern) { throw $Message }
}

Assert-Match $workflow '(?m)^\s*branches:\s*\[main\]\s*$' "Windows CI must target main."
Assert-Match $workflow '(?m)^\s*pull_request:\s*$' "Windows CI must run for pull requests."
Assert-Match $workflow '(?m)^\s*tags:\s*\[''v\*''\]\s*$' "Windows CI must preserve version-tag releases."
Assert-NotMatch $workflow '(?m)^\s*continue-on-error:\s*true\s*$' "Windows CI must be blocking."
Assert-Match $workflow 'persist-credentials:\s*false' "Checkout credentials must not persist."
Assert-Match $workflow '-MinimumTests\s+200' "Windows CI must enforce the full-suite test floor."
Assert-Match $workflow '-RequireSidecarRuntime\s+\$\{\{\s*startsWith\(github\.ref' "Version tags must require the managed sidecar runtime."
Assert-Match $workflow "hashFiles\('artifacts/windows-quality/artifact-upload-approved\.txt'\)" "Evidence upload must require post-scan approval."

foreach ($required in @(
    'restore.*--locked-mode',
    'build.*Release.*--no-restore',
    'test.*Release.*--no-build',
    'XPlat Code Coverage',
    'vulnerability-audit',
    'High.*Critical',
    'Test-SidecarPackage\.ps1',
    'Test-G005PublishContract\.ps1',
    'publish-manifest\.json',
    'secret_scan_passed',
    'Test-FileForSecret',
    'artifact-upload-approved\.txt'
)) {
    Assert-Match $batch $required "Verification batch is missing contract fragment: $required"
}

Write-Output "G006 CI contract verification passed."
