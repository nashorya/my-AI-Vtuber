param([Parameter(Mandatory)][string]$AppDirectory)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$app = Start-Process (Join-Path $AppDirectory 'AIVTuber.exe') -WorkingDirectory $AppDirectory -PassThru
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    do {
        Start-Sleep -Milliseconds 500
        $app.Refresh()
        if ($app.HasExited) { throw "Installed app exited before login UI (exit $($app.ExitCode))" }
    } while ($app.MainWindowHandle -eq 0 -and [DateTime]::UtcNow -lt $deadline)
    if ($app.MainWindowHandle -eq 0) { throw 'Installed app did not open a window' }
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
    function Find-Name([string]$Name) {
        $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $Name)
        return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    }
    $register = $null
    do {
        Start-Sleep -Milliseconds 300
        $register = Find-Name '用邀请码注册'
    } while ($null -eq $register -and [DateTime]::UtcNow -lt $deadline)
    if ($null -eq $register) { throw 'Invite registration entry not found' }
    $register.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 400
    foreach ($name in @('邀请码', '账号名', '再输一次密码', '注册并登录')) {
        if ($null -eq (Find-Name $name)) { throw "Registration field missing: $name" }
    }
    Write-Host 'PASS: installed Windows app opens the shared-account login and invite registration UI'
} finally {
    # Close as a user would, then clean up only descendants of this test app.
    # Killing only the WPF host can leave WebView/sidecar processes holding files.
    $allProcesses = @(Get-CimInstance Win32_Process)
    $tree = @($app.Id)
    do {
        $children = @($allProcesses | Where-Object { $_.ParentProcessId -in $tree -and $_.ProcessId -notin $tree } | ForEach-Object { [int]$_.ProcessId })
        $tree += $children
    } while ($children.Count -gt 0)
    try {
        if (-not $app.HasExited) {
            $null = $app.CloseMainWindow()
            if (-not $app.WaitForExit(10000)) { throw 'Application did not exit after normal window close' }
        }
        $app.Dispose()
        $releaseDeadline = [DateTime]::UtcNow.AddSeconds(15)
        do {
            try {
                $probe = [IO.File]::Open((Join-Path $AppDirectory 'AIVTuber.exe'), 'Open', 'Write', 'None')
                $probe.Dispose()
                Write-Host 'PASS: normally closed application released its executable'
                break
            } catch {
                if ([DateTime]::UtcNow -ge $releaseDeadline) { throw }
                Start-Sleep -Milliseconds 200
            }
        } while ($true)
    } finally {
        # Cleanup never turns a failed natural shutdown/release check into a pass.
        foreach ($childId in $tree) {
            $child = Get-Process -Id $childId -ErrorAction SilentlyContinue
            if ($null -ne $child) {
                Write-Host "Cleanup only: surviving test process $($child.ProcessName) ($childId)"
                Stop-Process -Id $childId -Force -ErrorAction SilentlyContinue
                $child.WaitForExit()
                $child.Dispose()
            }
        }
    }
}
