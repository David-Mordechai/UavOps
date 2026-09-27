# UavOps.Onboard.Detector

The onboard detection service: the process that runs next to the payload camera on the UAV (a
Jetson Orin Nano 8GB in the target build). The aircraft tells it what to look for; it pulls the
camera's survey frames, asks a vision-language model (VLM) about each, places what it finds on the
ground, and posts each new object back. In development the "aircraft" is `UavOps.Simulator`, which
renders the camera video from the offline map.

```bash
dotnet run --project src/UavOps.Onboard.Detector     # :5280
```

The simulator uses it when `Simulator:Detector` is `Onboard` (the default); `Simulated` falls back
to tag matching with no model.

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
| `PUT /tasks/{tail}` | Start (or replace) a search: `SearchTask` - prompt, mission, `frameSourceUrl`, `fromSeq`, `detectionCallbackUrl`, `zoomUrl`. |
| `DELETE /tasks/{tail}` | Stop it. |
| `GET /tasks` | What each search is doing: `analyzedThroughSeq` (every frame up to it done), frames, detections, last latency, last error. |
| `GET /healthz` | Up, and whether the model server answers. |

What it calls on the aircraft (contracts in `src/UavOps.Onboard.Contracts`):

- `GET {frameSourceUrl}/next?after=N&waitMs=` - the next survey frame, JPEG, telemetry in
  `X-Frame-*` headers (the dev stand-in for MISB KLV); 204 if none came in time.
- `GET {zoomUrl}?lat=&lng=&widthMeters=&pixels=` - a zoom close-up of a ground point.
- `POST {detectionCallbackUrl}` - an `OnboardDetection` (the fleet `DetectionReport` fields plus
  the box, frame and model latency).

The aircraft reports a search complete only once `analyzedThroughSeq` reaches its last frame.

## Configuration (`appsettings.json`)

- `Vlm`: `Endpoint` (OpenAI-compatible `/v1`), `Model`, `TimeoutSeconds`, `MaxTokens`,
  `DisableThinking` (sends `chat_template_kwargs.enable_thinking=false`).
- `Detector`: `MaxConcurrentFrames`, `MaxCandidatesPerFrame`, `TrackRadiusMeters`,
  `SameObjectMeters`, `ZoomPixels`, `MinZoomWidthMeters`/`MaxZoomWidthMeters` (vehicles),
  `MinStructureZoomWidthMeters`/`MaxStructureZoomWidthMeters` (everything else, 30/40),
  `FramePollWaitMs`.

Dev points at the GX10's `nvidia/Qwen3.6-35B-A3B-NVFP4` (vLLM, thinking off). Measured there:
~1-2 s per candidate question, ~0.5 s per close-up; with a busy street a frame has ~10-15
candidates, and the service runs ~1.9 s per frame with 6 frames in flight - slower than the 1×
camera (~1 frame/s), so reports trail the aircraft and drain after the route.

## On the Jetson Orin Nano 8GB

```bash
dotnet publish src/UavOps.Onboard.Detector -c Release -r linux-arm64 --self-contained
```

Run a small VLM beside it behind an OpenAI-compatible server (llama.cpp's `llama-server` or vLLM)
and point `Vlm:Endpoint`/`Vlm:Model` at it; nothing else changes. Candidates are small quantized
Qwen VL models (2-4B). Which one fits in 8 GB alongside the service, how fast it is and how
accurate it is on these two questions have not been measured yet - do that on the device before
relying on it. A real camera would replace `HttpSurveyFrameSource` (`IFrameSource`: RTSP/GStreamer
plus KLV telemetry) and `HttpZoomCamera` (`IZoomCamera`: the gimbal's own control).
