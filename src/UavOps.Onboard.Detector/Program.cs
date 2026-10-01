using UavOps.Onboard.Contracts;
using UavOps.Onboard.Detector;
using UavOps.Onboard.Detector.Autonomy;
using UavOps.Onboard.Detector.Link;
using UavOps.Onboard.Detector.Perception;

// The onboard detection service: the process that runs next to the camera on the UAV (a Jetson
// Orin Nano in the target build). The aircraft tells it what to look for (PUT /tasks/{tail}); it
// pulls the camera frames, asks a vision-language model about each, places what it finds on the
// ground, and posts each new object back. See README.md.

var builder = WebApplication.CreateBuilder(args);

var vlm = builder.Configuration.GetSection(VlmOptions.SectionName).Get<VlmOptions>() ?? new VlmOptions();
var detector = builder.Configuration.GetSection(DetectorOptions.SectionName).Get<DetectorOptions>() ?? new DetectorOptions();
var perception = builder.Configuration.GetSection(PerceptionOptions.SectionName).Get<PerceptionOptions>() ?? new PerceptionOptions();
// Model files live beside the app on the device (../models), so relative paths are the app's.
if (!string.IsNullOrWhiteSpace(perception.EnginePath))
    perception.EnginePath = Path.GetFullPath(perception.EnginePath, builder.Environment.ContentRootPath);
if (!string.IsNullOrWhiteSpace(perception.LabelsPath))
    perception.LabelsPath = Path.GetFullPath(perception.LabelsPath, builder.Environment.ContentRootPath);
if (string.IsNullOrWhiteSpace(vlm.Model))
    throw new InvalidOperationException("Missing 'Vlm:Model' configuration: the vision model to ask, as its server names it.");

builder.Services.AddSingleton(vlm);
builder.Services.AddSingleton(detector);
builder.Services.AddHttpClient<OpenAiVisionModel>(c => c.Timeout = TimeSpan.FromSeconds(vlm.TimeoutSeconds));
builder.Services.AddSingleton<IVisionModel>(sp => sp.GetRequiredService<OpenAiVisionModel>());
// Frame pulls long-poll, so their timeout must outlast the wait.
builder.Services.AddHttpClient<HttpSurveyFrameSource>(c => c.Timeout = TimeSpan.FromMilliseconds(detector.FramePollWaitMs + 15000));
builder.Services.AddSingleton<IFrameSource>(sp => sp.GetRequiredService<HttpSurveyFrameSource>());
builder.Services.AddHttpClient<HttpZoomCamera>(c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddSingleton<IZoomCamera>(sp => sp.GetRequiredService<HttpZoomCamera>());
// The link to the aircraft: over the ground agent's onboard hub (SignalR, this computer dials out),
// or plain HTTP both ways for a local dev run. Video is read from the aircraft directly either way.
var link = builder.Configuration.GetSection(AircraftLinkOptions.SectionName).Get<AircraftLinkOptions>() ?? new AircraftLinkOptions();
builder.Services.AddSingleton(link);
if (link.UsesSignalR)
{
    if (string.IsNullOrWhiteSpace(link.HubUrl))
        throw new InvalidOperationException("Link:Mode is SignalR but Link:HubUrl (the ground agent's onboard hub) is blank.");
    builder.Services.AddSingleton<SignalRAircraftLink>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<SignalRAircraftLink>());
    builder.Services.AddSingleton<IDetectionSink>(sp => sp.GetRequiredService<SignalRAircraftLink>());
}
else
{
    builder.Services.AddHttpClient<HttpDetectionSink>(c => c.Timeout = TimeSpan.FromSeconds(10));
    builder.Services.AddSingleton<IDetectionSink>(sp => sp.GetRequiredService<HttpDetectionSink>());
}
// The fast every-frame pipeline: a TensorRT detector where there is one (the Jetson), plus the
// payload control, target reports and the verifier it uses. Without an engine every search runs on
// the vision-model pipeline.
builder.Services.AddSingleton(perception);
if (perception.Enabled)
{
    builder.Services.AddSingleton(_ => new TensorRtDetector(perception.EnginePath, perception.LabelsPath));
    builder.Services.AddSingleton<IObjectDetector>(sp => perception.TileColumns * perception.TileRows > 1
        ? new TiledDetector(sp.GetRequiredService<TensorRtDetector>(), perception.TileColumns, perception.TileRows, perception.TileOverlapPx)
        : sp.GetRequiredService<TensorRtDetector>());
}
builder.Services.AddSingleton<IVerifier, VlmVerifier>();
if (link.UsesSignalR)
{
    builder.Services.AddSingleton<IPayloadControl>(sp => sp.GetRequiredService<SignalRAircraftLink>());
    builder.Services.AddSingleton<ITrackSink>(sp => sp.GetRequiredService<SignalRAircraftLink>());
}
else
{
    builder.Services.AddHttpClient<HttpPayloadControl>(c => c.Timeout = TimeSpan.FromSeconds(5));
    builder.Services.AddSingleton<IPayloadControl>(sp => sp.GetRequiredService<HttpPayloadControl>());
    builder.Services.AddHttpClient<HttpTrackSink>(c => c.Timeout = TimeSpan.FromSeconds(5));
    builder.Services.AddSingleton<ITrackSink>(sp => sp.GetRequiredService<HttpTrackSink>());
}
builder.Services.AddSingleton<SearchTaskRegistry>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SearchTaskRegistry>());

