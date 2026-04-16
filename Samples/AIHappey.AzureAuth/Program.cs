using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;
using AIHappey.Abstractions.Http;
using AIHappey.Telemetry;
using AIHappey.AzureAuth;
using AIHappey.Core.AI;
using AIHappey.Common.MCP;
using AIHappey.Core.MCP;
using AIHappey.Telemetry.MCP;
using AIHappey.Core.Providers.Azure;
using System.Text.Json.Serialization;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Orchestration;
using AIHappey.Core.Providers.AmazonBedrock;
using AIHappey.Core.Providers.Databricks;
using AIHappey.Core.Providers.Modal;
using AIHappey.Core.Storage;
using Microsoft.Extensions.Caching.Memory;

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsDevelopment())
    ProviderBackendCapture.ConfigureDevelopmentDefaults(builder.Environment.ContentRootPath);
else
    ProviderBackendCapture.Disable();

builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(230);
    o.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(230);
    o.Limits.MaxRequestBodySize = null;
});

// Add services to the container.
builder.Services.Configure<AIServiceConfig>(
    builder.Configuration.GetSection("AIServices"));

builder.Services.Configure<KeyVaultOptions>(
    builder.Configuration.GetSection("KeyVault"));

builder.Services.Configure<AzureAdClientOptions>(
    builder.Configuration.GetSection("AzureAd"));

builder.Services.Configure<EndUserIdHashingOptions>(
    builder.Configuration.GetSection("EndUserIdHashing"));

builder.Services.Configure<AzureProviderOptions>(
    builder.Configuration.GetSection("AIServices:Azure"));

builder.Services.Configure<AmazonProviderOptions>(
    builder.Configuration.GetSection("AIServices:AmazonBedrock"));

builder.Services.Configure<DatabricksProviderOptions>(
    builder.Configuration.GetSection("AIServices:Databricks"));

builder.Services.Configure<ModalProviderOptions>(
    builder.Configuration.GetSection("AIServices:Modal"));

builder.Services.Configure<ModelListingStorageOptions>(
    builder.Configuration.GetSection("ModelListingStorage"));

var telemetryDb = builder.Configuration.GetSection("TelemetryDatabase").Get<string>();
var kernelMemoryConfig = builder.Configuration.GetSection("AIServices:KernelMemory").Get<ProviderConfig>();
var openAiConfig = builder.Configuration.GetSection("AIServices:OpenAI").Get<ProviderConfig>();

builder.Services.AddTelemetryServices(telemetryDb!);
builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<StorageBackedModelProviderResolver>();
builder.Services.AddSingleton<IAIModelProviderResolver>(sp => sp.GetRequiredService<StorageBackedModelProviderResolver>());
builder.Services.AddSingleton<IAISkillProviderResolver, SkillProviderResolver>();
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

var modelListingStorage = builder.Configuration.GetSection("ModelListingStorage").Get<ModelListingStorageOptions>();
if (!string.IsNullOrWhiteSpace(modelListingStorage?.ConnectionString))
{
    builder.Services.AddSingleton<IModelListingSnapshotStore, AzureBlobModelListingSnapshotStore>();
    builder.Services.AddSingleton<IModelListingRefreshQueue, AzureQueueModelListingRefreshQueue>();

    if (!string.IsNullOrWhiteSpace(modelListingStorage.QueueName))
        builder.Services.AddHostedService<StorageBackedModelRefreshWorker>();
}

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
