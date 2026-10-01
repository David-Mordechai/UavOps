# UavOps.Onboard.Detector

The onboard service: the process that runs next to the payload camera on the UAV, on a **Jetson
Orin Nano Super 8 GB** (`192.168.1.154` in dev). Everything onboard runs there, never on the ground:
the aircraft tells it what to look for, and it finds the target, drives the payload onto it and
reports where it is. In development the "aircraft" is `UavOps.Simulator` on the dev PC, which
renders the camera video; the simulator's `Simulator:OnboardUrl` says where the onboard computer is.

```powershell
./scripts/deploy-onboard.ps1        # publish, copy, build what's missing on the device, (re)start
```

The simulator uses it when `Simulator:Detector` is `Onboard` (the default); `Simulated` falls back
to tag matching with no model. It still runs on a PC (`dotnet run`, :5280) for offline dev: with no
detector engine every search uses the vision-model pipeline below.

## Two pipelines

- **The fast pipeline (`Autonomy/TrackingRunner`)**: used when there's a detector engine, the
  aircraft offers live video, and the target is something the detector knows (a vehicle or a
  person, `Perception/TargetSpec`). No language model decides anything in it - see below.
- **The vision-model pipeline (`SearchTaskRunner`)**: every survey frame to a VLM, two questions
  each (below). Slow (~2 s a frame on the GX10's 35B model) but it finds anything that can be
  named, so non-vehicle targets (pylons, bridges) stay on it. It never tracks.

## The fast pipeline: perception -> executive -> payload

The shape of onboard autonomy stacks such as Shield AI's Hivemind (perception, then an executive,
then actions), not a small copy of the ground agent: a language model is far too slow for a control
loop on this device (seconds per answer) and isn't deterministic where safety matters. BrainAgent,
on the ground, has already turned the operator's words into a structured mission.

Per frame of the live video (10 fps, newest first - a backlog is skipped, not worked through):

