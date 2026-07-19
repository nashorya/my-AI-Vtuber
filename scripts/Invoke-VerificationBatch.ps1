[CmdletBinding()]
param(
    [string]$Solution = "AIVTuber.slnx",
    [string]$TestProject = "AIVTuber.Tests/AIVTuber.Tests.csproj",
    [string]$PublishProject = "App/App.csproj",
    [string]$OutputDirectory = "artifacts/verification-baseline",
    [string]$TestFilter = "",
    [ValidateRange(0, [int]::MaxValue)]
    [int]$MinimumTests = 1,
    [ValidateSet("true", "false")]
    [string]$RequireSidecarRuntime = "false",
    [switch]$EvidenceOnly
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..") | ForEach-Object { if ($_.ProviderPath) { $_.ProviderPath } else { $_.Path } })
$temporaryDriveName = $null
$executionRoot = $root
if ($root.StartsWith("\\")) {
    foreach ($driveCode in 90..68) {
        $candidate = [char]$driveCode
        if ($null -eq (Get-PSDrive -Name $candidate -ErrorAction SilentlyContinue)) {
            $temporaryDriveName = [string]$candidate
            break
        }
    }
    if ($null -eq $temporaryDriveName) {
        throw "No free drive letter is available for UNC repository root $root."
    }

    New-PSDrive -Name $temporaryDriveName -PSProvider FileSystem -Root $root -Scope Script -Persist | Out-Null
    $executionRoot = "${temporaryDriveName}:\"
}

$outputPath = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    if ($null -ne $temporaryDriveName -and $OutputDirectory.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        Join-Path $executionRoot $OutputDirectory.Substring($root.Length).TrimStart([char]'\', [char]'/')
    }
    else {
        $OutputDirectory
    }
}
else {
    Join-Path $executionRoot $OutputDirectory
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

function ConvertTo-ProcessArgument {
    param([AllowEmptyString()] [Parameter(Mandatory)] [string]$Argument)

    if ($Argument.Length -gt 0 -and -not [Regex]::IsMatch($Argument, '[\s"]')) {
        return $Argument
    }

    $escaped = New-Object System.Text.StringBuilder
    [void]$escaped.Append([char]'"')
    $backslashes = 0
    foreach ($character in $Argument.ToCharArray()) {
        if ($character -eq [char]'\') {
            $backslashes++
            continue
        }

        if ($character -eq [char]'"') {
            [void]$escaped.Append([char]'\', (2 * $backslashes) + 1)
            [void]$escaped.Append($character)
            $backslashes = 0
            continue
        }

        if ($backslashes -gt 0) {
            [void]$escaped.Append([char]'\', $backslashes)
            $backslashes = 0
        }
        [void]$escaped.Append($character)
    }

    if ($backslashes -gt 0) {
        [void]$escaped.Append([char]'\', 2 * $backslashes)
    }
    [void]$escaped.Append([char]'"')
    return $escaped.ToString()
}

function Invoke-ProcessStage {
    param(
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [string]$FileName,
        [Parameter(Mandatory)] [string[]]$Arguments
    )

    $stdoutPath = Join-Path $evidenceRoot "$Name.stdout.log"
    $stderrPath = Join-Path $evidenceRoot "$Name.stderr.log"
    $startedAt = [DateTimeOffset]::UtcNow
    $exitCode = 1
    try {
        $startInfo = [Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = (Get-Command $FileName).Source
        $startInfo.WorkingDirectory = $executionRoot
        $startInfo.UseShellExecute = $false
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        $startInfo.Arguments = (($Arguments | ForEach-Object { ConvertTo-ProcessArgument $_ }) -join ' ')

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
        command = $FileName + " " + ($Arguments -join " ")
        started_at = $startedAt.ToString("o")
        finished_at = [DateTimeOffset]::UtcNow.ToString("o")
        exit_code = $exitCode
        stdout = Get-RelativeEvidencePath $evidenceRoot $stdoutPath
        stderr = Get-RelativeEvidencePath $evidenceRoot $stderrPath
    })
}

function Invoke-DotNetStage {
    param(
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [string[]]$Arguments
    )

    Invoke-ProcessStage -Name $Name -FileName "dotnet" -Arguments $Arguments
}

function Invoke-NativeTestDependencyStage {
    param(
        [Parameter(Mandatory)] [string]$ProjectPath
    )

    $name = "02-native-test-dependency"
    $stdoutPath = Join-Path $evidenceRoot "$name.stdout.log"
    $stderrPath = Join-Path $evidenceRoot "$name.stderr.log"
    $startedAt = [DateTimeOffset]::UtcNow
    $exitCode = 1
    try {
        $resolvedProjectPath = if ([IO.Path]::IsPathRooted($ProjectPath)) {
            $ProjectPath
        }
        else {
            Join-Path $executionRoot $ProjectPath
        }
        $projectDirectory = Split-Path -Parent $resolvedProjectPath
        $assetsPath = Join-Path $projectDirectory "obj/project.assets.json"
        if (-not (Test-Path -LiteralPath $assetsPath -PathType Leaf)) {
            throw "NuGet assets file was not produced for the test project: $assetsPath"
        }

        $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
        $library = $assets.libraries.PSObject.Properties |
            Where-Object { $_.Name -match '^WebRtcVadSharp/[^/]+$' } |
            Select-Object -First 1
        if ($null -eq $library) {
            throw "WebRtcVadSharp was not resolved in $assetsPath."
        }

        $packageRelativePath = [string]$library.Value.path
        if ([string]::IsNullOrWhiteSpace($packageRelativePath)) {
            $packageRelativePath = $library.Name.ToLowerInvariant()
        }
        $packageRoot = $null
        foreach ($packageFolder in $assets.packageFolders.PSObject.Properties.Name) {
            $candidate = Join-Path $packageFolder $packageRelativePath
            if (Test-Path -LiteralPath $candidate -PathType Container) {
                $packageRoot = $candidate
                break
            }
        }
        if ($null -eq $packageRoot) {
            throw "WebRtcVadSharp package root '$packageRelativePath' was not found in any project.assets.json package folder."
        }

        $nativeArchitecture = if ([Environment]::Is64BitProcess) { "x64" } else { "x86" }

        $targetFramework = $assets.project.frameworks.PSObject.Properties.Name | Select-Object -First 1
        if ([string]::IsNullOrWhiteSpace($targetFramework)) {
            throw "The test target framework could not be resolved from $assetsPath."
        }
        $sourcePath = Join-Path $packageRoot "build/$nativeArchitecture/WebRtcVad.dll"
        $testOutputPath = Join-Path $projectDirectory "bin/Release/$targetFramework"
        $destinationPath = Join-Path $testOutputPath "WebRtcVad.dll"
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "WebRtcVadSharp native dependency was not found for $nativeArchitecture at $sourcePath."
        }
        if (-not (Test-Path -LiteralPath $testOutputPath -PathType Container)) {
            throw "Release test output was not produced before native dependency preparation: $testOutputPath"
        }

        Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -Force
        $sourceHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
        $destinationHash = (Get-FileHash -LiteralPath $destinationPath -Algorithm SHA256).Hash
        if ($sourceHash -ne $destinationHash) {
            throw "WebRtcVad.dll SHA256 validation failed after copying to $destinationPath."
        }

        [ordered]@{
            package = $library.Name
            package_root = $packageRoot
            architecture = $nativeArchitecture
            source = $sourcePath
            destination = $destinationPath
            sha256 = $destinationHash.ToLowerInvariant()
        } | ConvertTo-Json | Set-Content -LiteralPath $stdoutPath
        "" | Set-Content -LiteralPath $stderrPath
        $exitCode = 0
    }
    catch {
        $_ | Out-String | Set-Content -LiteralPath $stderrPath
        if (-not (Test-Path -LiteralPath $stdoutPath)) { "" | Set-Content -LiteralPath $stdoutPath }
    }

    $stages.Add([ordered]@{
        name = $name
        command = "Resolve WebRtcVadSharp from project.assets.json, copy WebRtcVad.dll, validate SHA256"
        started_at = $startedAt.ToString("o")
        finished_at = [DateTimeOffset]::UtcNow.ToString("o")
        exit_code = $exitCode
        stdout = Get-RelativeEvidencePath $evidenceRoot $stdoutPath
        stderr = Get-RelativeEvidencePath $evidenceRoot $stderrPath
    })
}

function Get-PowerShellHost {
    $pwsh = Get-Command "pwsh" -CommandType Application -ErrorAction SilentlyContinue
    if ($null -ne $pwsh) {
        return $pwsh.Source
    }

    return [Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
}

function Find-JsonSeverity {
    param($Value)

    $severities = [Collections.Generic.List[string]]::new()
    if ($null -eq $Value) { return $severities }
    if ($Value -is [Collections.IDictionary]) {
        foreach ($key in $Value.Keys) {
            if ([string]$key -eq "severity") {
                $severities.Add([string]$Value[$key])
            }
            else {
                foreach ($severity in (Find-JsonSeverity $Value[$key])) { $severities.Add($severity) }
            }
        }
        return $severities
    }
    if ($Value -is [Collections.IEnumerable] -and $Value -isnot [string]) {
        foreach ($item in $Value) {
            foreach ($severity in (Find-JsonSeverity $item)) { $severities.Add($severity) }
        }
        return $severities
    }
    if ($Value -is [PSCustomObject]) {
        foreach ($property in $Value.PSObject.Properties) {
            if ($property.Name -eq "severity") {
                $severities.Add([string]$property.Value)
            }
            else {
                foreach ($severity in (Find-JsonSeverity $property.Value)) { $severities.Add($severity) }
            }
        }
    }
    return $severities
}

function Test-FileForSecret {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string[]]$Patterns
    )

    $stream = [IO.File]::OpenRead($Path)
    try {
        $buffer = [byte[]]::new(65536)
        $tail = ""
        while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $text = $tail + [Text.Encoding]::ASCII.GetString($buffer, 0, $read)
            foreach ($pattern in $Patterns) {
                if ($text -match $pattern) { return $true }
            }
            $tail = if ($text.Length -gt 512) { $text.Substring($text.Length - 512) } else { $text }
        }
        return $false
    }
    finally {
        $stream.Dispose()
    }
}

Push-Location $root
try {
    $head = (& git rev-parse HEAD 2>$null)
    $branch = (& git branch --show-current 2>$null)
    $dotnetInfo = (& dotnet --info 2>&1 | Out-String)
    [ordered]@{
        captured_at = [DateTimeOffset]::UtcNow.ToString("o")
        repository_root = $root
        execution_root = $executionRoot
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
    Invoke-NativeTestDependencyStage -ProjectPath $TestProject

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
    Invoke-DotNetStage "05-vulnerability-audit" @(
        "list", $Solution, "package", "--vulnerable", "--include-transitive", "--no-restore",
        "--format", "json", "--output-version", "1"
    )
    Invoke-DotNetStage "06-publish" @("publish", $PublishProject, "-c", "Release", "-r", "win-x64", "--no-restore", "-o", $publishRoot)
    $powerShellHost = Get-PowerShellHost
    Invoke-ProcessStage "07-ci-contract" $powerShellHost @(
        "-NoProfile", "-File", (Join-Path $executionRoot "scripts/Test-G006CiContract.ps1"),
        "-RepositoryRoot", $executionRoot
    )
    $sidecarArguments = @(
        "-NoProfile", "-File", (Join-Path $publishRoot "scripts/Test-SidecarPackage.ps1"),
        "-PackageRoot", $publishRoot
    )
    if ($RequireSidecarRuntime -eq "true") { $sidecarArguments += "-RequireRuntime" }
    Invoke-ProcessStage "08-sidecar-package" $powerShellHost $sidecarArguments
    Invoke-ProcessStage "09-publish-contract" $powerShellHost @(
        "-NoProfile", "-File", (Join-Path $executionRoot "scripts/Test-G005PublishContract.ps1"),
        "-RepositoryRoot", $executionRoot
    )

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
        $_ | Out-String | Set-Content (Join-Path $evidenceRoot "10-publish-manifest.log")
        $manifestExitCode = 1
    }
    $stages.Add([ordered]@{
        name = "10-publish-manifest"
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
        "01-restore", "02-build", "02-native-test-dependency", "03-test-coverage", "04-dependency-graph",
        "05-vulnerability-audit", "06-publish", "07-ci-contract",
        "08-sidecar-package", "09-publish-contract", "10-publish-manifest"
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
    if ($null -eq $trxPath) {
        $integrityErrors.Add("No TRX test result was produced.")
    }
    $coverageFiles = @(Get-ChildItem $testResultsRoot -Filter "coverage.cobertura.xml" -File -Recurse)
    if ($coverageFiles.Count -eq 0) {
        $integrityErrors.Add("No Cobertura coverage result was produced.")
    }
    foreach ($coverageFile in $coverageFiles) {
        try {
            [xml]$coverage = Get-Content $coverageFile.FullName -Raw
            $coverageRoot = $coverage.SelectSingleNode("/*[local-name()='coverage']")
            if ($null -eq $coverageRoot -or
                [int]$coverageRoot.'lines-valid' -le 0 -or
                [int]$coverageRoot.'lines-covered' -le 0) {
                $integrityErrors.Add("Coverage result is empty or invalid: $($coverageFile.FullName)")
            }
        }
        catch {
            $integrityErrors.Add("Coverage XML could not be validated: $($coverageFile.FullName): $($_.Exception.Message)")
        }
    }

    $publishFiles = @(Get-ChildItem $publishRoot -File -Recurse)
    if ($publishFiles.Count -eq 0) {
        $integrityErrors.Add("Publish output is empty.")
    }
    $requiredPublishFiles = @(
        "AIVTuber.exe", "config.json.template", "sidecar/asr_server.py",
        "sidecar/asr-sidecar.manifest.json", "sidecar/requirements.lock",
        "scripts/Test-SidecarPackage.ps1", "scripts/Test-G005PublishContract.ps1"
    )
    $publishedRelativePaths = @(
        $publishFiles | ForEach-Object {
            (Get-RelativeEvidencePath $publishRoot $_.FullName).Replace("\", "/")
        }
    )
    $missingPublishFiles = @($requiredPublishFiles | Where-Object { $_ -notin $publishedRelativePaths })
    if ($missingPublishFiles.Count -gt 0) {
        $integrityErrors.Add("Publish output is missing required files: $($missingPublishFiles -join ', ')")
    }
    if (@($publishedRelativePaths | Where-Object { $_ -eq "config.json" -or $_ -like "*/config.json" }).Count -gt 0) {
        $integrityErrors.Add("Publish output contains forbidden real configuration file config.json.")
    }

    $avatarConfigPath = Join-Path $publishRoot "assets/avatar/avatar.json"
    if (-not (Test-Path -LiteralPath $avatarConfigPath -PathType Leaf)) {
        $integrityErrors.Add("Publish output is missing assets/avatar/avatar.json.")
    }
    else {
        try {
            $avatarConfig = Get-Content -LiteralPath $avatarConfigPath -Raw | ConvertFrom-Json
            $avatarStateFiles = @(
                $avatarConfig.states.PSObject.Properties |
                    ForEach-Object { [string]$_.Value.file } |
                    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
                    Sort-Object -Unique
            )
            foreach ($avatarStateFile in $avatarStateFiles) {
                $normalizedAvatarPath = "assets/avatar/" + $avatarStateFile.Replace("\", "/")
                if ($normalizedAvatarPath -notin $publishedRelativePaths) {
                    $integrityErrors.Add("Publish output is missing avatar state file: $normalizedAvatarPath")
                }
            }
        }
        catch {
            $integrityErrors.Add("Published avatar config could not be validated: $($_.Exception.Message)")
        }
    }

    $failedStages = @($stages | Where-Object { $_.exit_code -ne 0 } | ForEach-Object { $_.name })
    if (-not $EvidenceOnly -and $failedStages.Count -gt 0) {
        $integrityErrors.Add("Blocking stages failed: $($failedStages -join ', ')")
    }

    $auditStage = $stages | Where-Object { $_.name -eq "05-vulnerability-audit" } | Select-Object -First 1
    $blockingVulnerabilities = @()
    if ($null -ne $auditStage -and $auditStage.exit_code -eq 0) {
        try {
            $auditJson = Get-Content (Join-Path $evidenceRoot $auditStage.stdout) -Raw | ConvertFrom-Json
            $blockingVulnerabilities = @(
                Find-JsonSeverity $auditJson |
                    Where-Object { $_ -in @("High", "Critical") }
            )
            if ($blockingVulnerabilities.Count -gt 0) {
                $integrityErrors.Add("Dependency audit found $($blockingVulnerabilities.Count) High/Critical vulnerabilities.")
            }
        }
        catch {
            $integrityErrors.Add("Dependency audit JSON could not be validated: $($_.Exception.Message)")
        }
    }

    $secretPatterns = @(
        'github_pat_[A-Za-z0-9_]{20,}',
        'gh[pousr]_[A-Za-z0-9]{36,}',
        'AKIA[0-9A-Z]{16}',
        'sk-[A-Za-z0-9]{20,}',
        '-----BEGIN [A-Z ]*PRIVATE KEY-----',
        'Bearer\s+[A-Za-z0-9._~+/=-]{20,}'
    )
    $secretFindings = [Collections.Generic.List[string]]::new()
    foreach ($file in (Get-ChildItem $evidenceRoot -File -Recurse)) {
        if (Test-FileForSecret -Path $file.FullName -Patterns $secretPatterns) {
            $secretFindings.Add((Get-RelativeEvidencePath $evidenceRoot $file.FullName))
        }
    }
    if ($secretFindings.Count -gt 0) {
        $integrityErrors.Add("Potential credential material detected in evidence files: $(@($secretFindings) -join ', ')")
        foreach ($relativePath in $secretFindings) {
            Remove-Item -LiteralPath (Join-Path $evidenceRoot $relativePath) -Force -ErrorAction SilentlyContinue
        }
    }

    $unsafeFiles = @(
        Get-ChildItem $evidenceRoot -File -Recurse |
            Where-Object { Test-FileForSecret -Path $_.FullName -Patterns $secretPatterns }
    )
    if ($unsafeFiles.Count -eq 0) {
        "Secret scan completed after sanitization." | Set-Content (Join-Path $evidenceRoot "artifact-upload-approved.txt")
    }
    else {
        $integrityErrors.Add("Evidence sanitization failed; artifact upload approval was withheld.")
    }

    $summary = [ordered]@{
        schema_version = 1
        evidence_only = [bool]$EvidenceOnly
        head = $head
        generated_at = [DateTimeOffset]::UtcNow.ToString("o")
        test_results = $testCounts
        minimum_tests = $MinimumTests
        test_filter = $TestFilter
        require_sidecar_runtime = ($RequireSidecarRuntime -eq "true")
        integrity_passed = ($integrityErrors.Count -eq 0)
        integrity_errors = @($integrityErrors)
        failed_stages = $failedStages
        high_critical_vulnerabilities = $blockingVulnerabilities.Count
        coverage_files = @($coverageFiles | ForEach-Object { Get-RelativeEvidencePath $evidenceRoot $_.FullName })
        secret_scan_passed = ($secretFindings.Count -eq 0)
        stages = @($stages)
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $evidenceRoot "summary.json")

    if ($integrityErrors.Count -gt 0) {
        $integrityErrors | ForEach-Object { Write-Error $_ -ErrorAction Continue }
        exit 2
    }

    Write-Host "Verification batch complete: $evidenceRoot"
    exit 0
}
finally {
    Pop-Location
    if ($null -ne $temporaryDriveName) {
        Remove-PSDrive -Name $temporaryDriveName -Scope Script -Force -ErrorAction SilentlyContinue
    }
}
