param([string]$Page = "Dashboard", [string]$Out = "", [int]$Wait = 1200)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$proc = Get-Process | Where-Object { $_.MainWindowTitle -like "*FIFINE*" } | Select-Object -First 1
if (-not $proc) {
    Write-Error "FIFINE window not found"
    exit 1
}

$root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, $Page)
$item = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
if (-not $item) {
    Write-Error "Nav item not found: $Page"
    exit 2
}

$pattern = $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
$pattern.Select()
Start-Sleep -Milliseconds $Wait

if ($Out) {
    & "$PSScriptRoot\screenshot.ps1" -Out $Out | Out-Null
    Write-Output "navigated to $Page -> $Out"
} else {
    Write-Output "navigated to $Page"
}
