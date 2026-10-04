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
    if (-not $app.HasExited) { Stop-Process -Id $app.Id -Force; $app.WaitForExit() }
}
