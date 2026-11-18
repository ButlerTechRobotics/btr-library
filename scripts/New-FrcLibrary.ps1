<#
  Creates an empty FRC library folder with the Vendor\Category layout the add-in expects.
  Then drop vendor CAD (STEP or SOLIDWORKS files) into the matching folders.

  Usage:  powershell -ExecutionPolicy Bypass -File New-BtrLibraryrary.ps1 -Root "D:\Team Drive\BTR Library"
#>
param(
  [string]$Root = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'BTR Library')
)

$layout = [ordered]@{
  'WCP'          = 'Gears','Pulleys & Belts','Sprockets & Chain','Bearings','Shafts & Hubs','Structure','Wheels','Gearboxes','Swerve'
  'REV'          = 'Motors','Gears','Pulleys & Belts','Bearings','Shafts & Hubs','Structure','Wheels','Gearboxes','Electronics'
  'CTRE'         = 'Motors','Electronics','Sensors'
  'AndyMark'     = 'Gears','Gearboxes','Wheels','Structure','Electronics'
  'The Thrifty Bot' = 'Structure','Wheels','Bearings','Swerve','Electronics'
  'Swyft Robotics'  = 'Swerve','Structure','Wheels'
  'Hardware'     = 'Bolts','Nuts','Washers','Rivets','Spacers'
  'Team Parts'   = 'Brackets','Plates','Misc'
}

foreach ($vendor in $layout.Keys) {
  foreach ($cat in $layout[$vendor]) {
    New-Item -ItemType Directory -Force -Path (Join-Path $Root (Join-Path $vendor $cat)) | Out-Null
  }
}

@"
BTR Library layout
==================
  <Vendor>\<Category>[\<Subcategory>]\<Part>.SLDPRT | .SLDASM | .STEP

Optional files next to a part (same base name):
  <Part>.png   preview image (otherwise the SOLIDWORKS thumbnail is used)
  <Part>.meta  key=value lines:
                 name=Kraken X60
                 description=Brushless motor with integrated controller
                 tags=motor, brushless, kraken, wcp, ctre
                 url=https://wcproducts.com/...

STEP files are converted to SOLIDWORKS on first insert and cached in a _converted folder.
Folders starting with _ are ignored by the scanner.

Tip for teams: put this folder on a shared drive mapped to the SAME path on every
computer, because SOLIDWORKS assemblies store absolute paths to their components.
"@ | Set-Content -Encoding UTF8 (Join-Path $Root 'README.txt')

Write-Host "Library created at $Root"
Write-Host "Set it in SOLIDWORKS: BTR Library task pane > click the 'Library:' path at the bottom."
