
using AIHappey.HeaderAuth;
using AIHappey.Core.Contracts;
using AIHappey.Core.Orchestration;
using AIHappey.Core.Models;
using AIHappey.Core.Storage;
using Azure.Monitor.OpenTelemetry.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.AddHeaderAuthGateway();

var appInsightsConnectionString =
    builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];

if (!string.IsNullOrWhiteSpace(appInsightsConnectionString))
{
    builder.Services.AddOpenTelemetry()
        .UseAzureMonitor(options =>
        {
            options.ConnectionString = appInsightsConnectionString;
        });
}

var headerModelListingStorage = builder.Configuration.GetSection("ModelListingStorage").Get<ModelListingStorageOptions>();
if (!string.IsNullOrWhiteSpace(headerModelListingStorage?.ConnectionString))
{
    builder.Services.AddSingleton<IModelListingSnapshotStore, AzureBlobModelListingSnapshotStore>();
    builder.Services.AddSingleton<IModelListingRefreshQueue, AzureQueueModelListingRefreshQueue>();

    if (!string.IsNullOrWhiteSpace(headerModelListingStorage.QueueName))
        builder.Services.AddHostedService<StorageBackedModelRefreshWorker>();
}

var app = builder.Build();
app.MapHeaderAuthGateway();

app.Run();
