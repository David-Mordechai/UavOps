# UavOps.Simulator

A dev-only stand-in for the real fleet app, with a map. It connects to `UavOps.Agent`'s fleet hub
(`/uavCommandHub`) through `UavOps.FleetClient`, exactly where the real fleet app connects, and:

- flies UAVs 997/998/999 for real: `Navigate` flies there (no teleport) and then circles, speed and
  altitude change gradually, `ReturnToLaunch` flies home and lands, and an uploaded route is flown
  waypoint by waypoint once `StartMission` arrives;
- plays the payload camera: renders what each UAV sees - over the search zones, **real aerial photos**
  of the ground (and real vehicle photos as the placed targets); elsewhere the ground drawn from the
  offline OSM map - as a live video on the page, as survey frames for the onboard detector, and as
  zoom close-ups;
- hands the camera to the onboard detector (`UavOps.Onboard.Detector`, the Jetson service, which
  searches it with a vision-language model), or plays the detector itself without a model
  (`Simulator:Detector = Simulated`: tag match inside the camera frame);
- reports detections and mission ends back to the host, which tells the operator in chat;
- shows it all at http://localhost:5270: AOI zones, planned routes, UAVs with heading and trail,
  camera footprints while searching, ground objects and detections - and, for the UAV you click, its
  live camera with the detector's boxes and the frame of its last match.

## Run

```bash
./scripts/fetch-imagery.ps1                                          # once: the real aerial photos (~160 MB)
dotnet run --project src/UavOps.Agent --urls http://localhost:5262   # the host
dotnet run --project src/UavOps.Onboard.Detector                     # the onboard detector, :5280
dotnet run --project src/UavOps.Simulator                            # this, on :5270
```

McpMoav's `OperationBackend` must be `SignalR` so fleet commands go to the connected fleet app (this
simulator) instead of McpMoav's in-memory backend. The checked-in default is `Simulated`, so set it
once on the chat UI's **Settings** page (moav → `OperationBackend`, then restart the agent): it's
saved to `appsettings.Local.json` next to McpMoav's build output, which survives rebuilds and is
git-ignored. An `OperationBackend=SignalR` environment variable on the agent works too. The simulator
waits for the host and reconnects on its own, so start order doesn't matter. Don't run it alongside
`UavOps.MockFleetClient`: the host talks to one fleet connection at a time (the newest).

Demo: in the chat, "Enter AOI zone ZoneA and search for white van", answer which UAV, then "start
the mission". Click the UAV on the map to watch its camera. When the van is found, try "send 998 to
the white van". The page's time buttons speed the flight up (up to 30×), but not the vision model:
it analyses about one frame every ~2 s, so at higher speeds its reports trail the aircraft, and the
mission is reported complete only once every frame has been analysed.

## The camera and the search

- **Survey frames**: while a search mission is flying its route at search altitude, the camera takes
  a 1280×960 frame every 70% of a frame height flown (30% overlap), by distance flown - so the time
  scale changes how fast frames come, never how much ground they cover. Before the route (on the way
  there, and circling the first waypoint while descending to search altitude) it takes none.
- **Payload zoom, only by command**: the payload is 40° wide and zooms up to 30x, changed only by
  `SetPayloadZoom` (the fleet command, from the tool of the same name). `PrepareAoiSearch` sends
  the search zoom (~100 m of ground across, ~0.08 m per pixel, a van ~64×24 px) and plans lanes from
  the field of view this simulator reports in telemetry. The altitude only changes by command too: a
  search route is planned at the UAV's current altitude.
