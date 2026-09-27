// UavOps Simulator page: the offline map plus a live view of the simulated fleet.
// State arrives from /simHub ("state", ~5 Hz); everything on the map is redrawn from it.
(async function () {
  "use strict";

  const UAV_COLORS = { "997": "#5b8def", "998": "#e0a940", "999": "#4bb768" };
  const colorOf = (tail) => UAV_COLORS[tail] || "#c678dd";

  // Same coordinates as UavOps.Agent.Contracts' KnownPoints - the names the operator can use.
  const KNOWN_POINTS = [
    { name: "home", lat: 31.801447, lng: 34.643497 },
    { name: "alpha", lat: 31.812, lng: 34.66 },
    { name: "bravo", lat: 31.79, lng: 34.63 },
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
    center: [34.652, 31.806],
    zoom: 13.4,
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

  // ---- Overlay layers ----

  const empty = { type: "FeatureCollection", features: [] };
  for (const id of ["zones", "footprints", "routes", "trails", "destinations"])
    map.addSource(id, { type: "geojson", data: empty });

  map.addLayer({ id: "zones-fill", type: "fill", source: "zones", paint: { "fill-color": "#5b8def", "fill-opacity": 0.1 } });
  map.addLayer({ id: "zones-line", type: "line", source: "zones", paint: { "line-color": "#5b8def", "line-width": 2, "line-opacity": 0.8 } });
  map.addLayer({ id: "footprints", type: "fill", source: "footprints", paint: { "fill-color": ["get", "color"], "fill-opacity": 0.16 } });
  map.addLayer({ id: "footprints-line", type: "line", source: "footprints", paint: { "line-color": ["get", "color"], "line-width": 1, "line-opacity": 0.6 } });
  map.addLayer({
    id: "routes", type: "line", source: "routes",
    layout: { "line-join": "round" },
    paint: {
      "line-color": ["get", "color"],
      "line-width": ["case", ["get", "remaining"], 2, 1],
      "line-opacity": ["case", ["get", "remaining"], 0.9, 0.3],
      "line-dasharray": [2, 1.5],
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
    // Above the zone's north-west corner, clear of anything drawn inside it.
    const north = Math.max(...ring.map((c) => c[1]));
    const west = Math.min(...ring.map((c) => c[0]));
    new maplibregl.Marker({ element: el, anchor: "bottom-left", offset: [0, -4] }).setLngLat([west, north]).addTo(map);
  }

  // ---- Live state ----

  const uavMarkers = new Map();
  const objectMarkers = new Map();
  const detectionMarkers = new Map();
  let lastState = null;

  function render(state) {
    lastState = state;
    setPill("hostStatus", state.connected ? "host: connected" : "host: waiting…", state.connected ? "ok" : "wait");
    $("hostStatus").title = "UavOps.Agent fleet hub: " + state.hostHubUrl;
    for (const b of document.querySelectorAll("#timeScale button"))
      b.setAttribute("aria-pressed", String(Number(b.dataset.scale) === state.timeScale));

    const routes = [], trails = [], footprints = [], destinations = [];
    for (const u of state.uavs) {
      const color = colorOf(u.tailNumber);
      if (u.route.length > 1) {
        routes.push(feature("LineString", u.route, { color, remaining: false }));
        if (u.waypointIndex !== null && u.waypointIndex !== undefined)
          routes.push(feature("LineString", [[u.lng, u.lat], ...u.route.slice(u.waypointIndex)], { color, remaining: true }));
      }
      if (u.trail.length > 1) trails.push(feature("LineString", [...u.trail, [u.lng, u.lat]], { color }));
      // Only while the onboard agent is looking: a camera footprint means nothing otherwise.
      if (u.looking && u.footprintRadiusMeters > 0)
        footprints.push(feature("Polygon", [circle(u.lat, u.lng, u.footprintRadiusMeters)], { color, looking: u.looking }));
      if (u.destination) destinations.push(feature("LineString", [[u.lng, u.lat], u.destination], { color }));
      placeUav(u, color);
    }
    map.getSource("routes").setData(collection(routes));
    map.getSource("trails").setData(collection(trails));
    map.getSource("footprints").setData(collection(footprints));
    map.getSource("destinations").setData(collection(destinations));

    syncObjects(state.objects);
    syncDetections(state.detections);
    renderUavList(state.uavs);
    renderDetectionList(state.detections);
    renderObjectList(state.objects);
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
      uavMarkers.set(u.tailNumber, entry);
    }
    entry.marker.setLngLat([u.lng, u.lat]);
    entry.el.querySelector("svg").style.transform = `rotate(${u.headingDeg}deg)`;
    entry.el.querySelector(".uav-label").textContent = `${u.tailNumber} · ${u.altitudeFt} ft`;
  }

  function syncObjects(objects) {
    const ids = new Set(objects.map((o) => o.id));
    for (const [id, m] of objectMarkers) if (!ids.has(id)) { m.remove(); objectMarkers.delete(id); }
    for (const o of objects) {
      if (objectMarkers.has(o.id)) continue;
      const el = document.createElement("div");
      el.className = "object-marker";
      el.innerHTML = `<span class="object-label">${esc(o.label)}</span>`;
      objectMarkers.set(o.id, new maplibregl.Marker({ element: el }).setLngLat([o.lng, o.lat]).addTo(map));
    }
  }

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
            (done !== null && total ? `<div class="progress"><div style="width:${Math.round((done / total) * 100)}%"></div></div>` : "") +
          `</div>`
        : "";
      return `<div class="card" data-lng="${u.lng}" data-lat="${u.lat}">
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

  function circle(lat, lng, radiusMeters) {
    const points = [];
    const dLat = radiusMeters / 111195;
    const dLng = dLat / Math.cos((lat * Math.PI) / 180);
    for (let i = 0; i <= 48; i++) {
      const a = (i / 48) * 2 * Math.PI;
      points.push([lng + dLng * Math.cos(a), lat + dLat * Math.sin(a)]);
    }
    return points;
  }
})();
