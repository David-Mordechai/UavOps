// UavOps Simulator page: the offline map plus a live view of the simulated fleet.
// State arrives from /simHub ("state", ~5 Hz); everything on the map is redrawn from it.
(async function () {
  "use strict";

  const UAV_COLORS = { "997": "#5b8def", "998": "#e0a940", "999": "#4bb768" };
  const colorOf = (tail) => UAV_COLORS[tail] || "#c678dd";
  const ZONE_COLOR = "#ff3df2";

  // Same coordinates as UavOps.Agent.Contracts' KnownPoints - the names the operator can use.
  const KNOWN_POINTS = [
    { name: "home", lat: 31.344, lng: 35.035 },
    { name: "alpha", lat: 31.3465, lng: 35.0503 },
    { name: "bravo", lat: 32.0676, lng: 34.919 },
  ];

  const $ = (id) => document.getElementById(id);
  const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  const fmt = (n, d = 5) => Number(n).toFixed(d);

  // ---- Base map: the offline OSM tiles if built, otherwise a plain lat/lng grid ----

  const mapInfo = await fetch("/api/map").then((r) => r.json()).catch(() => ({ offlineTiles: false }));
  let style;
  if (mapInfo.offlineTiles) {
    const protocol = new pmtiles.Protocol();
    maplibregl.addProtocol("pmtiles", protocol.tile);
    // Hebrew and Arabic labels render reversed/unjoined without it.
    maplibregl.setRTLTextPlugin(location.origin + "/vendor/mapbox-gl-rtl-text.min.js", true);
    style = await fetch("map/style.json").then((r) => r.json());
    // MapLibre's workers need absolute URLs.
    style.glyphs = location.origin + "/map/glyphs/{fontstack}/{range}.pbf";
    style.sources.openmaptiles.url = "pmtiles://" + location.origin + "/map/israel.pmtiles";
    setPill("mapStatus", "map: offline OSM", "ok");
  } else {
    style = gridStyle();
    setPill("mapStatus", "map: grid (no tiles built)", "wait");
    $("mapStatus").title = "Run scripts/build-offline-map.ps1 once to build the offline map.";
  }

  const map = new maplibregl.Map({
    container: "map",
    style,
    center: [35.046, 31.3465],
    zoom: 14.2,
    dragRotate: false,
    attributionControl: { compact: true },
  });
  map.touchZoomRotate.disableRotation();
  map.addControl(new maplibregl.NavigationControl({ showCompass: false }), "top-left");
  map.addControl(new maplibregl.ScaleControl({ unit: "metric" }), "bottom-left");

  function gridStyle() {
    const features = [];
    const step = 0.005; // ~500 m
    for (let lng = 34.4; lng <= 34.95 + 1e-9; lng += step)
      features.push(line([[lng, 31.6], [lng, 32.05]], Math.round(lng * 1000) % 50 === 0));
    for (let lat = 31.6; lat <= 32.05 + 1e-9; lat += step)
      features.push(line([[34.4, lat], [34.95, lat]], Math.round(lat * 1000) % 50 === 0));
    return {
      version: 8,
      sources: { grid: { type: "geojson", data: { type: "FeatureCollection", features } } },
      layers: [
        { id: "background", type: "background", paint: { "background-color": "#15171a" } },
        { id: "grid-minor", type: "line", source: "grid", filter: ["!", ["get", "major"]], paint: { "line-color": "#202227", "line-width": 1 } },
        { id: "grid-major", type: "line", source: "grid", filter: ["get", "major"], paint: { "line-color": "#2c2f35", "line-width": 1 } },
      ],
    };
    function line(coordinates, major) {
      return { type: "Feature", properties: { major }, geometry: { type: "LineString", coordinates } };
    }
  }

  await new Promise((resolve) => map.on("load", resolve));
  // The credits start folded behind the (i) button, however wide the window.
  const attribution = document.querySelector(".maplibregl-ctrl-attrib");
  attribution?.classList.remove("maplibregl-compact-show");
  attribution?.removeAttribute("open");
  window.simMap = map; // handy from the browser console

  // ---- Satellite (offline Sentinel-2, where built: scripts/build-satellite.ps1) ----
  // Under the OSM layers; in satellite mode the map's own fills are hidden and its roads and
  // labels stay, faint, on top - a hybrid view. The toggle switches back to the dark map.

  const OSM_FILLS = ["landcover-wood", "landcover-grass", "landcover-sand", "landuse-residential", "landuse-industrial", "park", "water", "building"];
  const OSM_ROADS = ["road-minor", "road-secondary", "road-primary", "road-motorway", "rail", "waterway", "aeroway-runway"];
  if (mapInfo.basemap) {
    map.addSource("basemap", {
      type: "raster",
      tiles: [location.origin + "/api/basemap/{z}/{x}/{y}.jpg?v=" + mapInfo.basemap.version],
      tileSize: 256,
      minzoom: mapInfo.basemap.minzoom,
      maxzoom: mapInfo.basemap.maxzoom,
      attribution: esc(mapInfo.basemap.attribution),
    });
    const firstOsm = map.getStyle().layers.find((l) => l.id !== "background")?.id;
    map.addLayer({ id: "basemap", type: "raster", source: "basemap", paint: { "raster-saturation": -0.1, "raster-contrast": 0.08 } }, firstOsm);
    const toggle = document.createElement("button");
    toggle.type = "button";
    toggle.className = "pill pill-toggle";
    $("mapStatus").after(toggle);
    let satellite = true;
    try { satellite = localStorage.getItem("sim.satellite") !== "0"; } catch { /* private window */ }
    const apply = () => {
      map.setLayoutProperty("basemap", "visibility", satellite ? "visible" : "none");
      for (const id of OSM_FILLS) if (map.getLayer(id)) map.setLayoutProperty(id, "visibility", satellite ? "none" : "visible");
      for (const id of OSM_ROADS) if (map.getLayer(id)) map.setPaintProperty(id, "line-opacity", satellite ? 0.35 : 1);
      toggle.textContent = satellite ? "view: satellite" : "view: map";
      toggle.title = satellite ? "Sentinel-2 (10 m) with OSM roads and names. Click for the dark map." : "Click for the satellite view.";
      try { localStorage.setItem("sim.satellite", satellite ? "1" : "0"); } catch { /* private window */ }
    };
    toggle.addEventListener("click", () => { satellite = !satellite; apply(); });
    apply();
  }

  // ---- Real aerial photos (where downloaded: scripts/fetch-imagery.ps1) over the base map ----

  const photos = await fetch("/api/imagery").then((r) => r.json()).catch(() => []);
  if (photos.length) {
    map.addSource("imagery", {
      type: "raster",
      tiles: [location.origin + "/api/imagery/{z}/{x}/{y}.png"],
      tileSize: 256,
      minzoom: 12,
      maxzoom: 21,
      // One line: 30 photos' credits filled half the map. Each one, with its licence, is on the credits page.
      attribution: `Drone photos: OpenAerialMap contributors (${photos.length}, CC-BY / CC BY-SA) - <a href="credits.html" target="_blank">credits</a>`,
    });
    map.addLayer({ id: "imagery", type: "raster", source: "imagery" });
  }

  // ---- Overlay layers ----

  const empty = { type: "FeatureCollection", features: [] };

  // Moving traffic: dots when zoomed out, then the vehicle itself at true size and heading.
  // Sprites (drawn at 50 px per metre, facing up) load on first use.
  map.addSource("traffic", { type: "geojson", data: empty });
  const SPRITE_PX_PER_METER = 50;
  const metersPerScreenPx = (z) => (40075016.686 * Math.cos((31.6 * Math.PI) / 180)) / (512 * Math.pow(2, z));
  const iconSize = (z) => 1 / metersPerScreenPx(z) / SPRITE_PX_PER_METER;
  map.addLayer({
    id: "traffic-dots", type: "circle", source: "traffic", minzoom: 12.5, maxzoom: 16,
    paint: { "circle-radius": ["interpolate", ["linear"], ["zoom"], 12.5, 1.2, 16, 2.6], "circle-color": ["get", "color"], "circle-stroke-color": "#000", "circle-stroke-width": 0.5 },
  });
  map.addLayer({
    id: "traffic", type: "symbol", source: "traffic", minzoom: 16,
    layout: {
      "icon-image": ["get", "sprite"],
      "icon-rotate": ["get", "heading"],
      "icon-rotation-alignment": "map",
      "icon-pitch-alignment": "map",
      "icon-allow-overlap": true,
      "icon-ignore-placement": true,
      "icon-size": ["interpolate", ["exponential", 2], ["zoom"], 16, iconSize(16), 22, iconSize(22)],
    },
  });
  map.on("styleimagemissing", (e) => loadSprite(map, e.id));
  for (const id of ["zones", "footprints", "sightlines", "lookcenters", "routes", "waypoints", "trails", "destinations", "targets"])
    map.addSource(id, { type: "geojson", data: empty });

  // Zones in magenta, no UAV's colour, with a dark casing so the border reads on the aerial photos.
  map.addLayer({ id: "zones-fill", type: "fill", source: "zones", paint: { "fill-color": ZONE_COLOR, "fill-opacity": 0.08 } });
  map.addLayer({ id: "zones-casing", type: "line", source: "zones", layout: { "line-join": "round" }, paint: { "line-color": "#000", "line-width": 5, "line-opacity": 0.6 } });
  map.addLayer({ id: "zones-line", type: "line", source: "zones", layout: { "line-join": "round" }, paint: { "line-color": ZONE_COLOR, "line-width": 2.5 } });
  // Where every airborne UAV's payload looks: its ground rectangle, a dashed line of sight from
  // the UAV (shows an oblique look - tracking, PointPayload), and a ring with the tail number at
  // the look centre that stays visible when the rectangle is a few pixels at a low zoom.
  map.addLayer({ id: "footprints", type: "fill", source: "footprints", paint: { "fill-color": ["get", "color"], "fill-opacity": ["case", ["get", "looking"], 0.22, 0.12] } });
  map.addLayer({ id: "footprints-line", type: "line", source: "footprints", paint: { "line-color": ["get", "color"], "line-width": 2, "line-opacity": 0.9 } });
  map.addLayer({ id: "sightlines", type: "line", source: "sightlines", paint: { "line-color": ["get", "color"], "line-width": 1.2, "line-dasharray": [2, 2], "line-opacity": 0.75 } });
  map.addLayer({
    id: "lookcenters", type: "circle", source: "lookcenters",
    paint: { "circle-radius": 4, "circle-color": "rgba(0,0,0,0)", "circle-stroke-color": ["get", "color"], "circle-stroke-width": 2 },
  });
  map.addLayer({
    id: "lookcenters-label", type: "symbol", source: "lookcenters",
    layout: { "text-field": ["get", "label"], "text-size": 10, "text-offset": [0, 0.9], "text-anchor": "top", "text-font": ["Noto Sans Regular"], "text-allow-overlap": true },
    paint: { "text-color": ["get", "color"], "text-halo-color": "#000", "text-halo-width": 1.5 },
  });
  // The route in its UAV's colour: a solid line with a dark casing and a dot on every waypoint;
  // what's already flown is dimmed.
  map.addLayer({
    id: "routes-casing", type: "line", source: "routes",
    layout: { "line-join": "round", "line-cap": "round" },
    paint: { "line-color": "#000", "line-width": 5, "line-opacity": ["case", ["get", "remaining"], 0.6, 0.3] },
  });
  map.addLayer({
    id: "routes", type: "line", source: "routes",
    layout: { "line-join": "round", "line-cap": "round" },
    paint: { "line-color": ["get", "color"], "line-width": 3, "line-opacity": ["case", ["get", "remaining"], 1, 0.4] },
  });
  map.addLayer({
    id: "waypoints", type: "circle", source: "waypoints",
    paint: {
      "circle-radius": 4.5,
      "circle-color": ["get", "color"],
      "circle-stroke-color": "#fff",
      "circle-stroke-width": 1.5,
      "circle-opacity": ["case", ["get", "flown"], 0.4, 1],
      "circle-stroke-opacity": ["case", ["get", "flown"], 0.4, 1],
    },
  });
  map.addLayer({ id: "trails", type: "line", source: "trails", layout: { "line-join": "round", "line-cap": "round" }, paint: { "line-color": ["get", "color"], "line-width": 2, "line-opacity": 0.55 } });
  map.addLayer({ id: "destinations", type: "line", source: "destinations", paint: { "line-color": ["get", "color"], "line-width": 1.5, "line-dasharray": [1, 2], "line-opacity": 0.8 } });
  // A target the onboard computer tracks (find and track): a ring in its UAV's colour where it was
  // last reported - solid while tracked, dashed-looking (hollow, faint) while coasting or lost.
  map.addLayer({
    id: "targets", type: "circle", source: "targets",
    paint: {
      "circle-radius": 11,
      "circle-color": "rgba(0,0,0,0)",
      "circle-stroke-color": ["get", "color"],
      "circle-stroke-width": 3,
      "circle-stroke-opacity": ["case", ["==", ["get", "state"], "Tracking"], 1, 0.45],
    },
  });
  map.addLayer({
    id: "targets-label", type: "symbol", source: "targets",
    layout: { "text-field": ["get", "label"], "text-size": 11, "text-offset": [0, 1.6], "text-anchor": "top", "text-font": ["Noto Sans Regular"] },
    paint: { "text-color": "#fff", "text-halo-color": "#000", "text-halo-width": 1.5 },
  });

  for (const p of KNOWN_POINTS) {
    const el = document.createElement("div");
    el.className = "zone-label";
    el.style.color = "#9a9ba1";
    el.textContent = "◆ " + p.name;
    new maplibregl.Marker({ element: el, anchor: "left" }).setLngLat([p.lng, p.lat]).addTo(map);
  }

  const zones = await fetch("/api/zones").then((r) => r.json()).catch(() => empty);
  map.getSource("zones").setData(zones);
  for (const f of zones.features) {
    const ring = f.geometry.coordinates[0].slice(0, -1);
    const el = document.createElement("div");
    el.className = "zone-label";
    el.textContent = f.properties.name;
    // Just above the zone's northernmost point, which is always on its outline - a bounding-box
    // corner can be far off a diagonal zone like the Yatir road strip.
    const top = ring.reduce((best, c) => (c[1] > best[1] ? c : best), ring[0]);
    new maplibregl.Marker({ element: el, anchor: "bottom", offset: [0, -4] }).setLngLat(top).addTo(map);
  }

  // ---- Smooth motion ----
  // State arrives ~5 times a second; UAVs, traffic and driving objects glide between updates
  // instead of jumping: each keeps where it was and where it's going, drawn one update behind.

  const smooth = new Map(); // key → { from: [lng, lat, hdg], to: [...], t0 }
  let pushMs = 200, lastPushAt = 0;
  function aim(key, lng, lat, hdg) {
    const now = performance.now();
    const s = smooth.get(key);
    if (!s) { smooth.set(key, { from: [lng, lat, hdg], to: [lng, lat, hdg], t0: now, seen: now }); return; }
    s.from = pose(s, now);
    s.to = [lng, lat, hdg];
    s.t0 = now;
    s.seen = now;
  }
  function pose(s, now) {
    const k = Math.min(Math.max((now - s.t0) / pushMs, 0), 1);
    let dh = ((s.to[2] - s.from[2] + 540) % 360) - 180;
    return [s.from[0] + (s.to[0] - s.from[0]) * k, s.from[1] + (s.to[1] - s.from[1]) * k, (s.from[2] + dh * k + 360) % 360];
  }

  const SPRITE_COLORS = { white: "#eeefed", silver: "#b6babe", grey: "#767a7e", gray: "#767a7e", black: "#1e2023", blue: "#28488e", red: "#ac1e20", green: "#2a663e", yellow: "#e0bc2a", orange: "#d87020", brown: "#704e34", beige: "#d0c0a2" };
  let trafficRows = [];
  let targetFeatures = [];
  let lastFrame = 0;
  function animate(now) {
    requestAnimationFrame(animate);
    if (now - lastFrame < 33) return; // ~30 fps is plenty for a map
    lastFrame = now;
    for (const [tail, entry] of uavMarkers) {
      const s = smooth.get("uav:" + tail);
      if (!s) continue;
      const [lng, lat, hdg] = pose(s, now);
      entry.marker.setLngLat([lng, lat]);
      entry.el.querySelector("svg").style.transform = `rotate(${hdg}deg)`;
    }
    for (const [id, entry] of objectMarkers) {
      const s = smooth.get("obj:" + id);
      if (!s || !entry.object.speedKmh) continue;
      const [lng, lat, hdg] = pose(s, now);
      entry.marker.setLngLat([lng, lat]);
      entry.heading = hdg;
      sizeObject(entry);
    }
    // A tracked target's ring glides between state pushes like the car it marks, instead of
    // jumping once a push and trailing it in between.
    if (targetFeatures.length > 0) {
      for (const f of targetFeatures) {
        const s = smooth.get("tgt:" + f.properties.tail);
        if (s) f.geometry.coordinates = pose(s, now).slice(0, 2);
      }
      map.getSource("targets").setData({ type: "FeatureCollection", features: targetFeatures });
      // The followed object's detection marker too: its position otherwise only changes with the
      // detection updates (every 50 m or 20 s), so it trailed the car it marks.
      for (const f of targetFeatures) {
        const marker = detectionMarkers.get(`${f.properties.tail}|${f.properties.missionId}|${f.properties.trackId}`);
        const s = smooth.get("tgt:" + f.properties.tail);
        if (marker && s && f.properties.state === "Tracking") marker.setLngLat(pose(s, now).slice(0, 2));
      }
    }
    if (map.getZoom() >= 12.5) {
      const features = [];
      for (const r of trafficRows) {
        const s = smooth.get("trf:" + r[0]);
        if (!s) continue;
        const [lng, lat, hdg] = pose(s, now);
        features.push({ type: "Feature", properties: { sprite: r[4], heading: hdg, color: SPRITE_COLORS[r[4].split("-")[1]] || "#888" }, geometry: { type: "Point", coordinates: [lng, lat] } });
      }
      map.getSource("traffic").setData({ type: "FeatureCollection", features });
    }
  }

  const loadingSprites = new Set();
  function loadSprite(target, id) {
    if (!/^[a-z]+-[a-z]+$/.test(id) || loadingSprites.has(target.id + id)) return;
    loadingSprites.add(target.id + id);
    target.addImage(id, { width: 1, height: 1, data: new Uint8Array(4) }); // until it loads
    fetch(`/api/sprites/${id}.png`).then((r) => (r.ok ? r.blob() : null)).then(async (blob) => {
      if (!blob) return;
      const bitmap = await createImageBitmap(blob);
      if (target.hasImage(id)) target.removeImage(id);
      target.addImage(id, bitmap);
    }).catch(() => {});
  }

  // ---- Live state ----

  const uavMarkers = new Map();
  const objectMarkers = new Map();
  const detectionMarkers = new Map();
  let lastState = null;
  let cameraTail = null;
  requestAnimationFrame(animate);

  // The payload camera, in 3D (camera3d.js); the server's detector view on request.
  const cam3d = window.createCamera3D({
    container: $("camera3d"),
    mapInfo,
    photos,
    poseOf: (key, now) => { const s = smooth.get(key); return s ? pose(s, now) : null; },
    getState: () => lastState,
    getTraffic: () => trafficRows,
    loadSprite,
  });
  let cameraView = "3d";

  function render(state) {
    lastState = state;
    const now = performance.now();
    if (lastPushAt) pushMs = Math.min(Math.max(now - lastPushAt, 80), 1000);
    lastPushAt = now;
    trafficRows = state.vehicles || [];
    for (const r of trafficRows) aim("trf:" + r[0], r[2], r[1], r[3]);
    for (const o of state.objects) aim("obj:" + o.id, o.lng, o.lat, o.headingDeg);
    for (const u of state.uavs) aim("uav:" + u.tailNumber, u.lng, u.lat, u.headingDeg);
    for (const u of state.uavs) if (u.target) aim("tgt:" + u.tailNumber, u.target[0], u.target[1], 0);
    // Forget what hasn't been in an update for a while (drove out of view).
    for (const [key, s] of smooth) if (now - s.seen > 5000) smooth.delete(key);
    setPill("hostStatus", state.connected ? "host: connected" : "host: waiting…", state.connected ? "ok" : "wait");
    $("hostStatus").title = "UavOps.Agent fleet hub: " + state.hostHubUrl;
    const det = state.detector;
    if (det.mode === "Onboard")
      setPill("detectorPill", det.reachable ? "detector: onboard VLM" : "detector: unreachable", det.reachable ? "ok" : "wait");
    else
      setPill("detectorPill", "detector: simulated", "");
    $("detectorPill").title = det.mode === "Onboard"
      ? "UavOps.Onboard.Detector at " + det.url + " searches the survey frames with a vision model"
      : "Tag matching inside the camera frame (Simulator:Detector = Simulated), no model";
    for (const b of document.querySelectorAll("#timeScale button"))
      b.setAttribute("aria-pressed", String(Number(b.dataset.scale) === state.timeScale));
    // The onboard computer runs in real time whatever the sim speed, so sped up it sees fewer frames.
    const onboardBusy = det.mode === "Onboard" && state.uavs.some((u) => u.looking);
    $("timeScaleNote").hidden = !(onboardBusy && state.timeScale > 1);
    $("timeScaleNote").textContent = `Onboard detection runs in real time - at ${state.timeScale}× it sees 1/${state.timeScale} of the frames. Measure detection at 1×.`;

    const routes = [], waypoints = [], trails = [], footprints = [], sightlines = [], lookcenters = [], destinations = [], targets = [];
    for (const u of state.uavs) {
      const color = colorOf(u.tailNumber);
      if (u.route.length > 1) {
        routes.push(feature("LineString", u.route, { color, remaining: false }));
        if (u.waypointIndex !== null && u.waypointIndex !== undefined)
          routes.push(feature("LineString", [[u.lng, u.lat], ...u.route.slice(u.waypointIndex)], { color, remaining: true }));
        u.route.forEach((p, i) => waypoints.push(feature("Point", p, { color, flown: u.waypointIndex != null && i < u.waypointIndex })));
      }
      if (u.trail.length > 1) trails.push(feature("LineString", [...u.trail, [u.lng, u.lat]], { color }));
      // Every airborne UAV's camera on the ground, all the time.
      if (u.mode !== "Landed" && u.footprint.length === 4) {
        footprints.push(feature("Polygon", [[...u.footprint, u.footprint[0]]], { color, looking: u.looking }));
        const centre = [0, 1].map((k) => u.footprint.reduce((sum, p) => sum + p[k], 0) / 4);
        sightlines.push(feature("LineString", [[u.lng, u.lat], centre], { color }));
        lookcenters.push(feature("Point", centre, { color, label: u.tailNumber }));
      }
      if (u.destination) destinations.push(feature("LineString", [[u.lng, u.lat], u.destination], { color }));
      if (u.target)
        targets.push(feature("Point", u.target, {
          tail: u.tailNumber, missionId: u.missionId, trackId: u.targetTrackId, color, state: u.targetState,
          label: `${u.tailNumber} ${u.targetTrackId ?? ""} ${u.targetLabel ?? ""}${u.targetState && u.targetState !== "Tracking" ? " (" + u.targetState.toLowerCase() + ")" : ""}`.trim(),
        }));
      placeUav(u, color);
    }
    map.getSource("routes").setData(collection(routes));
    map.getSource("waypoints").setData(collection(waypoints));
    map.getSource("trails").setData(collection(trails));
    map.getSource("footprints").setData(collection(footprints));
    map.getSource("sightlines").setData(collection(sightlines));
    map.getSource("lookcenters").setData(collection(lookcenters));
    map.getSource("destinations").setData(collection(destinations));
    targetFeatures = targets;
    map.getSource("targets").setData(collection(targets));

    syncObjects(state.objects);
    syncDetections(state.detections);
    renderUavList(state.uavs);
    renderDetectionList(state.detections);
    renderObjectList(state.objects);
    renderCamera(state);
  }

  // ---- Payload camera ----

  function openCamera(tail) {
    if (cameraTail === tail) return;
    cameraTail = tail;
    $("cameraPanel").hidden = false;
    $("cameraTitle").textContent = "Camera · " + tail;
    $("cameraSwatch").style.background = colorOf(tail);
    showCameraView();
    $("lastDetection").hidden = true;
    if (lastState) render(lastState);
  }

  // Live (3D, in the browser) or Detector (the server's MJPEG, what the onboard model is given).
  function showCameraView() {
    const detector = cameraView === "detector";
    $("cameraView3d").setAttribute("aria-pressed", String(!detector));
    $("cameraViewDetector").setAttribute("aria-pressed", String(detector));
    $("cameraFeed").hidden = !detector;
    if (detector) {
      cam3d.hide();
      // MJPEG: the browser keeps the stream open and swaps frames in place.
      $("cameraFeed").src = "/api/uavs/" + encodeURIComponent(cameraTail) + "/camera.mjpg";
    } else {
      $("cameraFeed").removeAttribute("src"); // ends the stream
      cam3d.show(cameraTail);
    }
  }
  $("cameraView3d").addEventListener("click", () => { cameraView = "3d"; if (cameraTail) showCameraView(); });
  $("cameraViewDetector").addEventListener("click", () => { cameraView = "detector"; if (cameraTail) showCameraView(); });
  $("cameraIr").addEventListener("click", () => { cam3d.setIr(!cam3d.ir); $("cameraIr").setAttribute("aria-pressed", String(cam3d.ir)); });
  $("cameraExpand").addEventListener("click", () => { $("cameraPanel").classList.toggle("camera-large"); cam3d.map.resize(); });

  function closeCamera() {
    cameraTail = null;
    $("cameraFeed").removeAttribute("src"); // ends the stream
    cam3d.hide();
    $("cameraPanel").hidden = true;
    if (lastState) render(lastState);
  }
  $("cameraClose").addEventListener("click", closeCamera);

  function renderCamera(state) {
    if (!cameraTail) return;
    const u = state.uavs.find((x) => x.tailNumber === cameraTail);
    if (!u) return;
    $("cameraMeta").textContent = `${u.altitudeFt} ft · ${Math.round(u.headingDeg)}° · ` +
      (u.mode === "Searching" && u.waypointIndex === 0 ? "to route start" : u.mode);

    const det = state.detector;
    let status;
    if (det.mode !== "Onboard") {
      status = "Detector: simulated (tag match, no model).";
    } else if (!det.reachable) {
      status = `Detector: <strong>unreachable</strong> at ${esc(det.url)}.`;
    } else {
      const task = det.tasks.find((t) => t.tailNumber === cameraTail);
      if (!task) status = u.searchPrompt ? "Detector: starting…" : "Detector: idle - no search target.";
      else if (task.phase) {
        // The every-frame pipeline on the live video: what its executive is doing, and how fast.
        const t = task.timing;
        const phase = task.phase === "Tracking" || task.phase === "Reacquiring"
          ? `${task.phase} <strong>${esc(task.targetTrackId ?? "")}</strong> (${esc(task.prompt)})`
          : `${task.phase} for <strong>${esc(task.prompt)}</strong>`;
        status = `${phase} · ${t ? t.fps.toFixed(1) + " fps, detect " + Math.round(t.detectMs) + " ms" : ""}` +
          ` · ${task.detections} found` +
          (task.lastError ? ` · <span style="color:var(--danger)">${esc(task.lastError)}</span>` : "");
      }
      else {
        const behind = Math.max(0, u.lastFrameSeq - task.analyzedThroughSeq);
        status = `Searching for <strong>${esc(task.prompt)}</strong> · ${task.framesAnalyzed} frames analysed` +
          (task.lastLatencyMs ? ` · ${(task.lastLatencyMs / 1000).toFixed(1)} s/frame` : "") +
          ` · ${behind} waiting · ${task.detections} found` +
          (task.lastError ? ` · <span style="color:var(--danger)">${esc(task.lastError)}</span>` : "");
      }
    }
    $("detectorStatus").innerHTML = status;

    const last = det.recent.filter((d) => d.tailNumber === cameraTail && d.missionId === u.missionId).at(-1);
    if (!last) { $("lastDetection").hidden = true; return; }
    // The frame kept with the detection; an old survey-pipeline one without it is still in the buffer.
    const src = last.snapshotId != null
      ? `/api/onboard/snapshots/${last.snapshotId}.jpg`
      : `/api/uavs/${encodeURIComponent(cameraTail)}/frames/${last.frameSeq}.jpg`;
    if ($("lastDetectionFrame").getAttribute("src") !== src) $("lastDetectionFrame").src = src;
    const b = last.box, box = $("lastDetectionBox").style;
    box.left = b.x1 / 10 + "%"; box.top = b.y1 / 10 + "%";
    box.width = (b.x2 - b.x1) / 10 + "%"; box.height = (b.y2 - b.y1) / 10 + "%";
    $("lastDetectionText").textContent =
      `${last.label} · ${Math.round(last.confidence * 100)}% · frame #${last.frameSeq} · model ${(last.modelLatencyMs / 1000).toFixed(1)} s`;
    $("lastDetection").hidden = false;
  }

  function placeUav(u, color) {
    let entry = uavMarkers.get(u.tailNumber);
    if (!entry) {
      const el = document.createElement("div");
      el.className = "uav-marker";
      el.innerHTML =
        `<svg viewBox="0 0 30 30"><path d="M15 3 L24 26 L15 21 L6 26 Z" fill="${color}" stroke="#0f0f10" stroke-width="1.5" stroke-linejoin="round"/></svg>` +
        `<span class="uav-label"></span>`;
      entry = { el, marker: new maplibregl.Marker({ element: el }).setLngLat([u.lng, u.lat]).addTo(map) };
      el.title = "Show " + u.tailNumber + "'s camera";
      el.addEventListener("click", (e) => { e.stopPropagation(); openCamera(u.tailNumber); });
      uavMarkers.set(u.tailNumber, entry);
    }
    entry.el.querySelector(".uav-label").textContent = `${u.tailNumber} · ${u.altitudeFt} ft`;
  }

  // Placed objects are drawn as what they are - the real vehicle photo (or the drawn vehicle) the
  // camera sees - at true size and heading; zoomed out too far to see one, a small marker instead.
  function syncObjects(objects) {
    const ids = new Set(objects.map((o) => o.id));
    for (const [id, entry] of objectMarkers) if (!ids.has(id)) { entry.marker.remove(); objectMarkers.delete(id); }
    for (const o of objects) {
      const existing = objectMarkers.get(o.id);
      if (existing) {
        existing.object = o;
        if (!o.speedKmh) { existing.marker.setLngLat([o.lng, o.lat]); existing.heading = o.headingDeg; }
        continue;
      }
      const el = document.createElement("div");
      el.className = "object-marker";
      el.innerHTML = `<img class="object-sprite" alt="" hidden /><span class="object-label">${esc(o.label)}</span>`;
      const entry = { object: o, el, heading: o.headingDeg, metersPerPixel: null, marker: new maplibregl.Marker({ element: el }).setLngLat([o.lng, o.lat]).addTo(map) };
      objectMarkers.set(o.id, entry);
      fetch(`/api/objects/${encodeURIComponent(o.id)}/sprite.png`).then(async (r) => {
        if (!r.ok) return;
        entry.metersPerPixel = Number(r.headers.get("X-Meters-Per-Pixel"));
        const img = el.querySelector("img");
        img.src = URL.createObjectURL(await r.blob());
        img.onload = () => sizeObject(entry);
      }).catch(() => {});
    }
  }

  function sizeObject(entry) {
    const img = entry.el.querySelector("img");
    if (!entry.metersPerPixel || !img.naturalWidth) return;
    const lat = entry.object.lat;
    const metersPerScreenPixel = (40075016.686 * Math.cos((lat * Math.PI) / 180)) / (512 * Math.pow(2, map.getZoom()));
    const scale = entry.metersPerPixel / metersPerScreenPixel;
    const w = img.naturalWidth * scale, h = img.naturalHeight * scale;
    const visible = h >= 6;
    img.hidden = !visible;
    entry.el.classList.toggle("object-marker-photo", visible);
    img.style.width = w + "px";
    img.style.height = h + "px";
    img.style.transform = `translate(-50%, -50%) rotate(${entry.heading ?? entry.object.headingDeg}deg)`;
  }
  map.on("zoom", () => { for (const entry of objectMarkers.values()) sizeObject(entry); });

  function syncDetections(detections) {
    // One marker per object found: a moving one's marker moves with its updates (keyed by its
    // track id), instead of a new circle for every update.
    const keyOf = (d) => d.trackId ? `${d.tailNumber}|${d.missionId}|${d.trackId}` : d.tailNumber + d.detectedAtUtc + d.lat;
    const popupHtml = (d) =>
      `<strong>${esc(d.prompt)}</strong> (${Math.round(d.confidence * 100)}%)` + (d.trackId ? ` · ${esc(d.trackId)}` : "") + `<br>` +
      `${d.updates > 1 ? "last " : ""}seen by ${esc(d.tailNumber)} at ${new Date(d.detectedAtUtc).toLocaleTimeString()}<br>` +
      `<span style="font-family:var(--font-mono)">${fmt(d.lat)}, ${fmt(d.lng)}</span>`;
    const keys = new Set(detections.map(keyOf));
    for (const [key, m] of detectionMarkers) if (!keys.has(key)) { m.remove(); detectionMarkers.delete(key); }
    for (const d of detections) {
      const key = keyOf(d);
      const existing = detectionMarkers.get(key);
      if (existing) {
        // A followed object's marker is moved by animate() with its UAV's target ring.
        const followed = targetFeatures.some((f) => f.properties.state === "Tracking" &&
          `${f.properties.tail}|${f.properties.missionId}|${f.properties.trackId}` === key);
        if (!followed) existing.setLngLat([d.lng, d.lat]);
        existing.getPopup().setHTML(popupHtml(d));
        continue;
      }
      const el = document.createElement("div");
      el.className = "detection-marker";
      const popup = new maplibregl.Popup({ offset: 14, closeButton: false }).setHTML(popupHtml(d));
      detectionMarkers.set(key, new maplibregl.Marker({ element: el }).setLngLat([d.lng, d.lat]).setPopup(popup).addTo(map));
    }
  }

  function renderUavList(uavs) {
    $("uavList").innerHTML = uavs.map((u) => {
      const total = u.route.length;
      const done = u.waypointIndex ?? (u.mode === "Searching" ? 0 : null);
      const mission = u.searchPrompt || total
        ? `<div class="mission">` +
            (u.zoneName ? `<strong>${esc(u.zoneName)}</strong>` : "") +
            (u.searchPrompt ? ` · looking for <strong>${esc(u.searchPrompt)}</strong>${u.looking ? ' <span class="looking">●</span>' : ""}` : "") +
            (total ? ` · ${total} wpts` : "") +
            // Before waypoint 1 it's flying to the route's start and circling there until it's
            // down at search altitude; the camera only surveys once it's on the route.
            (u.mode === "Searching" && u.waypointIndex === 0 ? `<div class="meta">getting to the route start at search altitude…</div>` : "") +
            (done !== null && total ? `<div class="progress"><div style="width:${Math.round((done / total) * 100)}%"></div></div>` : "") +
          `</div>`
        : "";
      return `<div class="card${u.tailNumber === cameraTail ? " card-selected" : ""}" data-lng="${u.lng}" data-lat="${u.lat}" data-tail="${esc(u.tailNumber)}" title="Show its camera">
        <div class="card-top">
          <span class="swatch" style="background:${colorOf(u.tailNumber)}"></span>
          <span class="tail">${esc(u.tailNumber)}</span>
          <span class="mode mode-${esc(u.mode)}">${esc(u.mode)}</span>
        </div>
        <div class="stats"><span>${u.speedKts} kts</span><span>${u.altitudeFt} ft</span><span>${Math.round(u.headingDeg)}°</span></div>
        ${mission}
      </div>`;
    }).join("");
  }

  function renderDetectionList(detections) {
    $("detectionCount").textContent = detections.length ? String(detections.length) : "";
    $("detectionList").innerHTML = detections.length
      ? detections.slice().reverse().map((d) =>
          `<div class="row" data-lng="${d.lng}" data-lat="${d.lat}">
            <span class="what">${esc(d.prompt)} <span class="meta">by ${esc(d.tailNumber)}${d.trackId ? " · " + esc(d.trackId) : ""}${d.updates > 1 ? " · moving" : ""}</span></span>
            <span class="where">${fmt(d.lat)}, ${fmt(d.lng)}</span>
          </div>`).join("")
      : `<div class="empty">None yet.</div>`;
  }

  function renderObjectList(objects) {
    $("objectList").innerHTML = objects.length
      ? objects.map((o) =>
          `<div class="row" data-lng="${o.lng}" data-lat="${o.lat}">
            <span class="what">${esc(o.label)}${o.speedKmh ? ` <span class="meta">driving ${Math.round(o.speedKmh)} km/h</span>` : ""}</span>
            <span class="where">${fmt(o.lat)}, ${fmt(o.lng)}</span>
            <button type="button" class="icon-btn" data-remove="${esc(o.id)}" title="Remove">✕</button>
          </div>`).join("")
      : `<div class="empty">Nothing placed.</div>`;
  }

  // ---- Interaction ----

  for (const listId of ["uavList", "detectionList", "objectList"]) {
    $(listId).addEventListener("click", async (e) => {
      const remove = e.target.closest("[data-remove]");
      if (remove) {
        await fetch("/api/objects/" + encodeURIComponent(remove.dataset.remove), { method: "DELETE" });
        return;
      }
      const target = e.target.closest("[data-lng]");
      if (target) map.easeTo({ center: [Number(target.dataset.lng), Number(target.dataset.lat)], zoom: Math.max(map.getZoom(), 14) });
      if (target?.dataset.tail) openCamera(target.dataset.tail);
    });
  }

  $("timeScale").addEventListener("click", (e) => {
    const b = e.target.closest("button[data-scale]");
    if (b) fetch("/api/timescale", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ value: Number(b.dataset.scale) }) });
  });

  $("resetFleet").addEventListener("click", async (e) => {
    e.currentTarget.disabled = true;
    try { await fetch("/api/reset", { method: "POST" }); }
    finally { $("resetFleet").disabled = false; }
  });

  let placing = false;
  function setPlacing(on) {
    placing = on;
    $("placeHint").hidden = !on;
    $("map").classList.toggle("placing", on);
    if (on) $("objectLabel").focus();
  }
  $("placeObject").addEventListener("click", () => setPlacing(!placing));
  $("cancelPlace").addEventListener("click", () => setPlacing(false));
  map.on("click", async (e) => {
    if (!placing) return;
    const label = $("objectLabel").value.trim();
    if (!label) { $("objectLabel").focus(); return; }
    await fetch("/api/objects", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ label, lat: e.lngLat.lat, lng: e.lngLat.lng, speedKmh: Number($("objectSpeed").value) || 0 }),
    });
    setPlacing(false);
  });

  // ---- Feed ----

  const connection = new signalR.HubConnectionBuilder().withUrl("/simHub").withAutomaticReconnect().build();
  connection.on("state", render);
  connection.onreconnecting(() => setPill("hostStatus", "simulator: reconnecting…", "wait"));
  render(await fetch("/api/state").then((r) => r.json()));
  await startFeed();

  async function startFeed() {
    try { await connection.start(); reportView(); }
    catch { setTimeout(startFeed, 2000); }
  }
  function reportView() {
    if (connection.state !== "Connected") return;
    const b = map.getBounds();
    connection.invoke("SetView", b.getWest(), b.getSouth(), b.getEast(), b.getNorth(), map.getZoom()).catch(() => {});
  }
  map.on("moveend", reportView);
  connection.onreconnected(reportView);

  // ---- Helpers ----

  function setPill(id, text, kind) {
    const el = $(id);
    el.textContent = text;
    el.className = "pill" + (kind ? " pill-" + kind : "");
  }

  function feature(type, coordinates, properties) {
    return { type: "Feature", properties, geometry: { type, coordinates } };
  }

  function collection(features) {
    return { type: "FeatureCollection", features };
  }

})();
