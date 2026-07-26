using UavOps.ControlApi.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new() { Title = "UavOps.ControlApi", Version = "v1" });
});

builder.Services.AddSingleton<IUavFleetService, SimulatedUavFleetService>();
builder.Services.AddSingleton<IGdtService, SimulatedGdtService>();

var app = builder.Build();

// Keep serving the spec at the same path UavOps.Agent already points at (UavApi:OpenApiUrl).
app.UseSwagger(options => options.RouteTemplate = "openapi/{documentName}.json");
app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "UavOps.ControlApi v1"));

app.MapControllers();

app.Run();

public partial class Program;
