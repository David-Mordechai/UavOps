using UavOps.Onboard.Contracts;
using UavOps.Onboard.Detector;

// The onboard detection service: the process that runs next to the camera on the UAV (a Jetson
// Orin Nano in the target build). The aircraft tells it what to look for (PUT /tasks/{tail}); it
// pulls the camera frames, asks a vision-language model about each, places what it finds on the
// ground, and posts each new object back. See README.md.

var builder = WebApplication.CreateBuilder(args);

var vlm = builder.Configuration.GetSection(VlmOptions.SectionName).Get<VlmOptions>() ?? new VlmOptions();
var detector = builder.Configuration.GetSection(DetectorOptions.SectionName).Get<DetectorOptions>() ?? new DetectorOptions();
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
builder.Services.AddHttpClient<HttpDetectionSink>(c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton<IDetectionSink>(sp => sp.GetRequiredService<HttpDetectionSink>());
builder.Services.AddSingleton<SearchTaskRegistry>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SearchTaskRegistry>());

var app = builder.Build();

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

app.MapGet("/healthz", async (OpenAiVisionModel model, VlmOptions options, SearchTaskRegistry tasks, CancellationToken ct) =>
    Results.Ok(new
    {
        status = "ok",
        model = options.Model,
        modelEndpoint = options.Endpoint,
        modelReachable = await model.IsReachableAsync(ct),
        activeTasks = tasks.Statuses().Count
    }));

app.Run();