1. **Detect** every vehicle and person: D-FINE-X (Objects365, Apache-2.0: its classes include van,
   SUV and pickup, not just "car"), a TensorRT FP16 engine through `native/libuavtrt.so`
   (`Perception/ObjectDetector.cs`), run on **2x2 overlapping tiles** of the full-resolution frame
   (`Perception/TiledDetector.cs`, `Perception:TileColumns/TileRows/TileOverlapPx`; 1x1 is the
   whole frame): a 100 m search frame shrunk to 640 px put a car at ~25 px, and the red car was found
   in 2 of 14 frames, scoring 0.24 (under the 0.35 that starts a track); on tiles it scores 0.6-0.74.
   Each tile is read in place on the GPU (pointer + the frame's row stride, no copy); an object two
   tiles both see is reported once. The frame is decoded at the size that needs (`FrameDecoder`),
   and resized and normalized on the GPU (`native/preprocess.cu`).
2. **Place** each on the ground with the frame's telemetry (`CameraModel`) and read its **colour**
   from its pixels (`ColourNamer`, no model).
3. **Track** on the ground plane (`GroundTracker`): a constant-velocity Kalman filter per object,
   ByteTrack-style matching (confident detections first, weak ones only continue a track),
   gated by the filter's own uncertainty; confirmed after 3 hits, coasting when missed in view, lost
   after `CoastSeconds` of looking where it should be (time out of view, or before the lock while
   the close-up is checked and the payload slews, doesn't count). Ids are `T-n`.
4. **Executive** (`TrackingRunner.Step`): a confirmed track whose class and colour could be the
   target gets **one** zoomed close-up checked by the verifier (`Autonomy/Verifier.cs`: the small VLM
   names colour and type, `TargetMatcher` compares - one question, ~1.6 s, never per frame). A
   verified target is reported found (the ordinary detection). In a find-and-track mission
   (`SearchTask.Track`) the executive then locks the payload on it (`IPayloadControl`: point, zoom so
   ~60 m of ground shows), keeps it centred on the predicted position, and reports it
   (`TargetTrackReport`, ~1/s and on every state change). Lost, it looks where the target should be
   and takes a new track there back under the old id only after the verifier says it's the target
   again. "Where it should be" is a real search (`LostTargetSearch`): the payload zooms out (160 m)
   and steps through look points covering a disc around the dead-reckoned position, the disc growing
   with how far the target could have driven; the aircraft circles the moving search centre. After
   `ReacquireSeconds` (90) the target is given up (`Released`) and the search route resumes.
   The locked target's track only accepts sightings that could be the target (any vehicle class -
   zoomed in, the red car read "Truck" and "Bus" as often as "Car" - of the target's colour), never
   moves faster than `MaxSpeedMps`, and never jumps further than the target could have driven:
   without that, a locked car's track once slid 300 m onto other vehicles while still "tracking".
   It is also never out-competed for its own sightings: it is matched first, with a 99.9% gate, and
   a track of its sightings under another id (one sighting just outside the gate used to start one,
   which then took every later sighting while the target coasted out) is folded back into it. With
   these three fixes (2026-10-01) 997 followed the moving red car for 8+ minutes at 5x sim speed
   with no loss; before, every lock was lost within seconds.

The aircraft (simulator) turns the reports into the fleet contract: the lock, a loss and a regain as
mission events (`Tracking`, `TargetLost`, `TargetRegained`), the position as detection updates under
the track id; and its autopilot circles the reported position.

**Measured (2026-09-30, on the device, 25 W mode, the simulator's ZoneA scene):**

| | |
|---|---|
| Detector engine (trtexec) | D-FINE-X 43 ms / frame (23 fps); D-FINE-S 13 ms |
| Detector choice, search-scale frames of the 3 parked cars (FP32 reference) | S 13/18, M 5/18, L 15/18, **X 18/18** |
| In the loop, GPU at idle clock | X: decode ~13 ms + detect ~67 ms, 8.8 fps (S: ~35 ms, 10 fps) |
| Verifier, Qwen3-VL-2B Q4 (llama.cpp) | ~1.6 s per close-up; named the white van "van" (SmolVLM2-2.2B and Qwen2.5-VL-3B said "car") |
| Memory, whole onboard stack | 4.3 GB of 7.6 |
| Find and track the red car (12 km/h) | found (on a later pass of the repeating search), verified, locked; reported within 7-12 m of its true position over minutes; lost, searched for, re-verified and regained under the same id |
| Tracker confirmation | 3 hits, not in a row (a new track survives misses for 1.5 s): with S, dropping a new track at its first miss meant the red car was seen again and again during a pass and never confirmed |

Learned the hard way:
- The plain HuggingFace export's **FP16 engine is numerically broken**: its hand-built layer norms
  overflow in FP16 (cats scored 0.30 instead of 0.94; no vehicle found at all).
  `scripts/onboard/fuse_layernorm.py` fuses them into ONNX `LayerNormalization` first; the FP16
  engine then matches FP32 exactly, at the same speed.
- The GPU's clock is not the limit: 306 MHz was read while idle, and under the loop the governor
  raises it by itself (98% busy). Pinned at 1020 MHz (MAXN_SUPER + jetson_clocks, 2026-10-01) the
  loop ran no faster - and the device ran warmer idle - so it was undone.
- Detector recall on top-down vehicles is the weak point: Objects365 is ground-level photos. The
  frame now runs as 2x2 tiles (above); next, fine-tune on aerial data (VisDrone).
- Tiles cost 4 inferences a frame, ~40 ms each with D-FINE-X (161 ms a frame): ~3 fps per UAV with
  two searching (the GPU 98% busy, ~73 °C, 19.5 W; idle 6.8 W). Enough: the red car was found on the
  first pass in both runs. D-FINE-S on tiles is 3.2x faster (50 ms a frame) but lost the white
  vehicles (paused scene, 10 frames each, track-start: white van 2/10, white car 0/10; X 10/10 on all
  four), so X stays. A batch-4 engine (all tiles in one inference) is the remaining speed-up.

## How a frame is searched

Two questions per frame, both to the same OpenAI-compatible VLM (see `DetectionPrompt` for why one
question isn't enough - measured, not guessed):

1. **Candidates** (whole frame, for recall): every object of the target's colour and kind, whatever
   its exact type. Asked only for "things like the target", the model listed the single most
   target-like vehicle and stopped (1 in 5 on the white-pickup frame); asked for everything of that
   colour, 5 in 5.
2. **Close-up** (each candidate): the aircraft's payload zooms onto it (`ZoomUrl`, a 320 px image of
   14-40 m of ground) and the model names its colour and type. The name is matched against the
   target word by word (`TargetMatcher`) - the model is never asked "is this the target?", which it
   answers yes too readily. Without a zoom, the candidate is cropped from the frame instead
   (measured: pickups were named "car" 6/6 from crops, correctly 6/6 from zoom close-ups).

**Anything that isn't a vehicle** (a pylon, a bridge, a building - `TargetKind`) is searched
differently, because it has no colour to filter by and many names ("power grid antenna" is an
electricity pylon). Measured on ZoneB's real photo:

1. Once per search, the model is asked what the operator most likely means, by its usual name
   (text only: "power grid antenna" -> "transmission tower").
2. **Candidates**: anything in the frame that could be it, of any colour.
3. **Close-up**, 30-40 m across: what the object at the centre is, **without** being told the
   target. Told the target, the model called a sign gantry a "power grid antenna" and a road a
   "road bridge"; untold, it named them right. Wider close-ups (100-180 m) made it name whatever
   stood out ("road bridge" for both pylons). The question says what a road bridge is: from above,
   highway reads as bridge otherwise.
4. **Compare** that name with what the operator means (text only, cached per name). Taken
   literally the model said an electricity pylon is not a "power grid antenna"; against the
   interpretation, 131/132 right over 11 phrasings x 12 seen objects, cables and "cell tower"
   correctly not matching.
5. **A second look** at the other width (30 m or 40 m) must agree: false hits changed their answer
   between widths (cables: pylon at 40 m, power line at 30 m), real objects didn't.

On 15 spots of ZoneB checked by eye: both pylons and the road bridge found, 0 false hits; live
searches found exactly those (before: 0 found - the vehicle prompts proposed only vehicles).

A match's box centre is placed on the ground with that frame's own telemetry (`CameraModel`,
nadir, image top = heading). Survey frames overlap, so an object examined up close is remembered
by position (`SameObjectMeters`) and not zoomed on again, and a match near one already reported
(`TrackRadiusMeters`) is the same object and isn't reported twice.

## API

| | |
|---|---|
| `PUT /tasks/{tail}` | Start (or replace) a search: `SearchTask` - prompt, mission, `frameSourceUrl`, `fromSeq`, `detectionCallbackUrl`, `zoomUrl`; for the fast pipeline also `track`, `videoSourceUrl`, `payloadUrl`, `trackCallbackUrl`. |
| `DELETE /tasks/{tail}` | Stop it. |
| `GET /tasks` | What each search is doing: `analyzedThroughSeq` (every frame up to it done), frames, detections, last latency, last error; for the fast pipeline its `phase` (Searching/Verifying/Tracking/Reacquiring), the target's track id and per-stage timing. |
| `GET /healthz` | Up, the detector engine, and whether the verifier model answers. |
| `POST /detect` | Diagnostic: the detector on one JPEG (the body), with per-stage timings. `?minScore=`, `?all=true` for every class. |

What it calls on the aircraft (contracts in `src/UavOps.Onboard.Contracts`):

- `GET {frameSourceUrl}/next?after=N&waitMs=` - the next survey frame, JPEG, telemetry in
  `X-Frame-*` headers (the dev stand-in for MISB KLV); 204 if none came in time.
- `GET {zoomUrl}?lat=&lng=&widthMeters=&pixels=` - a zoom close-up of a ground point.
- `POST {detectionCallbackUrl}` - an `OnboardDetection` (the fleet `DetectionReport` fields plus
  the box, frame and model latency).
- Fast pipeline: `GET {videoSourceUrl}/next?after=N` (the newest live-video frame) and
  `{videoSourceUrl}/zoom` (close-ups, `?seq=` a video frame); `POST {payloadUrl}/point|zoom|release`;
  `POST {trackCallbackUrl}` - a `TargetTrackReport`.

The aircraft reports a search complete only once `analyzedThroughSeq` reaches its last frame.

## Configuration (`appsettings.json`)

- `Vlm`: `Endpoint` (OpenAI-compatible `/v1`), `Model`, `TimeoutSeconds`, `MaxTokens`,
  `DisableThinking` (sends `chat_template_kwargs.enable_thinking=false`).
- `Perception` (the fast pipeline): `EnginePath`, `LabelsPath` (relative to the app), score
  thresholds (`MinScore`, `HighScore`), tracking (`CoastSeconds`, `TrackMemorySeconds`), close-ups,
  payload (`TrackGroundWidthMeters` 60, `ReacquireGroundWidthMeters` 160, pointing deadband and
  lead), `ReportIntervalSeconds`, reacquisition radius. `appsettings.Jetson.json` holds the
  device's values (the `Jetson` environment, set by the service unit).
- `Detector`: `MaxConcurrentFrames`, `MaxCandidatesPerFrame`, `TrackRadiusMeters`,
  `SameObjectMeters`, `ZoomPixels`, `MinZoomWidthMeters`/`MaxZoomWidthMeters` (vehicles),
  `MinStructureZoomWidthMeters`/`MaxStructureZoomWidthMeters` (everything else, 30/40),
  `FramePollWaitMs`.

Dev points at the GX10's `nvidia/Qwen3.6-35B-A3B-NVFP4` (vLLM, thinking off). Measured there:
~1-2 s per candidate question, ~0.5 s per close-up; with a busy street a frame has ~10-15
candidates, and the service runs ~1.9 s per frame with 6 frames in flight - slower than the 1×
camera (~1 frame/s), so reports trail the aircraft and drain after the route.

## On the Jetson Orin Nano Super (JetPack 6, TensorRT 10.3, CUDA 12.6)

`scripts/deploy-onboard.ps1` (device and user default to the host in `Simulator:OnboardUrl`, SSH
with a key) does it all, idempotently:

- publishes the service self-contained for linux-arm64 and copies it to `~/uavops-onboard/app`;
- fuses the detector's layer norms on the dev PC (`fuse_layernorm.py`; the device has no pip) and
  uploads `~/uavops-onboard/models/dfine_s_obj365.ln.onnx`;
- on the device (`scripts/onboard/setup-onboard.sh`): builds the FP16 engine with `trtexec` (~20
  min, once), builds `libuavtrt.so` (`native/build.sh`: nvcc + g++ against TensorRT), builds
  llama.cpp with CUDA (once), downloads the verifier (Qwen3-VL-2B Q4 + its projector), and installs
  two **systemd user services**: `uavops-vlm` (llama-server on 127.0.0.1:8080) and `uavops-onboard`
  (this service on 0.0.0.0:5280). Logs: `~/uavops-onboard/onboard.log`, `vlm.log`. Linger is on
  for the user, so both start at boot.

## The link to the aircraft (`Link/`, `Link:Mode`)

**SignalR (on the Jetson):** the onboard computer dials the ground agent's onboard hub
(`Link:HubUrl`, e.g. `http://192.168.1.157:5262/onboardHub`) and the aircraft (simulator) dials it
too; the agent only routes `(tail, kind, payload)` messages between them (`OnboardLink`,
`UavOps.Agent/Hubs/OnboardLinkHub.cs`). Tasks come in, detections, target reports, payload
commands and a status every 2 s go out. No port needs opening on the Jetson. The camera video and
close-ups are still read straight from the aircraft (port 5270 on the PC). **Http** (a local dev
run): the aircraft calls `/tasks`, reports go to its callback URLs.

If the onboard computer restarts (or connects after a search began), the simulator sees from its
status that it lacks the search, sends the task again, and a UAV that was following a target goes
back to its search route (the onboard computer starts that search over, with no target). A
detection says which frame stream its frame number belongs to (`OnboardDetection.FromVideo`); the
simulator keeps that frame when the detection arrives (`/api/onboard/snapshots/{id}.jpg`), since
the live video buffer only holds a few seconds.

The PC must accept the Jetson's connections: 5270 (video) and 5262 (the agent), and the agent must
listen on the LAN (`--urls http://0.0.0.0:5262`). Windows keeps a per-program inbound rule: if the
Jetson can't connect, check `Get-NetFirewallRule -DisplayName "UavOps.Agent"` - on the dev PC it
was once a Block rule (a dismissed firewall prompt).

A real camera would replace `HttpSurveyFrameSource` (`IFrameSource`: RTSP/GStreamer plus KLV
telemetry) and `HttpZoomCamera`/`HttpPayloadControl` (the gimbal's own control).
