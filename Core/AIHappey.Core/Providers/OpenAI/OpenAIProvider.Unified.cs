using AIHappey.Core.AI;
using AIHappey.Unified.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.OpenAI;

public partial class OpenAIProvider
{
    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var containerDownloadContext = OpenAiContainerDownloadPolicy.Capture(request, DateTimeOffset.UtcNow);
        RemovePreviouslyUploadedHistoricalAttachments(request);

        if (await this.IsTranscriptionModelAsync(request.Model, cancellationToken))
        {
            return await this.ExecuteUnifiedTranscriptionAsync(request, cancellationToken);
        }

        if (await this.IsSpeechModelAsync(request.Model, cancellationToken))
            return await this.ExecuteUnifiedSpeechAsync(request, cancellationToken);

        if (request.Model?.Contains("search-preview") == true)
            return await this.ExecuteUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);

        request = OpenAiContainerDownloadPolicy.Attach(request, containerDownloadContext);
        return await this.ExecuteUnifiedViaResponsesAsync(request, cancellationToken: cancellationToken);
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var containerDownloadContext = OpenAiContainerDownloadPolicy.Capture(request, DateTimeOffset.UtcNow);
        RemovePreviouslyUploadedHistoricalAttachments(request);

        if (await this.IsTranscriptionModelAsync(request.Model, cancellationToken))
        {
            await foreach (var streamEvent in this.StreamUnifiedTranscriptionAsync(request, cancellationToken)
                .WithCancellation(cancellationToken))
            {
                yield return streamEvent;
            }

            yield break;
        }

        if (await this.IsSpeechModelAsync(request.Model, cancellationToken))
        {
            await foreach (var streamEvent in this.StreamUnifiedSpeechAsync(request, cancellationToken)
                .WithCancellation(cancellationToken))
            {
                yield return streamEvent;
            }

            yield break;
        }

        var stream = request.Model?.Contains("search-preview") == true
            ? this.StreamUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken)
            : this.StreamUnifiedViaResponsesAsync(
                OpenAiContainerDownloadPolicy.Attach(request, containerDownloadContext),
                cancellationToken: cancellationToken);

        await foreach (var streamEvent in stream.WithCancellation(cancellationToken))
            yield return NormalizeUploadFilesStreamEvent(streamEvent);
    }

    private static void RemovePreviouslyUploadedHistoricalAttachments(AIRequest request)
    {
        var items = request.Input?.Items;
        if (items is null || items.Count == 0)
            return;

        var uploadedSourceIdentities = FindUploadedAttachmentSourceIdentities(items);
        if (uploadedSourceIdentities.Count == 0)
            return;

        var latestUserItem = items.LastOrDefault(static item =>
            string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));

        foreach (var item in items)
        {
            if (ReferenceEquals(item, latestUserItem)
                || !string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase)
                || item.Content is null)
            {
                continue;
            }

            item.Content.RemoveAll(part => part is AIFileContentPart file
                                           && uploadedSourceIdentities.Contains(CreateAttachmentSourceIdentity(file)));
        }
    }

    private static HashSet<string> FindUploadedAttachmentSourceIdentities(
        IReadOnlyList<AIInputItem> items)
    {
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var toolPart in items
                     .SelectMany(static item => item.Content ?? [])
                     .OfType<AIToolCallContentPart>())
        {
            if (toolPart.ProviderExecuted != true
                || !string.Equals(toolPart.ToolName ?? toolPart.Title, "upload_files", StringComparison.OrdinalIgnoreCase)
                || toolPart.Output is null)
            {
                continue;
            }

            CollectUploadedAttachmentSourceIdentities(
                JsonSerializer.SerializeToElement(toolPart.Output, JsonSerializerOptions.Web),
                identities);
        }

        return identities;
    }

    private static void CollectUploadedAttachmentSourceIdentities(
        JsonElement value,
        HashSet<string> identities)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (string.Equals(property.Name, "source_identity", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String
                    && property.Value.GetString() is { Length: > 0 } identity)
                {
                    identities.Add(identity);
                }
                else
                {
                    CollectUploadedAttachmentSourceIdentities(property.Value, identities);
                }
            }

            return;
        }

        if (value.ValueKind != JsonValueKind.Array)
            return;

        foreach (var item in value.EnumerateArray())
            CollectUploadedAttachmentSourceIdentities(item, identities);
    }

    private static string CreateAttachmentSourceIdentity(AIFileContentPart file)
    {
        var responsesType = TryGetMetadataString(file.Metadata, "responses.type");
        var fileId = TryGetMetadataString(file.Metadata, "responses.file_id");
        var fileUrl = TryGetMetadataString(file.Metadata, "responses.file_url");
        var data = file.Data?.ToString();

        var identity = responsesType switch
        {
            "input_image" when !string.IsNullOrWhiteSpace(fileId) => $"image_file_id\n{fileId}",
            "input_image" => $"image_url\n{data}",
            _ when !string.IsNullOrWhiteSpace(fileId) => $"file_id\n{fileId}\n{file.Filename}",
            _ when data?.StartsWith("data:", StringComparison.OrdinalIgnoreCase) == true => $"file_data\n{data}\n{file.Filename}",
            _ => $"file_url\n{fileUrl ?? data}\n{file.Filename}"
        };

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    private static string? TryGetMetadataString(
        IReadOnlyDictionary<string, object?>? metadata,
        string key)
    {
        if (metadata is null || !metadata.TryGetValue(key, out var value) || value is null)
            return null;

        return value is JsonElement { ValueKind: JsonValueKind.String } json
            ? json.GetString()
            : value.ToString();
    }

    private static AIStreamEvent NormalizeUploadFilesStreamEvent(AIStreamEvent streamEvent)
    {
        if (streamEvent.Event.Data is not AIToolOutputAvailableEventData output
            || !string.Equals(output.ToolName, "upload_files", StringComparison.OrdinalIgnoreCase)
            || !TryGetUploadFilesStreamFlags(output.Output, out var preliminary, out var dynamic))
        {
            return streamEvent;
        }

        return new AIStreamEvent
        {
            ProviderId = streamEvent.ProviderId,
            Metadata = streamEvent.Metadata,
            Event = new AIEventEnvelope
            {
                Type = streamEvent.Event.Type,
                Id = streamEvent.Event.Id,
                Timestamp = streamEvent.Event.Timestamp,
                Input = streamEvent.Event.Input,
                Output = streamEvent.Event.Output,
                Metadata = streamEvent.Event.Metadata,
                Data = new AIToolOutputAvailableEventData
                {
                    ToolName = output.ToolName,
                    Output = output.Output,
                    ProviderExecuted = output.ProviderExecuted,
                    ProviderMetadata = output.ProviderMetadata,
                    Preliminary = preliminary,
                    Dynamic = dynamic
                }
            }
        };
    }

    private static bool TryGetUploadFilesStreamFlags(
        object? output,
        out bool? preliminary,
        out bool? dynamic)
    {
        preliminary = null;
        dynamic = null;

        try
        {
            var json = output is JsonElement element
                ? element
                : JsonSerializer.SerializeToElement(output, JsonSerializerOptions.Web);
            if (json.ValueKind != JsonValueKind.Object
                || !json.TryGetProperty("structuredContent", out var structured)
                || structured.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (structured.TryGetProperty("preliminary", out var preliminaryValue)
                && preliminaryValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                preliminary = preliminaryValue.GetBoolean();
            }

            if (structured.TryGetProperty("dynamic", out var dynamicValue)
                && dynamicValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                dynamic = dynamicValue.GetBoolean();
            }

            return preliminary is not null || dynamic is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }


}
