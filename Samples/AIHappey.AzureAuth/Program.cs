using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;
using AIHappey.Telemetry;
using AIHappey.AzureAuth.AI;
using AIHappey.AzureAuth;
using AIHappey.Core.AI;
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

var telemetryDb = builder.Configuration.GetSection("TelemetryDatabase").Get<string>();

builder.Services.AddTelemetryServices(telemetryDb!);
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<CachedModelProviderResolver>();
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
builder.Services.AddMcpServers();

//BACKGROUND WORKER TELEMETRY
//builder.Services.AddSingleton<ChatTelemetryQueue>();
//builder.Services.AddHostedService<ChatTelemetryWorker>();

//builder.Services.AddSingleton<AIModelProviderResolver>();
builder.Services.AddControllers();

builder.Services.AddControllers().AddJsonOptions(o =>
  {
      o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
  });;


var app = builder.Build();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.AddMcpMappings();
app.AddMcpRegistry([new { src = builder.Configuration.GetValue<string>("DarkIcon"), theme = "dark" },
    new { src = builder.Configuration.GetValue<string>("LightIcon"), theme = "light" }]);

app.Run();
