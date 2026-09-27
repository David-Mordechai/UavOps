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

  // ---- Real aerial photos (where downloaded: scripts/fetch-imagery.ps1) over the base map ----

  const photos = await fetch("/api/imagery").then((r) => r.json()).catch(() => []);
  if (photos.length) {
    map.addSource("imagery", {
      type: "raster",
      tiles: [location.origin + "/api/imagery/{z}/{x}/{y}.png"],
      tileSize: 256,
      minzoom: 12,
      maxzoom: 21,
      attribution: photos.map((p) => `${esc(p.title)}: ${esc(p.attribution)} (${esc(p.license)})`).join(" · "),
    });
    map.addLayer({ id: "imagery", type: "raster", source: "imagery" });
  }

  // ---- Overlay layers ----

  const empty = { type: "FeatureCollection", features: [] };
  for (const id of ["zones", "footprints", "routes", "waypoints", "trails", "destinations"])
    map.addSource(id, { type: "geojson", data: empty });

  // Zones in magenta, no UAV's colour, with a dark casing so the border reads on the aerial photos.
  map.addLayer({ id: "zones-fill", type: "fill", source: "zones", paint: { "fill-color": ZONE_COLOR, "fill-opacity": 0.08 } });
  map.addLayer({ id: "zones-casing", type: "line", source: "zones", layout: { "line-join": "round" }, paint: { "line-color": "#000", "line-width": 5, "line-opacity": 0.6 } });
  map.addLayer({ id: "zones-line", type: "line", source: "zones", layout: { "line-join": "round" }, paint: { "line-color": ZONE_COLOR, "line-width": 2.5 } });
  map.addLayer({ id: "footprints", type: "fill", source: "footprints", paint: { "fill-color": ["get", "color"], "fill-opacity": 0.16 } });
  map.addLayer({ id: "footprints-line", type: "line", source: "footprints", paint: { "line-color": ["get", "color"], "line-width": 1, "line-opacity": 0.6 } });
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

  // ---- Live state ----

  const uavMarkers = new Map();
  const objectMarkers = new Map();
  const detectionMarkers = new Map();
  let lastState = null;
  let cameraTail = null;

  function render(state) {
    lastState = state;
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

    const routes = [], waypoints = [], trails = [], footprints = [], destinations = [];
    for (const u of state.uavs) {
      const color = colorOf(u.tailNumber);
      if (u.route.length > 1) {
        routes.push(feature("LineString", u.route, { color, remaining: false }));
        if (u.waypointIndex !== null && u.waypointIndex !== undefined)
          routes.push(feature("LineString", [[u.lng, u.lat], ...u.route.slice(u.waypointIndex)], { color, remaining: true }));
        u.route.forEach((p, i) => waypoints.push(feature("Point", p, { color, flown: u.waypointIndex != null && i < u.waypointIndex })));
      }
      if (u.trail.length > 1) trails.push(feature("LineString", [...u.trail, [u.lng, u.lat]], { color }));
      // The camera's ground rectangle: while the onboard agent is looking, or while its view is open.
      if ((u.looking || u.tailNumber === cameraTail) && u.footprint.length === 4)
        footprints.push(feature("Polygon", [[...u.footprint, u.footprint[0]]], { color, looking: u.looking }));
      if (u.destination) destinations.push(feature("LineString", [[u.lng, u.lat], u.destination], { color }));
      placeUav(u, color);
    }
    map.getSource("routes").setData(collection(routes));
    map.getSource("waypoints").setData(collection(waypoints));
    map.getSource("trails").setData(collection(trails));
    map.getSource("footprints").setData(collection(footprints));
    map.getSource("destinations").setData(collection(destinations));

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
    // MJPEG: the browser keeps the stream open and swaps frames in place.
    $("cameraFeed").src = "/api/uavs/" + encodeURIComponent(tail) + "/camera.mjpg";
    $("lastDetection").hidden = true;
    if (lastState) render(lastState);
  }

  function closeCamera() {
    cameraTail = null;
    $("cameraFeed").removeAttribute("src"); // ends the stream
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
    const src = `/api/uavs/${encodeURIComponent(cameraTail)}/frames/${last.frameSeq}.jpg`;
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
    entry.marker.setLngLat([u.lng, u.lat]);
    entry.el.querySelector("svg").style.transform = `rotate(${u.headingDeg}deg)`;
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
        existing.marker.setLngLat([o.lng, o.lat]);
        continue;
      }
      const el = document.createElement("div");
      el.className = "object-marker";
      el.innerHTML = `<img class="object-sprite" alt="" hidden /><span class="object-label">${esc(o.label)}</span>`;
      const entry = { object: o, el, metersPerPixel: null, marker: new maplibregl.Marker({ element: el }).setLngLat([o.lng, o.lat]).addTo(map) };
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
    img.style.transform = `translate(-50%, -50%) rotate(${entry.object.headingDeg}deg)`;
  }
  map.on("zoom", () => { for (const entry of objectMarkers.values()) sizeObject(entry); });

  function syncDetections(detections) {
    const keyOf = (d) => d.tailNumber + d.detectedAtUtc + d.lat;
    const keys = new Set(detections.map(keyOf));
    for (const [key, m] of detectionMarkers) if (!keys.has(key)) { m.remove(); detectionMarkers.delete(key); }
    for (const d of detections) {
      const key = keyOf(d);
      if (detectionMarkers.has(key)) continue;
      const el = document.createElement("div");
      el.className = "detection-marker";
      const popup = new maplibregl.Popup({ offset: 14, closeButton: false }).setHTML(
        `<strong>${esc(d.prompt)}</strong> (${Math.round(d.confidence * 100)}%)<br>` +
        `seen by ${esc(d.tailNumber)} at ${new Date(d.detectedAtUtc).toLocaleTimeString()}<br>` +
        `<span style="font-family:var(--font-mono)">${fmt(d.lat)}, ${fmt(d.lng)}</span>`);
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
            <span class="what">${esc(d.prompt)} <span class="meta">by ${esc(d.tailNumber)}</span></span>
            <span class="where">${fmt(d.lat)}, ${fmt(d.lng)}</span>
          </div>`).join("")
      : `<div class="empty">None yet.</div>`;
  }

  function renderObjectList(objects) {
    $("objectList").innerHTML = objects.length
      ? objects.map((o) =>
          `<div class="row" data-lng="${o.lng}" data-lat="${o.lat}">
            <span class="what">${esc(o.label)}</span>
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
      body: JSON.stringify({ label, lat: e.lngLat.lat, lng: e.lngLat.lng }),
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
    try { await connection.start(); }
    catch { setTimeout(startFeed, 2000); }
  }

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
