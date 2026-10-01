<#
.SYNOPSIS
  Builds UavOps.Simulator's offline satellite basemap and terrain
  (src/UavOps.Simulator/wwwroot/map/basemap.mbtiles and terrain.mbtiles). Run once; internet is
  needed only for this step.

.DESCRIPTION
  Runs tools/UavOps.MapBuilder over the simulator's area:
    - basemap.mbtiles: Sentinel-2 L2A true colour (10 m per pixel), the least cloudy summer scene
      per tile from the open Earth Search catalogue (AWS), as JPEG web tiles, zoom 8-14.
      "Contains modified Copernicus Sentinel data" - free and open.
    - terrain.mbtiles: Copernicus DEM GLO-30 (30 m), Terrarium-encoded PNG tiles, zoom 8-12, for
      the 3D camera view's relief. Free with attribution (see the file's metadata).
  Only the parts of the source files inside the bounds are downloaded (HTTP range requests), and
  they're cached in .tools/satellite-cache/ (git-ignored), so a rerun downloads nothing.
  Expect a few hundred MB of downloads the first time; the output is ~100 MB, git-ignored.

.PARAMETER Bounds
  minLng,minLat,maxLng,maxLat - the same area as the offline OSM map (build-offline-map.ps1).
.PARAMETER Only
  basemap, terrain, or all (default).
#>
[CmdletBinding()]
param(
    [string]$Bounds = "34.20,29.45,35.95,33.35",
    [ValidateSet("all", "basemap", "terrain")][string]$Only = "all"
)
$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$out = Join-Path $repoRoot "src\UavOps.Simulator\wwwroot\map"
$cache = Join-Path $repoRoot ".tools\satellite-cache"

dotnet run --project (Join-Path $repoRoot "tools\UavOps.MapBuilder") -c Release -- `
    --bounds $Bounds --out $out --cache $cache --only $Only
if ($LASTEXITCODE -ne 0) { throw "UavOps.MapBuilder failed ($LASTEXITCODE)." }
Write-Host "Done. Restart UavOps.Simulator to use them."
