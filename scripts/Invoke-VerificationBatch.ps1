[CmdletBinding()]
param(
    [string]$Solution = "AIVTuber.slnx",
    [string]$TestProject = "AIVTuber.Tests/AIVTuber.Tests.csproj",
    [string]$PublishProject = "App/App.csproj",
    [string]$OutputDirectory = "artifacts/verification-baseline",
    [string]$TestFilter = "",
    [ValidateRange(0, [int]::MaxValue)]
    [int]$MinimumTests = 1
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$outputPath = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory
}
else {
    Join-Path $root $OutputDirectory
}
# PowerShell's provider resolver preserves UNC roots (including \\wsl.localhost\...)
# that System.IO.Path.GetFullPath can reject or reinterpret.
$evidenceRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($outputPath)
$publishRoot = Join-Path $evidenceRoot "publish"
$testResultsRoot = Join-Path $evidenceRoot "test-results"
$stages = [Collections.Generic.List[object]]::new()

Remove-Item $evidenceRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item $evidenceRoot, $publishRoot, $testResultsRoot -ItemType Directory -Force | Out-Null

function Get-RelativeEvidencePath {
    param(
        [Parameter(Mandatory)] [string]$BasePath,
        [Parameter(Mandatory)] [string]$TargetPath
    )

    $baseWithSeparator = $BasePath.TrimEnd([char]'\', [char]'/') + [IO.Path]::DirectorySeparatorChar
    [Uri]$baseUri = $baseWithSeparator
    [Uri]$targetUri = $TargetPath
    $relativeUri = $baseUri.MakeRelativeUri($targetUri)
    if ($relativeUri.IsAbsoluteUri) {
        return $TargetPath
    }

    return [Uri]::UnescapeDataString($relativeUri.ToString()).Replace('/', [IO.Path]::DirectorySeparatorChar)
}

function Invoke-DotNetStage {
    param(
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [string[]]$Arguments
    )

    $stdoutPath = Join-Path $evidenceRoot "$Name.stdout.log"
    $stderrPath = Join-Path $evidenceRoot "$Name.stderr.log"
    $startedAt = [DateTimeOffset]::UtcNow
    $exitCode = 1
    try {
        $startInfo = [Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = (Get-Command dotnet).Source
        $startInfo.WorkingDirectory = $root
        $startInfo.UseShellExecute = $false
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        foreach ($argument in $Arguments) {
            $startInfo.ArgumentList.Add($argument)
        }

        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $startInfo
        [void]$process.Start()
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        $stdout | Set-Content $stdoutPath
        $stderr | Set-Content $stderrPath
        if (-not [string]::IsNullOrWhiteSpace($stdout)) { Write-Host $stdout }
        if (-not [string]::IsNullOrWhiteSpace($stderr)) { Write-Warning $stderr }
        $exitCode = $process.ExitCode
    }
    catch {
        $_ | Out-String | Set-Content -Path $stderrPath
        if (-not (Test-Path $stdoutPath)) { "" | Set-Content $stdoutPath }
        $exitCode = 1
    }

    $stages.Add([ordered]@{
        name = $Name
        command = "dotnet " + ($Arguments -join " ")
        started_at = $startedAt.ToString("o")
        finished_at = [DateTimeOffset]::UtcNow.ToString("o")
        exit_code = $exitCode
        stdout = Get-RelativeEvidencePath $evidenceRoot $stdoutPath
        stderr = Get-RelativeEvidencePath $evidenceRoot $stderrPath
    })
}

Push-Location $root
try {
    $head = (& git rev-parse HEAD 2>$null)
    $branch = (& git branch --show-current 2>$null)
    $dotnetInfo = (& dotnet --info 2>&1 | Out-String)
    [ordered]@{
        captured_at = [DateTimeOffset]::UtcNow.ToString("o")
        repository_root = $root
        head = $head
        branch = $branch
        runner_os = $env:RUNNER_OS
        runner_arch = $env:RUNNER_ARCH
        dotnet_info = $dotnetInfo
        test_filter = $TestFilter
        minimum_tests = $MinimumTests
    } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $evidenceRoot "environment.json")

    Invoke-DotNetStage "01-restore" @("restore", $Solution, "--locked-mode")
    Invoke-DotNetStage "02-build" @("build", $Solution, "-c", "Release", "--no-restore")

    $testArguments = @(
        "test", $TestProject, "-c", "Release", "--no-build",
        "--logger", "trx;LogFileName=baseline.trx",
        "--results-directory", $testResultsRoot,
        "--collect", "XPlat Code Coverage"
    )
    if (-not [string]::IsNullOrWhiteSpace($TestFilter)) {
        $testArguments += @("--filter", $TestFilter)
    }
    Invoke-DotNetStage "03-test-coverage" $testArguments
    Invoke-DotNetStage "04-dependency-graph" @("list", $Solution, "package", "--include-transitive", "--no-restore")
    Invoke-DotNetStage "05-vulnerability-audit" @("list", $Solution, "package", "--vulnerable", "--include-transitive", "--no-restore")
    Invoke-DotNetStage "06-publish" @("publish", $PublishProject, "-c", "Release", "-r", "win-x64", "--no-restore", "-o", $publishRoot)

    $manifestPath = Join-Path $evidenceRoot "publish-manifest.json"
    $manifestStartedAt = [DateTimeOffset]::UtcNow
    $manifestExitCode = 0
    try {
        $manifest = Get-ChildItem $publishRoot -File -Recurse | Sort-Object FullName | ForEach-Object {
            [ordered]@{
                path = (Get-RelativeEvidencePath $publishRoot $_.FullName).Replace("\", "/")
                length = $_.Length
                sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
        @($manifest) | ConvertTo-Json -Depth 4 | Set-Content $manifestPath
    }
    catch {
        $_ | Out-String | Set-Content (Join-Path $evidenceRoot "07-publish-manifest.log")
        $manifestExitCode = 1
    }
    $stages.Add([ordered]@{
        name = "07-publish-manifest"
        command = "Get-ChildItem/Get-FileHash $publishRoot"
        started_at = $manifestStartedAt.ToString("o")
        finished_at = [DateTimeOffset]::UtcNow.ToString("o")
        exit_code = $manifestExitCode
        log = "publish-manifest.json"
    })

    $testCounts = [ordered]@{ total = 0; executed = 0; passed = 0; failed = 0; skipped = 0 }
    $trxPath = Get-ChildItem $testResultsRoot -Filter *.trx -File -Recurse | Select-Object -First 1
    if ($null -ne $trxPath) {
        [xml]$trx = Get-Content $trxPath.FullName
        $counters = $trx.SelectSingleNode("//*[local-name()='Counters']")
        if ($null -ne $counters) {
            foreach ($name in @("total", "executed", "passed", "failed")) {
                if ($null -ne $counters.$name) { $testCounts[$name] = [int]$counters.$name }
            }
            if ($null -ne $counters.notExecuted) { $testCounts.skipped = [int]$counters.notExecuted }
        }
    }

    $requiredStageNames = @(
        "01-restore", "02-build", "03-test-coverage", "04-dependency-graph",
        "05-vulnerability-audit", "06-publish", "07-publish-manifest"
    )
    $recordedStageNames = @($stages | ForEach-Object { $_.name })
    $missingStages = @($requiredStageNames | Where-Object { $_ -notin $recordedStageNames })
    $integrityErrors = [Collections.Generic.List[string]]::new()
    if ($missingStages.Count -gt 0) {
        $integrityErrors.Add("Missing stage records: $($missingStages -join ', ')")
    }
    if ($testCounts.executed -lt $MinimumTests) {
        $integrityErrors.Add("Test filter executed $($testCounts.executed) tests; required minimum is $MinimumTests.")
    }

    $summary = [ordered]@{
        schema_version = 1
        evidence_only = $true
        head = $head
        generated_at = [DateTimeOffset]::UtcNow.ToString("o")
        test_results = $testCounts
        minimum_tests = $MinimumTests
        test_filter = $TestFilter
        integrity_passed = ($integrityErrors.Count -eq 0)
        integrity_errors = @($integrityErrors)
        stages = @($stages)
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $evidenceRoot "summary.json")

    if ($integrityErrors.Count -gt 0) {
        $integrityErrors | ForEach-Object { Write-Error $_ -ErrorAction Continue }
        exit 2
    }

    Write-Host "Evidence batch complete. Product-stage failures are recorded, not hidden: $evidenceRoot"
    exit 0
}
finally {
    Pop-Location
}
