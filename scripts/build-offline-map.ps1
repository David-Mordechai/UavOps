<#
.SYNOPSIS
  Builds UavOps.Simulator's offline base map (src/UavOps.Simulator/wwwroot/map/israel.pmtiles)
  from OpenStreetMap. Run once; internet is needed only for this step.

.DESCRIPTION
  Runs Planetiler (https://github.com/onthegomap/planetiler) on Geofabrik's Israel & Palestine
  extract, clipped to the area around the simulated fleet, producing one .pmtiles vector-tile
  archive (OpenMapTiles schema) that the simulator page reads directly with HTTP range requests -
  no tile server. OSM's own tile servers forbid bulk/offline download, which is why the map is
  built from an extract instead.

  Needs one of, tried in this order:
    1. Docker, with the engine running (uses ghcr.io/onthegomap/planetiler).
    2. Java 21+ on PATH (downloads planetiler.jar).
    3. Neither: downloads a portable Temurin JDK 21 into .tools/ (git-ignored) and uses that.

  The first run also downloads the source data into .tools/planetiler-data/: the OSM extract
  (~115 MB) plus coastline/water polygons and Natural Earth (several hundred MB). Later runs reuse it.

.PARAMETER Bounds
  minLng,minLat,maxLng,maxLat to keep. Default covers the simulator's area with room to spare: the
  coast from Ashdod to Tel Aviv, the Yatir forest (ZoneA, the UAV base) and Route 443 (ZoneB).

.PARAMETER Force
  Rebuild even if the .pmtiles file already exists.
#>
[CmdletBinding()]
param(
    [string]$Bounds = "34.40,31.25,35.20,32.15",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"   # Invoke-WebRequest is very slow with the progress bar on

$repoRoot  = Split-Path -Parent $PSScriptRoot
$outputDir = Join-Path $repoRoot "src/UavOps.Simulator/wwwroot/map"
$output    = Join-Path $outputDir "israel.pmtiles"
$tools     = Join-Path $repoRoot ".tools"
$dataDir   = Join-Path $tools "planetiler-data"
New-Item -ItemType Directory -Force $outputDir, $dataDir | Out-Null

if ((Test-Path $output) -and -not $Force) {
    Write-Host "Already built: $output (use -Force to rebuild)."
    exit 0
}

$planetilerArgs = @(
    "--area=israel-and-palestine",
    "--bounds=$Bounds",
    "--download",
    "--force"
)

function Test-DockerEngine {
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { return $false }
    docker info *> $null
    return $LASTEXITCODE -eq 0
}

function Get-JavaMajorVersion([string]$java) {
    $versionLine = (& $java -version 2>&1 | Select-Object -First 1) -as [string]
    if ($versionLine -match '"(\d+)') { return [int]$Matches[1] }
    return 0
}

function Get-Java {
    $onPath = Get-Command java -ErrorAction SilentlyContinue
    if ($onPath -and (Get-JavaMajorVersion $onPath.Source) -ge 21) { return $onPath.Source }

    $portable = Get-ChildItem (Join-Path $tools "jdk-21*") -Directory -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $portable) {
        Write-Host "No Docker engine or Java 21 found; downloading a portable Temurin JDK 21 into .tools/ ..."
        $zip = Join-Path $tools "jdk21.zip"
        Invoke-WebRequest "https://api.adoptium.net/v3/binary/latest/21/ga/windows/x64/jdk/hotspot/normal/eclipse" -OutFile $zip
        Expand-Archive $zip -DestinationPath $tools -Force
        Remove-Item $zip
        $portable = Get-ChildItem (Join-Path $tools "jdk-21*") -Directory | Select-Object -First 1
    }
    return Join-Path $portable.FullName "bin/java.exe"
}

$started = Get-Date
if (Test-DockerEngine) {
    Write-Host "Building with Docker (ghcr.io/onthegomap/planetiler) ..."
    docker run --rm `
        -v "${dataDir}:/data" `
        ghcr.io/onthegomap/planetiler:latest `
        @planetilerArgs "--output=/data/israel.pmtiles"
    if ($LASTEXITCODE -ne 0) { throw "Planetiler (Docker) failed with exit code $LASTEXITCODE." }
}
else {
    $java = Get-Java
    $jar = Join-Path $tools "planetiler.jar"
    if (-not (Test-Path $jar)) {
        Write-Host "Downloading planetiler.jar ..."
        Invoke-WebRequest "https://github.com/onthegomap/planetiler/releases/latest/download/planetiler.jar" -OutFile $jar
    }
    Write-Host "Building with $java ..."
    Push-Location $dataDir
    try {
        & $java -Xmx4g -jar $jar @planetilerArgs "--output=$dataDir/israel.pmtiles"
        if ($LASTEXITCODE -ne 0) { throw "Planetiler failed with exit code $LASTEXITCODE." }
    }
    finally {
        Pop-Location
    }
}

Move-Item -Force (Join-Path $dataDir "israel.pmtiles") $output
$size = "{0:N1} MB" -f ((Get-Item $output).Length / 1MB)
Write-Host "Built $output ($size) in $([int]((Get-Date) - $started).TotalMinutes) min. Reload the simulator page to use it."
