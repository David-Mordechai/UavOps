using UavOps.ControlApi.Hubs;
using UavOps.ControlApi.Options;
using UavOps.ControlApi.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new() { Title = "UavOps.ControlApi", Version = "v1" });
});

var fleetBridgeOptions = builder.Configuration.GetSection(FleetBridgeOptions.SectionName).Get<FleetBridgeOptions>()
    ?? new FleetBridgeOptions();
builder.Services.AddSingleton(fleetBridgeOptions);

// The hub is always mapped (harmless no-op when FleetBackend=Simulated) so a fleet command
// client can connect at any time without a restart. MaximumParallelInvocationsPerClient is
// raised for the same reason UavOps.Agent's ChatHub raises it: several commands can be in
// flight to the same connected client at once, and each SubmitCommandResult reply must not
// queue behind another still-processing one.
builder.Services.AddSignalR(options => options.MaximumParallelInvocationsPerClient = 10);
builder.Services.AddSingleton<IUavCommandBroker, UavCommandBroker>();

var fleetBackend = Enum.TryParse<FleetBackend>(builder.Configuration["FleetBackend"], ignoreCase: true, out var backend)
    ? backend
    : FleetBackend.Simulated;

if (fleetBackend == FleetBackend.SignalR)
{
    builder.Services.AddSingleton<IUavFleetService, SignalRUavFleetService>();
    builder.Services.AddSingleton<IGdtService, SignalRGdtService>();
}
else
{
    builder.Services.AddSingleton<IUavFleetService, SimulatedUavFleetService>();
    builder.Services.AddSingleton<IGdtService, SimulatedGdtService>();
}

var app = builder.Build();

// Keep serving the spec at the same path UavOps.Agent already points at (UavApi:OpenApiUrl).
app.UseSwagger(options => options.RouteTemplate = "openapi/{documentName}.json");
app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "UavOps.ControlApi v1"));

app.MapControllers();
app.MapHub<UavCommandHub>("/uavCommandHub");

app.Run();

public partial class Program;
