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
Assert-Match $workflow '-RequireSidecarRuntime\s+false' "Standard API-ASR releases must not require the optional managed sidecar runtime."
Assert-Match $workflow "hashFiles\('artifacts/windows-quality/artifact-upload-approved\.txt'\)" "Evidence upload must require post-scan approval."
Assert-Match $batch 'Get-Command\s+"pwsh"\s+-CommandType\s+Application\s+-ErrorAction\s+SilentlyContinue' "Verification batch must prefer pwsh when it is available."
Assert-Match $batch '\[Diagnostics\.Process\]::GetCurrentProcess\(\)\.MainModule\.FileName' "Verification batch must fall back to the current PowerShell host."
Assert-Match $batch '(?m)^\s*\$powerShellHost\s*=\s*Get-PowerShellHost\s*$' "Verification batch must resolve one PowerShell host for child scripts."
Assert-NotMatch $batch 'Invoke-ProcessStage\s+"(?:07-ci-contract|08-sidecar-package|09-publish-contract)"\s+"pwsh"' "Verification stages must use the resolved PowerShell host."
Assert-Match $batch '(?m)^function\s+Invoke-NativeTestDependencyStage\s*\{' "Verification batch must define native test dependency preparation."
Assert-Match $batch 'obj/project\.assets\.json' "Native dependency preparation must use the restored test project assets."
Assert-Match $batch "\^WebRtcVadSharp/\[\^/\]\+\$" "Native dependency preparation must resolve the WebRtcVadSharp package and version from project.assets.json."
Assert-Match $batch '\$packageRelativePath\s*=\s*\[string\]\$library\.Value\.path' "Native dependency preparation must use the package path resolved by project.assets.json."
Assert-Match $batch '\$assets\.packageFolders\.PSObject\.Properties\.Name' "Native dependency preparation must resolve the NuGet package root from project.assets.json."
Assert-Match $batch 'Join-Path\s+\$packageFolder\s+\$packageRelativePath' "Native dependency preparation must combine the assets package folder and resolved package path."
Assert-NotMatch $batch 'webrtcvadsharp[/\\]1\.1\.0' "Native dependency preparation must not hard-code a WebRtcVadSharp version."
Assert-Match $batch '\$nativeArchitecture\s*=\s*if\s*\(\[Environment\]::Is64BitProcess\)\s*\{\s*"x64"\s*\}\s*else\s*\{\s*"x86"\s*\}' "Native dependency preparation must select the x64 or x86 native library for the current process."
Assert-Match $batch 'build/\$nativeArchitecture/WebRtcVad\.dll' "Native dependency preparation must select the architecture-specific WebRtcVad.dll."
Assert-Match $batch 'bin/Release/\$targetFramework' "Native dependency preparation must target the Release test output."
Assert-Match $batch '(?s)Test-Path\s+-LiteralPath\s+\$sourcePath\s+-PathType\s+Leaf.*?throw' "Native dependency preparation must fail when the package DLL is missing."
Assert-Match $batch '(?s)Test-Path\s+-LiteralPath\s+\$testOutputPath\s+-PathType\s+Container.*?throw' "Native dependency preparation must fail when Release test output is missing."
Assert-Match $batch 'Copy-Item\s+-LiteralPath\s+\$sourcePath\s+-Destination\s+\$destinationPath\s+-Force' "Native dependency preparation must copy WebRtcVad.dll into test output."
Assert-Match $batch 'Get-FileHash\s+-LiteralPath\s+\$sourcePath\s+-Algorithm\s+SHA256' "Native dependency preparation must hash the package DLL."
Assert-Match $batch 'Get-FileHash\s+-LiteralPath\s+\$destinationPath\s+-Algorithm\s+SHA256' "Native dependency preparation must hash the copied DLL."
Assert-Match $batch '(?s)if\s*\(\$sourceHash\s+-ne\s+\$destinationHash\).*?throw' "Native dependency preparation must fail on a SHA256 mismatch."
Assert-Match $batch '(?s)Invoke-DotNetStage\s+"02-build".*?Invoke-NativeTestDependencyStage\s+-ProjectPath\s+\$TestProject.*?Invoke-DotNetStage\s+"03-test-coverage"' "Native dependency preparation must run after build and before tests."
Assert-Match $batch '(?s)name\s*=\s*\$name.*?exit_code\s*=\s*\$exitCode' "Native dependency preparation must record its result for the final integrity gate."
Assert-Match $batch '(?s)\$requiredStageNames\s*=\s*@\(.*?"02-native-test-dependency".*?\)' "Native dependency preparation must be a required verification stage."
Assert-Match $batch '(?s)\$failedStages\s*=.*?if\s*\(-not\s+\$EvidenceOnly\s+-and\s+\$failedStages\.Count\s+-gt\s+0\).*?Blocking stages failed' "Failed native dependency preparation must block normal verification."
Assert-Match $batch 'assets/avatar/avatar\.json' "Verification must require the published avatar config."
Assert-Match $batch 'missing avatar state file' "Verification must fail when a referenced avatar sprite is missing."

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
