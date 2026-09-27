<#
.SYNOPSIS
  Downloads the real aerial photos the simulated payload camera uses (src/UavOps.Simulator/imagery).

.DESCRIPTION
  Fetches each image listed in src/UavOps.Simulator/imagery/imagery.json - openly licensed drone
  orthomosaics (Cloud-Optimized GeoTIFF) from OpenAerialMap - into that folder, once. The files
  are git-ignored (~160 MB); the manifest, with each image's license and attribution, is committed.
  Without them the simulator still runs and draws the ground from the OSM map instead.

  Internet is needed only for this step.

.EXAMPLE
  ./scripts/fetch-imagery.ps1
  ./scripts/fetch-imagery.ps1 -Force   # download again even if a file is already there
#>
param([switch]$Force)

$ErrorActionPreference = "Stop"
$dir = Join-Path $PSScriptRoot "..\src\UavOps.Simulator\imagery" | Resolve-Path
$manifest = Get-Content (Join-Path $dir "imagery.json") -Raw | ConvertFrom-Json

foreach ($image in $manifest.images) {
    $target = Join-Path $dir $image.file
    if ((Test-Path $target) -and -not $Force) {
        Write-Host "$($image.file): already there (use -Force to download again)."
        continue
    }
    Write-Host "Downloading $($image.title) ($($image.license), $($image.attribution)) ..."
    $partial = "$target.partial"
    Invoke-WebRequest -Uri $image.url -OutFile $partial
    Move-Item -Force $partial $target
    Write-Host ("  {0}: {1:N0} MB" -f $image.file, ((Get-Item $target).Length / 1MB))
}
Write-Host "Done. Restart UavOps.Simulator to use them."
