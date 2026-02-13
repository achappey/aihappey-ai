
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.HeaderAuth;
using AIHappey.Common.MCP;
using AIHappey.Core.MCP;
using AIHappey.Core.Contracts;
using AIHappey.Core.Orchestration;
using AIHappey.Core.Models;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(230);
    o.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(230);
    o.Limits.MaxRequestBodySize = null;
});

builder.Services.AddHttpContextAccessor();
builder.Services.Configure<EndUserIdHashingOptions>(builder.Configuration.GetSection("EndUserIdHashing"));

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

builder.Services.AddSingleton<IAIModelProviderResolver, AIModelProviderResolver>();
builder.Services.AddSingleton<IApiKeyResolver, HeaderApiKeyResolver>();
builder.Services.AddSingleton<IEndUserIdResolver, HeaderEndUserIdResolver>();
builder.Services.AddProviders();
builder.Services.AddHttpClient();
builder.Services.AddMcpServers(CoreMcpDefinitions.GetDefinitions());

builder.Services.AddControllers().AddJsonOptions(o =>
  {
      o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
  }); ;

var app = builder.Build();

app.UseCors();
app.MapMcpEndpoints(CoreMcpDefinitions.GetDefinitions(), false);
app.MapMcpRegistry(CoreMcpDefinitions.GetDefinitions());
app.MapControllers();

app.Run();
