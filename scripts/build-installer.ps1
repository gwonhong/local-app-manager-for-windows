<#
.SYNOPSIS
  Builds the installer (Setup.exe), the portable zip and the update packages with Velopack.

.DESCRIPTION
  The same steps the release workflow runs (.github/workflows/release.yml), so anyone can build
  the installer from source and compare it with the one on GitHub Releases.

    ./scripts/build-installer.ps1                       # this machine's architecture, version from Directory.Build.props
    ./scripts/build-installer.ps1 -Platform ARM64 -Version 0.3.0

  Output (artifacts/ is git-ignored):
    artifacts/installer/<rid>/LocalAppManager.App-win-x64-Setup.exe     installer (per-user, no admin)
    artifacts/installer/<rid>/LocalAppManager.App-win-x64-Portable.zip  unzip-and-run copy
    artifacts/installer/<rid>/*.nupkg, releases.win-x64.json, …         update feed files

  vpk is pinned in dotnet-tools.json and restored with `dotnet tool restore`.

.PARAMETER PreviousReleasesDir
  A folder holding earlier packages of the same channel (CI fills it with `vpk download github`).
  When given, vpk also builds a delta package against the newest one.
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64')]
    [string] $Platform = $(if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'ARM64' } else { 'x64' }),

    [string] $Version,

    [string] $OutputDir,

    [string] $PreviousReleasesDir
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $rid = "win-$($Platform.ToLowerInvariant())"
    if (-not $Version) {
        $Version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    }
    if (-not $OutputDir) { $OutputDir = "artifacts/installer/$rid" }
    $publishDir = "artifacts/publish/$rid"

    # Pack id is also the install folder (%LocalAppData%\LocalAppManager.App). It must differ from the
    # data folder (%LocalAppData%\LocalAppManager): uninstalling deletes the whole install folder.
    $packId = 'LocalAppManager.App'

    Write-Host "== Local App Manager $Version ($rid)" -ForegroundColor Cyan

    dotnet tool restore
    if ($LASTEXITCODE) { throw 'dotnet tool restore failed' }

    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    dotnet publish src/LocalAppManager/LocalAppManager.csproj `
        -c Release `
        -p:Platform=$Platform `
        -p:PublishProfile=$rid `
        -p:Version=$Version `
        -o $publishDir
    if ($LASTEXITCODE) { throw 'dotnet publish failed' }

    New-Item -ItemType Directory -Force $OutputDir | Out-Null
    if ($PreviousReleasesDir -and (Test-Path $PreviousReleasesDir)) {
        # vpk builds a delta from the newest full package it finds in the output folder. Skip this
        # version's own package (rebuilding an existing tag), which vpk would refuse as a duplicate.
        Get-ChildItem $PreviousReleasesDir -Filter *-full.nupkg |
            Where-Object { $_.Name -notlike "*-$Version-*" } |
            Copy-Item -Destination $OutputDir -Force
    }

    dotnet vpk pack `
        --packId $packId `
        --packVersion $Version `
        --packDir $publishDir `
        --mainExe LocalAppManager.exe `
        --packTitle 'Local App Manager' `
        --packAuthors 'Local App Manager contributors' `
        --icon src/LocalAppManager/Assets/AppIcon.ico `
        --channel $rid `
        --shortcuts StartMenuRoot `
        --outputDir $OutputDir
    if ($LASTEXITCODE) { throw 'vpk pack failed' }

    # Drop the copied-in previous package: it already lives on its own release.
    if ($PreviousReleasesDir) {
        $new = (Get-Content "$OutputDir/assets.$rid.json" -Raw | ConvertFrom-Json).RelativeFileName
        Get-ChildItem $OutputDir -Filter *-full.nupkg | Where-Object { $_.Name -notin $new } | Remove-Item
    }

    Write-Host "== Done: $OutputDir" -ForegroundColor Green
    Get-ChildItem $OutputDir | Format-Table Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } -AutoSize
}
finally {
    Pop-Location
}