var app = builder.Build();
app.Logger.LogInformation(perception.Enabled
    ? "Fast pipeline on: detector engine {Engine}; verifier {Model} at {Endpoint}."
    : "No detector engine ({Engine}): every search runs on the vision-model pipeline ({Model} at {Endpoint}).",
    perception.EnginePath, vlm.Model, vlm.Endpoint);

app.MapPut("/tasks/{tail}", (string tail, SearchTask task, SearchTaskRegistry tasks) =>
{
    if (!string.Equals(tail, task.TailNumber, StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest("The task's tailNumber doesn't match the URL.");
    if (string.IsNullOrWhiteSpace(task.Prompt) || string.IsNullOrWhiteSpace(task.FrameSourceUrl) || string.IsNullOrWhiteSpace(task.DetectionCallbackUrl))
        return Results.BadRequest("prompt, frameSourceUrl and detectionCallbackUrl are required.");
    tasks.Start(task);
    return Results.NoContent();
});

app.MapDelete("/tasks/{tail}", (string tail, SearchTaskRegistry tasks) =>
    tasks.Stop(tail) ? Results.NoContent() : Results.NotFound());

app.MapGet("/tasks", (SearchTaskRegistry tasks) => tasks.Statuses());

// Diagnostic: the detector on one JPEG (the request body), with timings - for measuring a model on
// the simulator's own frames. ?minScore= (default the pipeline's), ?all=true for every class, not
// only vehicles and people.
app.MapPost("/detect", async (HttpRequest request, IServiceProvider services, double? minScore, bool? all) =>
{
    if (services.GetService<IObjectDetector>() is not { } objects)
        return Results.Problem("No detector engine on this machine (Perception:EnginePath).", statusCode: 503);
    using var body = new MemoryStream();
    await request.Body.CopyToAsync(body);
    var watch = System.Diagnostics.Stopwatch.StartNew();
    using var image = FrameDecoder.Decode(body.ToArray(), objects.InputSize);
    if (image is null)
        return Results.BadRequest("The body isn't a readable image.");
    var decodeMs = watch.Elapsed.TotalMilliseconds;
    watch.Restart();
    var found = objects.Detect(image, minScore ?? perception.MinScore);
    var detectMs = watch.Elapsed.TotalMilliseconds;
    // Materialized here: serialized lazily, the colours would be read from the bitmap after it's
    // disposed (a native use-after-free that took the process down).
    var shown = found.Where(o => all == true || ObjectClasses.IsTracked(o.Class))
        .OrderByDescending(o => o.Score)
        .Select(o => new { o.Class, score = Math.Round(o.Score, 3), colour = ColourNamer.Name(image, o.Box), o.Box })
        .ToList();
    return Results.Ok(new
    {
        detector = objects.Name,
        image.Width,
        image.Height,
        decodeMs = Math.Round(decodeMs, 1),
        detectMs = Math.Round(detectMs, 1),
        stagesMs = objects is IDetectorTiming trt
            ? new { prepare = Math.Round(trt.LastTiming.Prepare, 1), infer = Math.Round(trt.LastTiming.Infer, 1), decode = Math.Round(trt.LastTiming.Decode, 1) }
            : null,
        objects = shown
    });
});

app.MapGet("/healthz", async (OpenAiVisionModel model, VlmOptions options, SearchTaskRegistry tasks, IServiceProvider services, CancellationToken ct) =>
    Results.Ok(new
    {
        status = "ok",
        detector = services.GetService<IObjectDetector>()?.Name,
        link = link.UsesSignalR ? new { mode = "SignalR", hub = link.HubUrl, connected = services.GetService<SignalRAircraftLink>()?.Connected } : (object)new { mode = "Http" },
        model = options.Model,
        modelEndpoint = options.Endpoint,
        modelReachable = await model.IsReachableAsync(ct),
        activeTasks = tasks.Statuses().Count
    }));

app.Run();
