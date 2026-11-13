# Dot-source for UI Automation helpers:  . ./tools/e2e/uia.ps1 ; $root = Get-AppRoot (Get-TestPid)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
function Get-TestPid { [int](Get-Content (Join-Path $env:TEMP "lam-e2e\pid.txt")) }
function Get-AppRoot([int]$ProcessId) {
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
  [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
}
function Find-ById($root, [string]$id) {
  $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)))
}
function Find-ByName($root, [string]$name) {
  $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)))
}
function Invoke-El($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Select-Project($root, [string]$name) {
  $list = Find-ById $root "ProjectList"
  $cond = New-Object System.Windows.Automation.AndCondition(
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)),
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)))
  $list.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
}
function Get-LogLines($root) {
  $log = Find-ById $root "LogView"
  $log.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text))) | ForEach-Object { $_.Current.Name }
}
# Useful AutomationIds: AddProjectButton, ProjectList, ProjectTitle, ProjectState, ProjectPort, ProjectUrl,
# StartButton, StopButton, RestartButton, ErrorBar, ReassignPortButton, ScriptChips, LogFilter, LogSummary,
# LogView, CopyLogButton, ClearLogButton, SettingsButton, ProjectSettingsButton, GroupToggle.
