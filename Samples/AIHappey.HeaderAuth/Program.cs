
using AIHappey.HeaderAuth;
using AIHappey.Core.Contracts;
using AIHappey.Core.Orchestration;
using AIHappey.Core.Models;
using AIHappey.Core.Storage;

var builder = WebApplication.CreateBuilder(args);
builder.AddHeaderAuthGateway();

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
