# UavOps.Simulator

A dev-only stand-in for the real fleet app, with a map. It connects to `UavOps.Agent`'s fleet hub
(`/uavCommandHub`) through `UavOps.FleetClient`, exactly where the real fleet app connects, and:

- flies UAVs 997/998/999 for real: `Navigate` flies there (no teleport) and then circles, speed and
  altitude change gradually, `ReturnToLaunch` flies home and lands, and an uploaded route is flown
  waypoint by waypoint once `StartMission` arrives;
- plays the onboard detector (`IOnboardDetector`, the seam the real Jetson service replaces): while
  a search target is set, it reports any scenario object inside the camera footprint whose tags
  contain every word of the prompt, once per object per mission;
- reports detections and mission ends back to the host, which tells the operator in chat;
- shows it all at http://localhost:5270: AOI zones, planned routes, UAVs with heading and trail,
  camera footprints while searching, ground objects and detections.

## Run

```bash
dotnet run --project src/UavOps.Agent --urls http://localhost:5262   # the host
dotnet run --project src/UavOps.Simulator                            # this, on :5270
```

Set `OperationBackend: SignalR` in `src/UavOps.Agent.McpMoav/appsettings.json` so fleet commands go
to the connected fleet app (this simulator) instead of McpMoav's in-memory backend. The simulator
waits for the host and reconnects on its own, so start order doesn't matter. Don't run it alongside
`UavOps.MockFleetClient`: the host talks to one fleet connection at a time (the newest).

Demo: in the chat, "Enter AOI zone ZoneA and search for white van", answer which UAV, then "start
the mission" and approve. Speed things up with the page's time buttons (up to 30×). When the van is
found, try "send 998 to the white van".

## Map

The base map is fully offline. `scripts/build-offline-map.ps1` builds
`wwwroot/map/israel.pmtiles` once from OpenStreetMap (internet needed only for that step; the file is
git-ignored). Without it the page draws a plain lat/lng grid. The style, fonts (Noto Sans, including
the Hebrew and Arabic ranges) and MapLibre/pmtiles/RTL-text scripts are committed under `wwwroot/`.

## Configuration (`appsettings.json`)

- `Simulator`: `HostHubUrl`, `AoiDatabasePath` (blank = the same default file McpMoav uses, so the
  zones match), `TimeScale`, tick/push intervals, waypoint arrival radius, orbit radius, camera FOV.
- `Scenario:Objects`: what's on the ground at startup (a white van inside ZoneA, plus a red car and
  a white truck that a "white van" search must not match). Objects can also be placed from the page.

## HTTP API

`GET /api/state`, `GET /api/zones` (GeoJSON), `GET|POST /api/objects`, `DELETE /api/objects/{id}`,
`POST /api/timescale` (`{ "value": 10 }`), `GET /api/map`. Live state is pushed on `/simHub`
(`state` event).
