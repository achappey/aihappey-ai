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
using Microsoft.KernelMemory;
using AIHappey.Core.Providers.KernelMemory;
using AIHappey.Core.Providers.OpenAI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Orchestration;
using AIHappey.Core.Providers.AmazonBedrock;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(230);
    o.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(230);
    o.Limits.MaxRequestBodySize = null;
});

// Add services to the container.
builder.Services.Configure<AIServiceConfig>(
    builder.Configuration.GetSection("AIServices"));

builder.Services.Configure<EndUserIdHashingOptions>(
    builder.Configuration.GetSection("EndUserIdHashing"));

builder.Services.Configure<AzureProviderOptions>(
    builder.Configuration.GetSection("AIServices:Azure"));

builder.Services.Configure<AmazonProviderOptions>(
    builder.Configuration.GetSection("AIServices:AmazonBedrock"));

var telemetryDb = builder.Configuration.GetSection("TelemetryDatabase").Get<string>();
var kernelMemoryConfig = builder.Configuration.GetSection("AIServices:KernelMemory").Get<ProviderConfig>();
var openAiConfig = builder.Configuration.GetSection("AIServices:OpenAI").Get<ProviderConfig>();

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
builder.Services.AddSingleton<IEndUserIdResolver, AzureEndUserIdResolver>();
builder.Services.AddProviders();

if (!string.IsNullOrEmpty(kernelMemoryConfig?.Endpoint)
    && !string.IsNullOrEmpty(openAiConfig?.ApiKey))
{
    builder.Services.AddKernelMemoryWithOptions(memoryBuilder =>
    {
        memoryBuilder
            .WithOpenAI(new OpenAIConfig()
            {
                APIKey = openAiConfig.ApiKey,
                TextModel = kernelMemoryConfig.DefaultModel ?? "gpt-5.2",
                TextModelMaxTokenTotal = 128_000,
                EmbeddingDimensions = 3072,
                EmbeddingModel = "text-embedding-3-large"
            })
            .WithSqlServerMemoryDb(new()
            {
                ConnectionString = kernelMemoryConfig.Endpoint
            })
            .WithSearchClientConfig(new()
            {
                MaxMatchesCount = int.MaxValue,
                AnswerTokens = 64_000
            });
    }, new()
    {
        AllowMixingVolatileAndPersistentData = true
    });

    builder.Services.AddSingleton<IModelProvider, KernelMemoryProvider>();
    builder.Services.AddSingleton<OpenAIProvider>();
}

//builder.Services.AddMemoryCache();

var allMcpServers = CoreMcpDefinitions.GetDefinitions()
    .Concat(TelemetryMcpDefinitions.GetDefinitions())
    .GroupBy(d => d.Name)
    .Select(g => new McpServerDefinition(
        g.Key,
        g.First().Description,
        g.First().Title,
        [.. g.SelectMany(d => d.PromptTypes ?? [])],
        [.. g.SelectMany(d => d.ToolTypes ?? [])]
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
