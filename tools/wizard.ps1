param([int]$Steps = 6, [string]$OutPrefix = "wizard")

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Get-Root {
    $proc = Get-Process | Where-Object { $_.MainWindowTitle -like "*FIFINE*" } | Select-Object -First 1
    if (-not $proc) { throw "FIFINE window not found" }
    [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
}

function Find-ByName($root, $name) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

for ($i = 1; $i -le $Steps; $i++) {
    $root = Get-Root
    $button = Find-ByName $root "Continue"
    if (-not $button) { $button = Find-ByName $root "Finish" }
    if (-not $button) {
        Write-Output "no continue button at step $i"
        break
    }

    $pattern = $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
    Start-Sleep -Milliseconds 1500

    & "$PSScriptRoot\screenshot.ps1" -Out ("{0}_{1}.png" -f $OutPrefix, $i) | Out-Null
    Write-Output "step $i -> {0}_{1}.png" -f $OutPrefix, $i
}