- **Pointing**: `PointPayload` keeps that point in the centre of the live view (north-up, so it
  doesn't spin while the UAV circles), at the current zoom; `ResetPayload` looks straight down again.
  A UAV sent somewhere circles around that point. Survey frames always look straight down.
- **Zoom close-ups** (`/api/uavs/{tail}/zoom`): the onboard detector points the payload at each
  candidate for a sharp close-up; within `MaxZoomRangeMeters` of the UAV.
- **The zones and the base**: ZoneA follows the Yatir forest road (northern Negev), ZoneB covers
  Route 443 near Modi'in - open country, each where a real drone photo exists. The UAVs' base
  ("home") is by ZoneA; 999 starts at a forward point by ZoneB (~75 km away).
- **Real photos** (`imagery/`): `imagery.json` lists them - openly licensed drone orthomosaics from
  OpenAerialMap with their credits (Yatir road: Weizmann Institute Ecophysiology Group, CC-BY 4.0;
  Route 443: CC-BY 4.0; Glilot parking lot, a source of real vehicle photos only: Harel Dan,
  CC BY-SA 4.0). `scripts/fetch-imagery.ps1` downloads the GeoTIFFs once (git-ignored). The camera
  draws them at the right resolution level; the map page shows them too. Route 443's photo keeps
  the real traffic of its moment, so a search there finds real vehicles.
- **Placed targets** look like real vehicles: `Simulator:VehiclePhotos` cuts a real white van (from
  the Route 443 photo), a red sedan, a white hatchback and a black SUV (from the parking lot) out of
  the photos, and a scenario object of that kind and colour is drawn as that photo at true scale.
  Others fall back to drawn vehicles.
- **Elsewhere**, the ground is drawn from the pmtiles map in cached chunks (`Camera/GroundRenderer.cs`)
  with sparse traffic (`TrafficDensity`) that never includes a white, silver or beige van. Without
  the pmtiles file the camera shows plain terrain; without the photos, the zones are drawn too.

## Map

The base map is fully offline. `scripts/build-offline-map.ps1` builds
`wwwroot/map/israel.pmtiles` once from OpenStreetMap (internet needed only for that step; the file is
git-ignored). Without it the page draws a plain lat/lng grid. The style, fonts (Noto Sans, including
the Hebrew and Arabic ranges) and MapLibre/pmtiles/RTL-text scripts are committed under `wwwroot/`.

## Configuration (`appsettings.json`)

- `Simulator`: `HostHubUrl`, `AoiDatabasePath` (blank = the same default file McpMoav uses, so the
  zones match), `TimeScale`, tick/push intervals, waypoint arrival radius, orbit radius; the camera
  (`PayloadWideHorizontalFovDeg`, `PayloadMaxZoom`, `CameraWidth`/`CameraHeight`, `SurveyFrameOverlap`, `SurveyFramesKept`,
  `LiveCameraFps`, `ZoomPixels`, `MaxZoomRangeMeters`); the detector (`Detector`: `Onboard` or
  `Simulated`, `OnboardDetectorUrl`, `PublicBaseUrl` - this simulator's address as the detector
  reaches it).
- `Scenario:Objects`: what's on the ground at startup - on the Yatir road in ZoneA, a white van in a
  lay-by with a white car and a black car along the road, and a red car at the bend. Each has a
  `Kind` (car, van, pickup, truck, bus), `Color`, `HeadingDeg` (defaulting from the label, "red car")
  and optionally `Photo` (default: the first vehicle photo of its kind and colour). Objects can also
  be placed from the page.
- `Simulator:TrafficDensity` (drawn traffic outside the photos) and `Simulator:VehiclePhotos` (the
  real vehicle cut-outs: image, centre pixel, size, heading, kind, colour).

## HTTP API

`GET /api/state`, `GET /api/zones` (GeoJSON), `GET|POST /api/objects`, `DELETE /api/objects/{id}`,
`POST /api/timescale` (`{ "value": 10 }`), `GET /api/map`. Live state is pushed on `/simHub`
(`state` event).

The camera: `GET /api/uavs/{tail}/camera.mjpg` (live MJPEG, ~5 fps, rendered only while watched),
`GET /api/uavs/{tail}/frames/next?after=N` and `/frames/{seq}.jpg` (survey frames, telemetry in
`X-Frame-*` headers), `GET /api/uavs/{tail}/zoom?lat=&lng=&widthMeters=&pixels=`, the photos as
map tiles (`GET /api/imagery/{z}/{x}/{y}.png`, `GET /api/imagery` for their extents and credits), and
`POST /api/onboard/detections` (the detector's callback). See `src/UavOps.Onboard.Detector/README.md`.
