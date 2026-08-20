using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.OpenAI;

internal static class OpenAiContainerDownloadPolicy
{
    internal const string RuntimeContextMetadataKey = "__aihappey_openai_container_download_context";

    internal static OpenAiContainerDownloadRequestContext Capture(
        AIRequest request,
        DateTimeOffset turnStartedAt)
    {
        var downloadedFileKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var toolPart in request.Input?.Items?
                     .SelectMany(static item => item.Content ?? [])
                     .OfType<AIToolCallContentPart>() ?? [])
        {
            if (!IsHistoricalDownloadFileTool(toolPart))
                continue;

            CollectContainerFileKeys(toolPart.Input, downloadedFileKeys);
            CollectContainerFileKeys(toolPart.Output, downloadedFileKeys);
            CollectContainerFileKeys(toolPart.Metadata, downloadedFileKeys);
        }

        return new OpenAiContainerDownloadRequestContext(
            turnStartedAt.ToUnixTimeSeconds(),
            downloadedFileKeys);
    }

    internal static AIRequest Attach(
        AIRequest request,
        OpenAiContainerDownloadRequestContext context)
    {
        var metadata = request.Metadata is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(request.Metadata);
        metadata[RuntimeContextMetadataKey] = JsonSerializer.SerializeToElement(new
        {
            turn_started_at = context.TurnStartedAtUnixSeconds,
            downloaded_file_keys = context.DownloadedFileKeys.ToArray()
        }, JsonSerializerOptions.Web);

        return new AIRequest
        {
            ProviderId = request.ProviderId,
            Model = request.Model,
            Id = request.Id,
            Instructions = request.Instructions,
            Input = request.Input,
            Temperature = request.Temperature,
            TopP = request.TopP,
            MaxOutputTokens = request.MaxOutputTokens,
            MaxToolCalls = request.MaxToolCalls,
            Stream = request.Stream,
            ParallelToolCalls = request.ParallelToolCalls,
            ToolChoice = request.ToolChoice,
            ResponseFormat = request.ResponseFormat,
            Tools = request.Tools,
            Metadata = metadata,
            Headers = request.Headers,
            Verbosity = request.Verbosity
        };
    }

    internal static OpenAiContainerDownloadRequestContext Consume(
        Dictionary<string, object?>? metadata,
        DateTimeOffset fallbackTurnStartedAt)
    {
        if (metadata is null
            || !metadata.Remove(RuntimeContextMetadataKey, out var rawContext)
            || rawContext is null)
        {
            return OpenAiContainerDownloadRequestContext.Empty(fallbackTurnStartedAt);
        }

        try
        {
            var context = rawContext is JsonElement element
                ? element
                : JsonSerializer.SerializeToElement(rawContext, JsonSerializerOptions.Web);
            if (context.ValueKind != JsonValueKind.Object)
                return OpenAiContainerDownloadRequestContext.Empty(fallbackTurnStartedAt);

            var turnStartedAtUnixSeconds = context.TryGetProperty("turn_started_at", out var turnStartedAt)
                && turnStartedAt.TryGetInt64(out var parsedTurnStartedAt)
                    ? parsedTurnStartedAt
                    : fallbackTurnStartedAt.ToUnixTimeSeconds();
            var downloadedFileKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (context.TryGetProperty("downloaded_file_keys", out var keys)
                && keys.ValueKind == JsonValueKind.Array)
            {
                foreach (var key in keys.EnumerateArray())
                {
                    if (key.ValueKind == JsonValueKind.String
                        && key.GetString() is { Length: > 0 } value)
                    {
                        downloadedFileKeys.Add(value);
                    }
                }
            }

            return new OpenAiContainerDownloadRequestContext(
                turnStartedAtUnixSeconds,
                downloadedFileKeys);
        }
        catch (JsonException)
        {
            return OpenAiContainerDownloadRequestContext.Empty(fallbackTurnStartedAt);
        }
    }

    internal static string CreateContainerFileKey(string containerId, string fileId)
        => $"{containerId}:{fileId}";

    internal static bool IsFallbackFileFromCurrentTurn(
        long? createdAtUnixSeconds,
        long turnStartedAtUnixSeconds)
        => createdAtUnixSeconds is long createdAt
           && createdAt >= turnStartedAtUnixSeconds;

    private static bool IsHistoricalDownloadFileTool(AIToolCallContentPart toolPart)
    {
        if (toolPart.ProviderExecuted != true)
            return false;

        if (string.Equals(toolPart.ToolName, "download_file", StringComparison.OrdinalIgnoreCase)
            || string.Equals(toolPart.Title, "download_file", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ContainsDownloadToolMarker(toolPart.Metadata);
    }

    private static bool ContainsDownloadToolMarker(object? value)
    {
        if (value is null)
            return false;

        try
        {
            var json = value is JsonElement element
                ? element
                : JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
            return ContainsDownloadToolMarker(json);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ContainsDownloadToolMarker(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray().Any(ContainsDownloadToolMarker);

        if (value.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var property in value.EnumerateObject())
        {
            if (string.Equals(property.Name, "download_tool", StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if ((string.Equals(property.Name, "tool_name", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(property.Name, "name", StringComparison.OrdinalIgnoreCase))
                && property.Value.ValueKind == JsonValueKind.String
                && string.Equals(property.Value.GetString(), "download_file", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (ContainsDownloadToolMarker(property.Value))
                return true;
        }

        return false;
    }

    private static void CollectContainerFileKeys(object? value, HashSet<string> keys)
    {
        if (value is null)
            return;

        try
        {
            var json = value is JsonElement element
                ? element
                : JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
            CollectContainerFileKeys(json, keys);
        }
        catch (JsonException)
        {
            // Historical UI data is best-effort. Invalid metadata must not fail a turn.
        }
    }

    private static void CollectContainerFileKeys(JsonElement value, HashSet<string> keys)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                CollectContainerFileKeys(item, keys);
            return;
        }

        if (value.ValueKind != JsonValueKind.Object)
            return;

        var containerId = TryGetString(value, "container_id");
        var fileId = TryGetString(value, "file_id");
        if (!string.IsNullOrWhiteSpace(containerId) && !string.IsNullOrWhiteSpace(fileId))
            keys.Add(CreateContainerFileKey(containerId, fileId));

        foreach (var property in value.EnumerateObject())
            CollectContainerFileKeys(property.Value, keys);
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }

        return null;
    }
}

internal sealed record OpenAiContainerDownloadRequestContext(
    long TurnStartedAtUnixSeconds,
    IReadOnlySet<string> DownloadedFileKeys)
{
    internal static OpenAiContainerDownloadRequestContext Empty(DateTimeOffset turnStartedAt)
        => new(
            turnStartedAt.ToUnixTimeSeconds(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
}
