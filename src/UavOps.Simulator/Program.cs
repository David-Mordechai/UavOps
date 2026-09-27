using System.Text.Json.Nodes;
using Microsoft.AspNetCore.StaticFiles;
using UavOps.Agent.Mission;
using UavOps.Simulator;

var builder = WebApplication.CreateBuilder(args);

var simOptions = builder.Configuration.GetSection(SimOptions.SectionName).Get<SimOptions>() ?? new SimOptions();
var scenarioOptions = builder.Configuration.GetSection(ScenarioOptions.SectionName).Get<ScenarioOptions>() ?? new ScenarioOptions();

builder.Services.AddSingleton(simOptions);
builder.Services.AddSingleton(new ScenarioStore(scenarioOptions));
builder.Services.AddSingleton<SimulatedDetector>();
builder.Services.AddSingleton<IOnboardDetector>(sp => sp.GetRequiredService<SimulatedDetector>());
builder.Services.AddSingleton<SimFleet>();
builder.Services.AddSingleton<SimulatorCommandHandler>();
builder.Services.AddSingleton<FleetConnectionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FleetConnectionService>());
builder.Services.AddSingleton<FlightTickService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FlightTickService>());
// Read-only here: the same zones McpMoav plans routes over, drawn on the map.
builder.Services.AddSingleton<IAoiZoneStore>(new SqliteAoiZoneStore(MissionOptions.ResolveDatabasePath(simOptions.AoiDatabasePath)));
builder.Services.AddSignalR();

var app = builder.Build();

app.UseDefaultFiles();

// The offline map's tile archive and glyph ranges have extensions static files doesn't know,
// and would otherwise 404. Range requests (how MapLibre reads a .pmtiles file) are on by default.
var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".pmtiles"] = "application/octet-stream";
contentTypes.Mappings[".pbf"] = "application/x-protobuf";
app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = contentTypes });

app.MapHub<SimHub>("/simHub");

app.MapGet("/api/state", (FlightTickService tick) => Results.Json(tick.Snapshot()));

app.MapGet("/api/zones", async (IAoiZoneStore zones, CancellationToken ct) =>
{
    var features = new JsonArray();
    foreach (var zone in await zones.ListAsync(ct))
    {
        features.Add(new JsonObject
        {
            ["type"] = "Feature",
            ["properties"] = new JsonObject { ["name"] = zone.Name },
            ["geometry"] = JsonNode.Parse(GeoJsonPolygon.Write(zone.Vertices))
        });
    }
    return Results.Json(new JsonObject { ["type"] = "FeatureCollection", ["features"] = features });
});

app.MapGet("/api/objects", (ScenarioStore scenario) => scenario.All());

app.MapPost("/api/objects", (NewObject body, ScenarioStore scenario) =>
    string.IsNullOrWhiteSpace(body.Label)
        ? Results.BadRequest("A label is required, e.g. 'white van'.")
        : Results.Ok(scenario.Add(body.Label, body.Tags, body.Lat, body.Lng)));

app.MapDelete("/api/objects/{id}", (string id, ScenarioStore scenario) =>
    scenario.Remove(id) ? Results.NoContent() : Results.NotFound());

app.MapPost("/api/reset", (SimFleet fleet) =>
{
    fleet.Reset();
    return Results.NoContent();
});

app.MapPost("/api/timescale", (TimeScaleBody body, SimOptions options) =>
{
    options.TimeScale = Math.Clamp(body.Value, 0, 100);
    return Results.Ok(new { timeScale = options.TimeScale });
});

// Tells the page whether the offline tile archive has been built (scripts/build-offline-map.ps1);
// without it the page draws a plain grid instead.
app.MapGet("/api/map", (IWebHostEnvironment env) =>
    Results.Ok(new { offlineTiles = env.WebRootFileProvider.GetFileInfo("map/israel.pmtiles").Exists }));

app.Run();

internal sealed record NewObject(string Label, List<string>? Tags, double Lat, double Lng);
internal sealed record TimeScaleBody(double Value);
