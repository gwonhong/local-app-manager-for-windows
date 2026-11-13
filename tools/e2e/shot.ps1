# Screenshot of the test instance's window:  ./tools/e2e/shot.ps1 name [-Scale 0.6]  -> %TEMP%\lam-e2e\name.png
param([string]$Name = "shot", [double]$Scale = 0.6)
$repo = Resolve-Path "$PSScriptRoot\..\.."
$state = Join-Path $env:TEMP "lam-e2e"
$tool = "$repo\tools\WindowShot\bin\Debug\net10.0-windows10.0.26100.0\WindowShot.exe"
if (-not (Test-Path $tool)) { dotnet build "$repo\tools\WindowShot\WindowShot.csproj" -v q | Out-Null }
& $tool ([int](Get-Content "$state\pid.txt")) "$state\$Name.png" --scale $Scale
