
using System.Text.Json.Serialization;
using AIHappey.Abstractions.Http;
using AIHappey.Core.AI;
using AIHappey.HeaderAuth;
using AIHappey.Common.MCP;
using AIHappey.Core.MCP;
using AIHappey.Core.Contracts;
using AIHappey.Core.Orchestration;
using AIHappey.Core.Models;
using AIHappey.Core.Storage;

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

builder.Services.AddHttpContextAccessor();
builder.Services.Configure<EndUserIdHashingOptions>(builder.Configuration.GetSection("EndUserIdHashing"));
builder.Services.Configure<ModelListingStorageOptions>(builder.Configuration.GetSection("ModelListingStorage"));

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

builder.Services.AddSingleton<StorageBackedModelProviderResolver>();
builder.Services.AddSingleton<IAIModelProviderResolver>(sp => sp.GetRequiredService<StorageBackedModelProviderResolver>());
builder.Services.AddSingleton<IAISkillProviderResolver, SkillProviderResolver>();
builder.Services.AddSingleton<HeaderApiKeySnapshot>();
builder.Services.AddSingleton<IApiKeyResolver, HeaderApiKeyResolver>();
builder.Services.AddSingleton<IEndUserIdResolver, HeaderEndUserIdResolver>();
builder.Services.AddProviders();
builder.Services.AddHttpClient();

var headerModelListingStorage = builder.Configuration.GetSection("ModelListingStorage").Get<ModelListingStorageOptions>();
if (!string.IsNullOrWhiteSpace(headerModelListingStorage?.ConnectionString))
{
    builder.Services.AddSingleton<IModelListingSnapshotStore, AzureBlobModelListingSnapshotStore>();
    builder.Services.AddSingleton<IModelListingRefreshQueue, AzureQueueModelListingRefreshQueue>();

    if (!string.IsNullOrWhiteSpace(headerModelListingStorage.QueueName))
        builder.Services.AddHostedService<StorageBackedModelRefreshWorker>();
}

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
