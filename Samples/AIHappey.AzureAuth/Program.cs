using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;
using AIHappey.Telemetry;
using AIHappey.AzureAuth;
using AIHappey.Core.AI;
using AIHappey.Common.MCP;
using AIHappey.Core.MCP;
using AIHappey.Telemetry.MCP;
using AIHappey.Core.Providers.Azure;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(230);
    o.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(230);
});

// Add services to the container.
builder.Services.Configure<AIServiceConfig>(
    builder.Configuration.GetSection("AIServices"));

builder.Services.Configure<AzureProviderOptions>(
    builder.Configuration.GetSection("AIServices:Azure"));

var telemetryDb = builder.Configuration.GetSection("TelemetryDatabase").Get<string>();

builder.Services.AddTelemetryServices(telemetryDb!);
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IAIModelProviderResolver, CachedModelProviderResolver>();
// Add authentication/authorization
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));

builder.Services.AddAuthorization();

// CORS for SPA (adjust origin as needed)
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy
              .AllowAnyHeader()
              .AllowAnyOrigin()
              .AllowAnyMethod()
              .WithExposedHeaders("WWW-Authenticate")
              .WithExposedHeaders("Mcp-Session-Id");
    });
});

builder.Services.AddSingleton<IApiKeyResolver, ConfigKeyResolver>();
builder.Services.AddProviders();
//builder.Services.AddMemoryCache();

var allMcpServers = CoreMcpDefinitions.GetDefinitions()
    .Concat(TelemetryMcpDefinitions.GetDefinitions())
    .GroupBy(d => d.Name)
    .Select(g => new McpServerDefinition(
        g.Key,
        g.First().Description,
        g.First().Title,
        g.SelectMany(d => d.PromptTypes ?? []).ToArray(),
        g.SelectMany(d => d.ToolTypes ?? []).ToArray()
    ))
    .ToList();

builder.Services.AddMcpServers(allMcpServers);

//BACKGROUND WORKER TELEMETRY
//builder.Services.AddSingleton<ChatTelemetryQueue>();
//builder.Services.AddHostedService<ChatTelemetryWorker>();

//builder.Services.AddSingleton<AIModelProviderResolver>();
builder.Services.AddControllers();

builder.Services.AddControllers().AddJsonOptions(o =>
  {
      o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
  }); ;


var app = builder.Build();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.MapMcpEndpoints(allMcpServers, true);
app.MapMcpRegistry(allMcpServers, [new { src = builder.Configuration.GetValue<string>("DarkIcon"), theme = "dark" },
    new { src = builder.Configuration.GetValue<string>("LightIcon"), theme = "light" }]);

app.Run();
