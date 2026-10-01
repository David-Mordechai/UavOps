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
./scripts/build-satellite.ps1                                        # once: satellite basemap + terrain (~300 MB download)
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
the white van". A search target can drive: place one from the page with a speed ("+ Place",
"Driving … km/h"), or give it `SpeedKmh` in `Scenario:Objects`. The page's time buttons speed the flight up (up to 30×), but not the vision model:
it analyses about one frame every ~2 s, so at higher speeds its reports trail the aircraft, and the
mission is reported complete only once every frame has been analysed.

## The live camera (3D, in the browser)

Clicking a UAV opens its camera as the operator watches it: a second MapLibre view
(`wwwroot/camera3d.js`) placed at the UAV's position and altitude (feet MSL) over 3D terrain, looking
where the gimbal points:
- straight down while flying a search route (`SURVEY`, the survey frames' view);
- at the point after `PointPayload` (`LOCK`);
- otherwise forward and 30° down, the gimbal's rest position (`FWD`, also after `ResetPayload`).

It uses the payload's zoom and field of view (`SetPayloadZoom`). Its layers:
- the satellite basemap, the drone photos, terrain relief and extruded OSM buildings;
- moving traffic and the scenario objects, at true size;
- green boxes on detections.

The HUD shows heading tape, altitude MSL/AGL, ground speed, zoom/HFOV, the look point and slant
range, and UTC. The picture has grain, a vignette, a little gimbal jitter and haze toward the
horizon. **IR** switches to a white-hot thermal look; **⤢** enlarges the view. It renders at
~30 fps, only while open.

The **Detector** tab shows what the onboard detector is given instead: the server's own render
(SkiaSharp MJPEG, straight down or north-up on a locked point, with its detection boxes). Both
views draw from the same world state (`World/GroundWorld.cs`), so a car is in the same place in
each. Without `build-satellite.ps1` the 3D view is flat and plain outside the drone photos.

## What moves (`World/`)

- **The clock** (`SimClock`) is simulated seconds, advancing with the time scale. Everything on the
  ground is a function of it, so any moment can be drawn again. A survey frame is rendered as at
  its capture. A close-up for the detector (`/zoom?seq=`) is taken as at the survey frame the
  candidate was seen in, plus `CloseUpDelaySeconds` (the payload's slew): a moving car is still
  there. That is the one simulation shortcut; on a real aircraft the onboard computer tracks it
  while slewing.
- **Roads** (`RoadNetwork`) are read once at startup from `israel.pmtiles` around each
  `Simulator:TrafficAreas` point (`TrafficAreaRadiusMeters`, default 6 km): about 80k road pieces
  in ~0.3 s.
- **Traffic** (`Traffic`), **off by default** (`TrafficDensity: 0`): the operator found the dots on
  every road distracting, and wanted only the vehicles that matter (the scenario objects, drawn from
  real vehicle photos). Set a density (0.15 was the old default) to bring it back: vehicles on the
  main roads and streets, as many per km as the road class and `TrafficDensity` give. Each drives a fixed 1-5 km route back and forth at its road's speed,
  keeping right, picked from a fixed seed so the traffic is the same every run. The mix never
  includes a white, silver or beige van. Traffic isn't drawn over the drone photos
  (`TrafficOverPhotos`, off): their own vehicles are the traffic there, and a drawn red car driving
  through a zone would be one more hit for a "red car" search. Parked cars stay in the cached
  ground chunks.
- **Driving targets**: a scenario object with `SpeedKmh` above 0 drives the road nearest where it
  was placed (tracks included), `DriveMeters` each way (default 800) back and forth, so it stays in
  its search area. With no road within 150 m, it stays parked.
- **The page** gets the vehicles near each UAV and in its map view with every state push (~5 Hz),
  and animates them (and the UAVs) smoothly between pushes, one push behind.

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
- **The zone and the base**: ZoneA is the whole Yatir drone photo (northern Negev, ~0.7 km² of
  open country). All three UAVs start at the base ("home") next to it. The demo scenario: "Send 997
  and 998 to search and track for a red car in ZoneA, when the car is found return the other uav
  home and keep tracking the red car" - a mission plan (see `CLAUDE.md`), then "start".
- **Real photos** (`imagery/`): `imagery.json` lists them - openly licensed drone orthomosaics from
  OpenAerialMap with their credits (Yatir road: Weizmann Institute Ecophysiology Group, CC-BY 4.0;
  Route 443: CC-BY 4.0; Glilot parking lot, a source of real vehicle photos only: Harel Dan,
  CC BY-SA 4.0), plus **every other openly licensed drone photo OpenAerialMap has over Israel**
  (27 more sites, ~7.5 km² at 2-4 cm, 1.25 GB): Mitzpe Ramon, Nehusha, Kfar Ruppin, Za'ura (Golan),
  Rehovot (Weizmann), Nahariya, Yad Hashmona, Herzliya, a Tel Aviv park and more. Left out:
  non-commercial licences (Arava/Hatzeva, a Jerusalem street, a Maxar strip), duplicate uploads,
  and one in Web Mercator. The reader takes classic TIFF and BigTIFF; a photo it can't read is
  skipped with a warning. `scripts/fetch-imagery.ps1` downloads the GeoTIFFs once (git-ignored). The camera
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

The base map is fully offline and covers all of Israel, Metula to Eilat (default bounds
`34.20,29.45,35.95,33.35`, the same in both build scripts). `scripts/build-offline-map.ps1` builds
`wwwroot/map/israel.pmtiles` once from OpenStreetMap (run it with PowerShell 7, `pwsh`: Windows
PowerShell 5 stops at the Docker check when Docker isn't running) (internet needed only for that step; the file is
git-ignored). Without it the page draws a plain lat/lng grid. The style, fonts (Noto Sans, including
the Hebrew and Arabic ranges) and MapLibre/pmtiles/RTL-text scripts are committed under `wwwroot/`.

**Satellite and terrain.** `scripts/build-satellite.ps1` builds two more offline files, both
git-ignored, using `tools/UavOps.MapBuilder` (only the source tiles inside the bounds are
downloaded, by HTTP range requests, and cached in `.tools/satellite-cache/`, ~1.7 GB for all of
Israel; run it with `pwsh`):
- `wwwroot/map/basemap.mbtiles`: Sentinel-2 L2A true colour at 10 m, the least cloudy summer scene
  (one date for all 18 tiles covering the country, so no colour seams), zoom 8-14, ~300 MB.
  Tiles on the edge of the area are transparent PNG outside it; the rest JPEG. "Contains modified
  Copernicus Sentinel data".
- `wwwroot/map/terrain.mbtiles`: Copernicus DEM GLO-30, Terrarium-encoded, zoom 8-12, ~150 MB. A square with
  no Copernicus tile is all sea, sea level.

The page opens on the satellite view (Sentinel, then the drone photos, with OSM roads and names faint on top); **view: satellite** toggles back to the dark map. Tile URLs carry the file's build time (`?v=`), so
a rebuilt map is never mixed with tiles the browser kept from the last one. Sentinel's 10 m pixels look like
satellite imagery from any map zoom. The live camera, though, is only sharp over the drone photos:
at search zoom a Sentinel pixel is a tenth of the frame.

## Configuration (`appsettings.json`)

- `Simulator`: `HostHubUrl`, `AoiDatabasePath` (blank = the same default file McpMoav uses, so the
  zones match), `TimeScale`, tick/push intervals, waypoint arrival radius, orbit radius; the camera
  (`PayloadWideHorizontalFovDeg`, `PayloadMaxZoom`, `CameraWidth`/`CameraHeight`, `SurveyFrameOverlap`, `SurveyFramesKept`,
  `LiveCameraFps`, `ZoomPixels`, `MaxZoomRangeMeters`); the detector (`Detector`: `Onboard` or
  `Simulated`, `OnboardUrl` - the onboard computer, the Jetson at `http://192.168.1.154:5280` by
  default; `PublicBaseUrl` - this simulator's address as the onboard computer reaches it, blank =
  this machine's LAN address on the route there; `OnboardVideoFps`/`VideoFramesKept` - the live video
  the onboard loop reads). The app listens on all interfaces (`Urls: http://0.0.0.0:5270`).
- For find and track, the onboard computer drives the payload (`POST /api/uavs/{tail}/payload/point|zoom|release`),
  reads the live video (`/api/uavs/{tail}/video/next`, `/video/zoom`) and reports the target
  (`POST /api/onboard/tracks`); the UAV then circles the reported position (`Following`), and the
  map shows the target as a ring labelled with its track id.
- `Scenario:Objects`: what's on the ground at startup - on the Yatir road in ZoneA, a white van in a
  lay-by with a white car and a black car along the road, and a red car at the bend. Each has a
  `Kind` (car, van, pickup, truck, bus), `Color`, `HeadingDeg` (defaulting from the label, "red car")
  and optionally `Photo` (default: the first vehicle photo of its kind and colour). Objects can also
  be placed from the page.
- `Scenario:Objects[].SpeedKmh` / `DriveMeters`: a target that drives (see "What moves").
- `Simulator:TrafficDensity`, `TrafficAreas`, `TrafficAreaRadiusMeters`, `TrafficOverPhotos` (moving
  traffic), `CloseUpDelaySeconds`, and `Simulator:VehiclePhotos` (the real vehicle cut-outs: image,
  centre pixel, size, heading, kind, colour).

## HTTP API

`GET /api/state`, `GET /api/zones` (GeoJSON), `GET|POST /api/objects`, `DELETE /api/objects/{id}`,
`POST /api/timescale` (`{ "value": 10 }`), `GET /api/map` (what's built, with attributions).
Live state is pushed on `/simHub` (`state` event: UAVs with their payload mode, objects where
they are now, nearby traffic as `[id, lat, lng, heading, sprite]` rows). The page reports its
map view with `SetView` on the hub, so it gets that area's traffic. `GET /api/basemap/{z}/{x}/{y}.jpg`
and `/api/terrain/{z}/{x}/{y}.png` serve the satellite and terrain tiles. `GET /api/sprites/{kind}-{colour}.png`
is a traffic vehicle's top-down sprite.

The camera: `GET /api/uavs/{tail}/camera.mjpg` (live MJPEG, ~5 fps, rendered only while watched),
`GET /api/uavs/{tail}/frames/next?after=N` and `/frames/{seq}.jpg` (survey frames, telemetry in
`X-Frame-*` headers), `GET /api/uavs/{tail}/zoom?lat=&lng=&widthMeters=&pixels=[&seq=]`, the photos as
map tiles (`GET /api/imagery/{z}/{x}/{y}.png`, `GET /api/imagery` for their extents and credits), and
`POST /api/onboard/detections` (the detector's callback). See `src/UavOps.Onboard.Detector/README.md`.
