using System.Text.Json;
using Azure;
using Azure.Storage.Queues;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using Microsoft.Extensions.Options;

namespace AIHappey.Core.Storage;

public sealed class AzureQueueModelListingRefreshQueue : IModelListingRefreshQueue
{
    private readonly QueueClient? _queueClient;

    public AzureQueueModelListingRefreshQueue(IOptions<ModelListingStorageOptions> options)
    {
        var settings = options.Value;

        if (string.IsNullOrWhiteSpace(settings.ConnectionString)
            || string.IsNullOrWhiteSpace(settings.QueueName))
        {
            return;
        }

        _queueClient = new QueueClient(settings.ConnectionString, settings.QueueName);
    }

    public bool IsEnabled => _queueClient != null;

    public async Task EnqueueAsync(ModelListingRefreshRequest request, CancellationToken cancellationToken = default)
    {
        if (_queueClient == null)
            return;

        await _queueClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        await _queueClient.SendMessageAsync(
            BinaryData.FromObjectAsJson(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)).ToString(),
            cancellationToken);
    }

    public async Task<ModelListingQueueMessage?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        if (_queueClient == null)
            return null;

        await _queueClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        var response = await _queueClient.ReceiveMessagesAsync(maxMessages: 1, visibilityTimeout: TimeSpan.FromMinutes(5), cancellationToken);
        var message = response.Value.FirstOrDefault();

        if (message == null)
            return null;

        var request = JsonSerializer.Deserialize<ModelListingRefreshRequest>(message.MessageText, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (request == null)
            throw new InvalidOperationException("Invalid model listing queue message.");

        return new ModelListingQueueMessage
        {
            MessageId = message.MessageId,
            PopReceipt = message.PopReceipt,
            Request = request
        };
    }

    public async Task DeleteAsync(ModelListingQueueMessage message, CancellationToken cancellationToken = default)
    {
        if (_queueClient == null)
            return;

        await _queueClient.DeleteMessageAsync(message.MessageId, message.PopReceipt, cancellationToken);
    }
}
