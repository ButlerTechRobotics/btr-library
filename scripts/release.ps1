<#
.SYNOPSIS
  Builds BTR Library and its installer, then (optionally) publishes it as a GitHub Release.

.DESCRIPTION
  Run on a PC with SOLIDWORKS installed (the build needs its API files) and Inno Setup 6.
  The version comes from <Version> in src\BtrLibrary.csproj, so bump that first for a new release.

    .\scripts\release.ps1            # build dist\BTR-Library-Setup-<version>.exe only
    .\scripts\release.ps1 -Publish   # ...and create GitHub release v<version> with the installer attached

  Students then download the installer from the repo's Releases page, and the add-in shows
  "Update available" when a newer release is published.
  Parts-list changes don't need a release: Sync reads manifest/library.json from GitHub.
#>
param(
    [switch]$Publish,
    [string]$Notes = ""
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $repo "src\BtrLibrary.csproj"
$out  = Join-Path $repo "src\bin\Release\net48"
$dist = Join-Path $repo "dist"

[xml]$xml = Get-Content $proj
$version = ($xml.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { throw "No <Version> in $proj" }
$tag = "v$version"
Write-Host "BTR Library $version" -ForegroundColor Cyan

if (Get-Process -Name SLDWORKS -ErrorAction SilentlyContinue) {
    Write-Warning "SOLIDWORKS is running. If it loads the add-in from this build folder the build can't replace the DLL; close it if the build fails."
}

$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
          "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it from https://jrsoftware.org/isdl.php" }

if ($Publish) {
    # A release must match what's on GitHub: committed, pushed, and not already released.
    Push-Location $repo
    try {
        if (git status --porcelain) { throw "Uncommitted changes. Commit (and push) before publishing." }
        git fetch -q origin main
        if ((git rev-parse HEAD) -ne (git rev-parse origin/main)) { throw "HEAD isn't origin/main. Push (or pull) first." }
        gh release view $tag *> $null
        if ($LASTEXITCODE -eq 0) { throw "Release $tag already exists. Bump <Version> in src\BtrLibrary.csproj." }
    } finally { Pop-Location }
}

Write-Host "Building..." -ForegroundColor Cyan
dotnet build $proj -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

Write-Host "Making installer..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force $dist | Out-Null
& $iscc /Q "/DAppVersion=$version" "/DSourceDir=$out" "/DOutputDir=$dist" (Join-Path $repo "installer\BtrLibrary.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed." }
$setup = Join-Path $dist "BTR-Library-Setup-$version.exe"
Write-Host "Installer: $setup" -ForegroundColor Green

if ($Publish) {
    if (-not $Notes) {
        $Notes = "Download **BTR-Library-Setup-$version.exe** below, close SOLIDWORKS, and run it.`n`n" +
                 "Parts come from the team parts list on GitHub through Sync, so this installer is only needed for add-in updates."
    }
    Push-Location $repo
    try {
        gh release create $tag $setup --target main --title "BTR Library $version" --notes $Notes
        if ($LASTEXITCODE -ne 0) { throw "gh release create failed." }
    } finally { Pop-Location }
    Write-Host "Published $tag" -ForegroundColor Green
}
