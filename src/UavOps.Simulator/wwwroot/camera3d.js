// The payload camera as the operator watches it: a second MapLibre map, looking from the UAV
// (its position and altitude, 3D terrain) where the gimbal points - straight down while surveying,
// locked on a point after PointPayload, otherwise forward and down (its rest position) - at the
// payload's zoom. Satellite, drone photos, extruded buildings, moving traffic and the scenario
// objects, with a HUD and a sensor look (grain, vignette, a little gimbal jitter, EO or IR).
// Everything comes from the same world state the server's camera draws for the onboard detector.
(function () {
  "use strict";

  const FT = 0.3048;
  const REST_DEPRESSION_DEG = 30; // gimbal rest: forward, 30° below the horizon
  const SPRITE_PX_PER_METER = 50;

  window.createCamera3D = function ({ container, mapInfo, photos, poseOf, getState, getTraffic, loadSprite }) {
    container.innerHTML =
      `<div class="c3d-map"></div>` +
      `<div class="c3d-grain"></div><div class="c3d-vignette"></div>` +
      `<canvas class="c3d-hud"></canvas>`;
    const mapEl = container.querySelector(".c3d-map");
    const hud = container.querySelector(".c3d-hud");
    const ctx = hud.getContext("2d");

    const sources = {
      traffic: { type: "geojson", data: empty() },
      objects: { type: "geojson", data: empty() },
      detections: { type: "geojson", data: empty() },
    };
    const layers = [{ id: "background", type: "background", paint: { "background-color": "#b8ad97" } }];
    if (mapInfo.basemap) {
      sources.basemap = { type: "raster", tiles: [location.origin + "/api/basemap/{z}/{x}/{y}.jpg?v=" + mapInfo.basemap.version], tileSize: 256, minzoom: mapInfo.basemap.minzoom, maxzoom: mapInfo.basemap.maxzoom };
      layers.push({ id: "basemap", type: "raster", source: "basemap", paint: { "raster-fade-duration": 0 } });
    }
    if (photos.length) {
      // Only where there are photos (outside them a tile is empty, never cached, and asked for again
      // every frame - measured: ~120 requests a second, the view flashing as they came and went).
      const b = photos.map((p) => p.bounds);
      const bounds = [Math.min(...b.map((x) => x[0])), Math.min(...b.map((x) => x[1])), Math.max(...b.map((x) => x[2])), Math.max(...b.map((x) => x[3]))];
      sources.photos = { type: "raster", tiles: [location.origin + "/api/imagery/{z}/{x}/{y}.png"], tileSize: 256, minzoom: 12, maxzoom: 19, bounds };
      layers.push({ id: "photos", type: "raster", source: "photos", paint: { "raster-fade-duration": 0 } });
    }
    if (mapInfo.offlineTiles) {
      sources.osm = { type: "vector", url: "pmtiles://" + location.origin + "/map/israel.pmtiles" };
      // Roads crisper than 10 m satellite pixels can show, at their real width.
      layers.push({
        id: "roads", type: "line", source: "osm", "source-layer": "transportation",
        filter: ["in", ["get", "class"], ["literal", ["motorway", "trunk", "primary", "secondary", "tertiary", "minor", "service"]]],
        layout: { "line-join": "round", "line-cap": "round" },
        paint: {
          "line-color": "#7d7b76",
          "line-opacity": 0.55,
          "line-width": ["interpolate", ["exponential", 2], ["zoom"], 12, 0.4, 22, ["match", ["get", "class"], ["motorway", "trunk", "primary"], 700, ["secondary", "tertiary"], 500, 300]],
        },
      });
      layers.push({
        id: "buildings", type: "fill-extrusion", source: "osm", "source-layer": "building", minzoom: 14,
        paint: { "fill-extrusion-color": "#cfc8bb", "fill-extrusion-height": ["coalesce", ["get", "render_height"], 8], "fill-extrusion-opacity": 0.92 },
      });
    }
    // In IR, vehicles glow (engines, sun-warmed metal): a soft hot spot under each one.
    layers.push({
      id: "ir-hot", type: "circle", source: "traffic", layout: { visibility: "none" },
      paint: { "circle-radius": ["interpolate", ["exponential", 2], ["zoom"], 14, 1, 22, 400], "circle-color": "#fff", "circle-blur": 0.6, "circle-pitch-alignment": "map" },
    });
    const size = (z) => (512 * Math.pow(2, z)) / (40075016.686 * Math.cos((31.6 * Math.PI) / 180)) / SPRITE_PX_PER_METER;
    layers.push({
      id: "traffic", type: "symbol", source: "traffic",
      layout: {
        "icon-image": ["get", "sprite"], "icon-rotate": ["get", "heading"], "icon-rotation-alignment": "map", "icon-pitch-alignment": "map",
        "icon-allow-overlap": true, "icon-ignore-placement": true,
        "icon-size": ["interpolate", ["exponential", 2], ["zoom"], 12, size(12), 24, size(24)],
      },
    });
    layers.push({
      id: "objects", type: "symbol", source: "objects",
      layout: {
        "icon-image": ["get", "sprite"], "icon-rotate": ["get", "heading"], "icon-rotation-alignment": "map", "icon-pitch-alignment": "map",
        "icon-allow-overlap": true, "icon-ignore-placement": true,
        "icon-size": ["interpolate", ["exponential", 2], ["zoom"], 12, ["*", ["get", "k"], size(12)], 24, ["*", ["get", "k"], size(24)]],
      },
    });
    layers.push({ id: "detections", type: "line", source: "detections", paint: { "line-color": "#50ff78", "line-width": 2 } });
    if (mapInfo.terrain)
      sources.terrain = { type: "raster-dem", tiles: [location.origin + "/api/terrain/{z}/{x}/{y}.png?v=" + mapInfo.terrain.version], tileSize: 256, encoding: "terrarium", minzoom: mapInfo.terrain.minzoom, maxzoom: mapInfo.terrain.maxzoom };

    // ?debug on the page URL: keep each frame readable, and log every frame's brightness with the
    // camera and tile state (window.simCameraDebug) - how a one-frame flash is found.
    const debug = new URLSearchParams(location.search).has("debug");
    const map = new maplibregl.Map({
      preserveDrawingBuffer: debug,
      container: mapEl,
      style: { version: 8, sources, layers },
      interactive: false,
      attributionControl: false,
      maxPitch: 85,
      maxZoom: 24,
      fadeDuration: 0,
      // A tilted view reaches the horizon: hundreds of tiles. Keep them, or they're dropped and
      // fetched again as the view swings round an orbit.
      maxTileCacheSize: 3000,
      center: [35.04, 31.345],
      zoom: 14,
    });
    window.simCamera = map; // handy from the browser console
    map.on("styleimagemissing", (e) => loadSprite(map, e.id));
    let ready = false;
    map.on("load", () => {
      // Haze toward the horizon, as through 1-2 km of summer air.
      map.setSky({ "sky-color": "#9fbfdd", "horizon-color": "#e3ddd0", "fog-color": "#d8d1c2", "sky-horizon-blend": 0.7, "horizon-fog-blend": 0.6, "fog-ground-blend": 0.35, "atmosphere-blend": 0.6 });
      ready = true;
    });

    let tail = null;
    let ir = false;
    let running = false;
    const objectImages = new Map(); // object id → k (sprite metres per pixel × 50), once loaded

    function show(t) {
      tail = t;
      container.hidden = false;
      map.resize();
      if (!running) { running = true; requestAnimationFrame(frame); }
    }
    function hide() { tail = null; container.hidden = true; running = false; }
    function setIr(on) {
      ir = on;
      container.classList.toggle("c3d-ir", ir);
      if (ready) map.setLayoutProperty("ir-hot", "visibility", ir ? "visible" : "none");
    }

    // ----- Per animation frame -----

    let last = 0;
    let lastWorld = 0;
    const lastData = {};
    let mode = "";
    let look = null;
    let lastGround = 0, lastTargetGround = 0;

    // Terrain on below this zoom, off above the other (a gap, so it doesn't toggle back and forth).
    const TERRAIN_ON_ZOOM = 16, TERRAIN_OFF_ZOOM = 16.8;
    let useTerrain = false;
    function setTerrain(on) {
      on = on && !!mapInfo.terrain;
      if (on === useTerrain) return;
      useTerrain = on;
      map.setTerrain(on ? { source: "terrain", exaggeration: 1 } : null);
    }
    function frame(now) {
      if (!running) return;
      requestAnimationFrame(frame);
      if (now - last < 33 || !ready || !tail) return; // ~30 fps
      last = now;
      const state = getState();
      const u = state?.uavs.find((x) => x.tailNumber === tail);
      const pose = poseOf("uav:" + tail, now);
      if (!u || !pose) return;
      const [lng, lat, heading] = pose;
      const altitude = Math.max(u.altitudeFt, 1) * FT;
      const ground = elevationAt(lng, lat) ?? lastGround;
      lastGround = ground;

      // Where the gimbal points.
      let target;
      if (u.surveying || u.mode === "Landed") {
        mode = u.surveying ? "SURVEY" : "PARKED";
        target = offset(lng, lat, heading, 0.5); // straight down (a hair forward, so there's a heading)
      } else if (u.lookAt) {
        mode = "LOCK";
        target = u.lookAt;
      } else {
        mode = "FWD";
        const agl = Math.max(altitude - ground, 20);
        target = offset(lng, lat, heading, agl / Math.tan((REST_DEPRESSION_DEG * Math.PI) / 180));
      }
      // The payload's field of view, not MapLibre's: zooming in by the ratio of the two is a
      // narrower lens from the same place.
      const canvas = map.getCanvas();
      const aspect = canvas.clientWidth / Math.max(canvas.clientHeight, 1);
      const mapHfov = 2 * Math.atan(Math.tan(map.transform._fov / 2) * aspect);
      const payloadHfov = (Math.max(u.payloadHfovDeg, 0.5) * Math.PI) / 180;
      const lens = Math.log2(Math.tan(mapHfov / 2) / Math.tan(payloadHfov / 2));

      // 3D terrain only for wide views, where relief shows. With terrain on, MapLibre paints the
      // imagery onto the terrain first, and zoomed in (hundreds of small photo tiles, new ones
      // arriving all the time as the view swings round an orbit) a frame could come out with the
      // imagery missing - the 998 camera blinked. Zoomed in from thousands of feet the ground is
      // flat anyway: the camera is placed by its height above the ground it looks at instead.
      const targetGround = elevationAt(target[0], target[1]) ?? lastTargetGround;
      lastTargetGround = targetGround;
      let cam;
      try {
        cam = map.calculateCameraOptionsFromTo(new maplibregl.LngLat(lng, lat), Math.max(altitude - targetGround, 20), new maplibregl.LngLat(target[0], target[1]), 0);
      } catch { return; }
      setTerrain(cam.zoom + lens < (useTerrain ? TERRAIN_OFF_ZOOM : TERRAIN_ON_ZOOM));
      if (useTerrain) {
        try {
          cam = map.calculateCameraOptionsFromTo(new maplibregl.LngLat(lng, lat), Math.max(altitude, ground + 20), new maplibregl.LngLat(target[0], target[1]));
        } catch { return; }
      }
      cam.zoom += lens;
      // A little gimbal jitter, less when zoomed out.
      const jitter = 0.04 / Math.max(u.payloadZoom, 1) ** 0.3;
      cam.bearing += Math.sin(now / 173) * jitter + Math.sin(now / 71) * jitter * 0.5;
      cam.pitch = Math.min(84, Math.max(0, cam.pitch + Math.sin(now / 131) * jitter));
      map.jumpTo(cam);
      if (debug) logFrame(u, cam);
      look = { center: cam.center, range: distance([lng, lat], [cam.center.lng, cam.center.lat], altitude - targetGround) };

      if (now - lastWorld >= 100) { lastWorld = now; updateWorld(state, now); }
      drawHud(u, heading, altitude, ground, now);
    }

    function updateWorld(state, now) {
      const traffic = [];
      for (const r of getTraffic()) {
        const p = poseOf("trf:" + r[0], now);
        if (p) traffic.push(point(p[0], p[1], { sprite: r[4], heading: p[2] }));
      }
      setData("traffic", traffic);

      const objects = [];
      for (const o of state.objects) {
        const k = objectImages.get(o.id);
        if (k === undefined) { loadObject(o.id); continue; }
        const p = poseOf("obj:" + o.id, now) || [o.lng, o.lat, o.headingDeg];
        objects.push(point(p[0], p[1], { sprite: "obj-" + o.id, heading: p[2], k }));
      }
      setData("objects", objects);

      const boxes = (state.detections || []).map((d) => square(d.lng, d.lat, 7));
      setData("detections", boxes);
    }

    // Only when it changed: every setData re-tiles the source.
    function setData(id, features) {
      const key = JSON.stringify(features);
      if (lastData[id] === key) return;
      lastData[id] = key;
      map.getSource(id).setData(collection(features));
    }

    function loadObject(id) {
      objectImages.set(id, null); // loading
      fetch(`/api/objects/${encodeURIComponent(id)}/sprite.png`).then(async (r) => {
        if (!r.ok) return;
        const mpp = Number(r.headers.get("X-Meters-Per-Pixel")) || 1 / SPRITE_PX_PER_METER;
        const bitmap = await createImageBitmap(await r.blob());
        if (map.hasImage("obj-" + id)) map.removeImage("obj-" + id);
        map.addImage("obj-" + id, bitmap);
        objectImages.set(id, mpp * SPRITE_PX_PER_METER);
      }).catch(() => objectImages.delete(id));
    }

    // Ground height (m) from the terrain tiles themselves, whether or not the map shows terrain:
    // the z12 Terrarium tile under the point, decoded once. Undefined until its tile has loaded.
    const DEM_ZOOM = 12;
    const demTiles = new Map(); // "x/y" → ImageData | null (loading or missing)
    function elevationAt(lng, lat) {
      if (!mapInfo.terrain) return 0;
      const n = 2 ** DEM_ZOOM;
      const fx = ((lng + 180) / 360) * n;
      const r = (lat * Math.PI) / 180;
      const fy = ((1 - Math.log(Math.tan(r) + 1 / Math.cos(r)) / Math.PI) / 2) * n;
      const x = Math.floor(fx), y = Math.floor(fy), key = x + "/" + y;
      if (!demTiles.has(key)) { demTiles.set(key, null); loadDem(x, y, key); return undefined; }
      const img = demTiles.get(key);
      if (!img) return undefined;
      const px = Math.min(255, Math.floor((fx - x) * 256)), py = Math.min(255, Math.floor((fy - y) * 256));
      const i = (py * 256 + px) * 4;
      return img.data[i] * 256 + img.data[i + 1] + img.data[i + 2] / 256 - 32768;
    }
    function loadDem(x, y, key) {
      fetch(`/api/terrain/${DEM_ZOOM}/${x}/${y}.png?v=${mapInfo.terrain.version}`).then(async (res) => {
        if (res.status !== 200) { // no DEM here: sea level (Terrarium 0 m is red 128, green 0, blue 0)
          const data = new Uint8ClampedArray(256 * 256 * 4);
          for (let i = 0; i < data.length; i += 4) data[i] = 128;
          demTiles.set(key, { data });
          return;
        }
        const bitmap = await createImageBitmap(await res.blob(), { premultiplyAlpha: "none", colorSpaceConversion: "none" });
        const c = new OffscreenCanvas(256, 256).getContext("2d", { willReadFrequently: true });
        c.drawImage(bitmap, 0, 0);
        demTiles.set(key, c.getImageData(0, 0, 256, 256));
      }).catch(() => demTiles.delete(key));
    }

    // ----- Debug: per-frame brightness, to find flashes -----

    const probe = document.createElement("canvas");
    probe.width = 32; probe.height = 24;
    const probeCtx = probe.getContext("2d", { willReadFrequently: true });
    const debugLog = (window.simCameraDebug = []);
    let loading = {};
    if (debug) map.on("dataloading", (e) => { if (e.tile) loading[e.sourceId] = (loading[e.sourceId] || 0) + 1; });
    function logFrame(u, cam) {
      map.once("render", () => {
        probeCtx.drawImage(map.getCanvas(), 0, 0, 32, 24);
        const d = probeCtx.getImageData(0, 0, 32, 24).data;
        let sum = 0, bare = 0;
        for (let i = 0; i < d.length; i += 4) {
          sum += d[i] + d[i + 1] + d[i + 2];
          // The background colour (#b8ad97): ground with no imagery drawn on it.
          if (Math.abs(d[i] - 184) < 6 && Math.abs(d[i + 1] - 173) < 6 && Math.abs(d[i + 2] - 151) < 6) bare++;
        }
        debugLog.push({ t: Math.round(performance.now()), lum: Math.round(sum / (d.length / 4) / 3), bare: +(bare / (d.length / 4)).toFixed(2),
          zoom: +cam.zoom.toFixed(2), pitch: +cam.pitch.toFixed(1), bearing: Math.round(cam.bearing), tileZoom: map.transform.tileZoom,
          allLoaded: map.areTilesLoaded(), mode, loading });
        loading = {};
        if (debugLog.length > 600) debugLog.shift();
      });
    }

    // ----- HUD -----

    function drawHud(u, heading, altitude, ground, now) {
      const w = hud.clientWidth, h = hud.clientHeight, dpr = window.devicePixelRatio || 1;
      if (hud.width !== Math.round(w * dpr) || hud.height !== Math.round(h * dpr)) {
        hud.width = Math.round(w * dpr);
        hud.height = Math.round(h * dpr);
      }
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      ctx.clearRect(0, 0, w, h);
      const green = ir ? "rgba(255,255,255,0.92)" : "rgba(120,255,150,0.92)";
      ctx.strokeStyle = green;
      ctx.fillStyle = green;
      ctx.lineWidth = 1.4;
      ctx.font = "12px Consolas, 'Courier New', monospace";
      ctx.shadowColor = "rgba(0,0,0,0.8)";
      ctx.shadowBlur = 3;

      // Crosshair with a gap, and corner brackets.
      const cx = w / 2, cy = h / 2, g = 10, l = 28;
      line(cx - l, cy, cx - g, cy); line(cx + g, cy, cx + l, cy); line(cx, cy - l, cx, cy - g); line(cx, cy + g, cx, cy + l);
      const bw = w * 0.16, bh = h * 0.16;
      for (const [sx, sy] of [[-1, -1], [1, -1], [-1, 1], [1, 1]]) {
        line(cx + sx * bw, cy + sy * bh, cx + sx * (bw - 14), cy + sy * bh);
        line(cx + sx * bw, cy + sy * bh, cx + sx * bw, cy + sy * (bh - 14));
      }

      // Heading tape along the top.
      const tapeW = w * 0.5, x0 = cx - tapeW / 2;
      for (let d = -30; d <= 30; d += 5) {
        const deg = Math.round(heading) + d - (Math.round(heading) % 5);
        const x = cx + ((deg - heading) / 30) * (tapeW / 2);
        if (x < x0 || x > x0 + tapeW) continue;
        const major = ((deg % 30) + 30) % 30 === 0;
        line(x, 14, x, major ? 24 : 19);
        if (major) text(label(deg), x, 36, "center");
      }
      line(cx, 8, cx - 5, 2); line(cx, 8, cx + 5, 2);

      const utc = new Date().toISOString().slice(11, 19) + "Z";
      text(`${tail}  ${ir ? "IR WH" : "EO"}  ${mode}`, 12, 20, "left");
      if (Math.floor(now / 600) % 2 === 0) { ctx.beginPath(); ctx.arc(w - 64, 15, 4, 0, Math.PI * 2); ctx.fillStyle = "#ff4040"; ctx.fill(); ctx.fillStyle = green; }
      text("REC", w - 12, 20, "right");
      text(`ALT ${Math.round(u.altitudeFt)} FT MSL`, w - 12, cy - 8, "right");
      text(`AGL ${Math.round((altitude - ground) / FT)} FT`, w - 12, cy + 8, "right");
      text(`GS ${u.speedKts} KT`, 12, cy - 8, "left");
      text(`HDG ${String(Math.round((heading + 360) % 360)).padStart(3, "0")}`, 12, cy + 8, "left");
      const zoom = `${u.payloadZoom.toFixed(1)}x  HFOV ${u.payloadHfovDeg.toFixed(1)}°`;
      text(zoom, 12, h - 28, "left");
      text(utc, 12, h - 12, "left");
      if (look) {
        text(`${look.center.lat.toFixed(5)}N ${look.center.lng.toFixed(5)}E`, w - 12, h - 28, "right");
        text(`SLR ${Math.round(look.range)} M`, w - 12, h - 12, "right");
      }
      ctx.shadowBlur = 0;
    }
    function line(x1, y1, x2, y2) { ctx.beginPath(); ctx.moveTo(x1, y1); ctx.lineTo(x2, y2); ctx.stroke(); }
    function text(s, x, y, align) { ctx.textAlign = align; ctx.fillText(s, x, y); }
    function label(deg) {
      const d = ((deg % 360) + 360) % 360;
      return { 0: "N", 90: "E", 180: "S", 270: "W" }[d] ?? String(d / 10).padStart(2, "0");
    }

    return { show, hide, setIr, get ir() { return ir; }, map };
  };

  // ----- Geometry helpers -----

  function offset(lng, lat, headingDeg, meters) {
    const h = (headingDeg * Math.PI) / 180;
    const dLat = (meters * Math.cos(h)) / 111195;
    const dLng = (meters * Math.sin(h)) / (111195 * Math.cos((lat * Math.PI) / 180));
    return [lng + dLng, lat + dLat];
  }
  function distance(a, b, height) {
    const dx = (b[0] - a[0]) * 111195 * Math.cos((a[1] * Math.PI) / 180);
    const dy = (b[1] - a[1]) * 111195;
    return Math.hypot(dx, dy, height);
  }
  function square(lng, lat, half) {
    const dLat = half / 111195, dLng = half / (111195 * Math.cos((lat * Math.PI) / 180));
    return { type: "Feature", properties: {}, geometry: { type: "LineString", coordinates: [[lng - dLng, lat - dLat], [lng + dLng, lat - dLat], [lng + dLng, lat + dLat], [lng - dLng, lat + dLat], [lng - dLng, lat - dLat]] } };
  }
  function point(lng, lat, properties) { return { type: "Feature", properties, geometry: { type: "Point", coordinates: [lng, lat] } }; }
  function collection(features) { return { type: "FeatureCollection", features }; }
  function empty() { return collection([]); }
})();
