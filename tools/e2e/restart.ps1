# Stop the test instance, rebuild, relaunch against a throwaway data folder (never the real %LOCALAPPDATA%).
# Run unsandboxed: WinUI renders a black window when started from a sandboxed shell.
#   ./tools/e2e/restart.ps1 [-NoBuild] [-AppArgs @('--add', 'C:\path')]
param([switch]$NoBuild, [string[]]$AppArgs = @())
$repo = Resolve-Path "$PSScriptRoot\..\.."
$state = Join-Path $env:TEMP "lam-e2e"
New-Item -ItemType Directory -Force "$state\data" | Out-Null
if (Test-Path "$state\pid.txt") { Stop-Process -Id ([int](Get-Content "$state\pid.txt")) -Force -ErrorAction SilentlyContinue }
Start-Sleep -Milliseconds 600
if (-not $NoBuild) {
  $out = dotnet build "$repo\src\LocalAppManager\LocalAppManager.csproj" 2>&1
  $errs = $out | Select-String -Pattern ": error " | Sort-Object -Unique
  if ($errs) { $errs; throw "build failed" }
  "build ok"
}
$env:LOCALAPPMANAGER_DATA = "$state\data"
$exe = "$repo\src\LocalAppManager\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\LocalAppManager.exe"
$p = if ($AppArgs.Count) { Start-Process $exe -ArgumentList $AppArgs -PassThru } else { Start-Process $exe -PassThru }
$p.Id | Set-Content "$state\pid.txt"
Start-Sleep 4
"pid=$($p.Id) data=$state\data"
