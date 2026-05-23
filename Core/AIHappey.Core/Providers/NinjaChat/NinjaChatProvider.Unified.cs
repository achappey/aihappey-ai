using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.NinjaChat;

public partial class NinjaChatProvider
{
    private async IAsyncEnumerable<AIStreamEvent> StreamUnifiedEnsembleAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var options = request.ToChatCompletionOptions(GetIdentifier());
        options.Stream = false;
        options.Store ??= false;

        var completion = await CompleteChatAsync(options, cancellationToken);
        var rawResponse = JsonSerializer.SerializeToElement(completion, NinjaChatJson);
        var streamId = string.IsNullOrWhiteSpace(completion.Id)
            ? $"chatcmpl_{Guid.NewGuid():N}"
            : completion.Id;
        var model = string.IsNullOrWhiteSpace(completion.Model) ? options.Model : completion.Model;
        var timestamp = completion.Created > 0
            ? DateTimeOffset.FromUnixTimeSeconds(completion.Created)
            : DateTimeOffset.UtcNow;
        var created = completion.Created > 0
            ? completion.Created
            : timestamp.ToUnixTimeSeconds();
        var providerMetadata = BuildEnsembleProviderMetadata(rawResponse);
        var text = ExtractAssistantMessageText(completion);
        var finishReason = ExtractFinishReason(completion) ?? "stop";
        var inputTokens = ExtractUsageInt(completion.Usage, "prompt_tokens", "promptTokens", "inputTokens", "input_tokens");
        var outputTokens = ExtractUsageInt(completion.Usage, "completion_tokens", "completionTokens", "outputTokens", "output_tokens");
        var totalTokens = ExtractUsageInt(completion.Usage, "total_tokens", "totalTokens", "total_tokens");

        if (!string.IsNullOrWhiteSpace(text))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return CreateSyntheticEnsembleTextEvent(
                type: "text-start",
                streamId: streamId,
                timestamp: timestamp,
                rawResponse: rawResponse,
                rawChunk: CreateSyntheticEnsembleChunk(
                    streamId,
                    created,
                    model,
                    CreateEnsembleStartChoice(),
                    usage: null,
                    additionalProperties: completion.AdditionalProperties),
                providerMetadata: providerMetadata,
                delta: null);

            foreach (var chunk in ChunkText(text))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return CreateSyntheticEnsembleTextEvent(
                    type: "text-delta",
                    streamId: streamId,
                    timestamp: timestamp,
                    rawResponse: rawResponse,
                    rawChunk: CreateSyntheticEnsembleChunk(
                        streamId,
                        created,
                        model,
                        CreateEnsembleTextDeltaChoice(chunk),
                        usage: null,
                        additionalProperties: completion.AdditionalProperties),
                    providerMetadata: providerMetadata,
                    delta: chunk);
            }

            cancellationToken.ThrowIfCancellationRequested();
            yield return CreateSyntheticEnsembleTextEvent(
                type: "text-end",
                streamId: streamId,
                timestamp: timestamp,
                rawResponse: rawResponse,
                rawChunk: CreateSyntheticEnsembleChunk(
                    streamId,
                    created,
                    model,
                    CreateEnsembleTextEndChoice(),
                    usage: null,
                    additionalProperties: completion.AdditionalProperties),
                providerMetadata: providerMetadata,
                delta: null);
        }

        cancellationToken.ThrowIfCancellationRequested();

        yield return new AIStreamEvent
        {
            ProviderId = GetIdentifier(),
            Metadata = BuildEnsembleStreamMetadata(
                rawResponse,
                CreateSyntheticEnsembleChunk(
                    streamId,
                    created,
                    model,
                    CreateEnsembleFinishChoice(finishReason),
                    usage: completion.Usage,
                    additionalProperties: completion.AdditionalProperties),
                providerMetadata),
            Event = new AIEventEnvelope
            {
                Type = "finish",
                Id = streamId,
                Timestamp = timestamp,
                Data = new AIFinishEventData
                {
                    FinishReason = finishReason,
                    Model = model,
                    CompletedAt = created,
                    InputTokens = inputTokens,
                    OutputTokens = outputTokens,
                    TotalTokens = totalTokens,
                    Response = rawResponse,
                    MessageMetadata = AIFinishMessageMetadata.Create(
                        model: model ?? string.Empty,
                        timestamp: timestamp,
                        usage: completion.Usage,
                        outputTokens: outputTokens,
                        inputTokens: inputTokens,
                        totalTokens: totalTokens,
                        additionalProperties: BuildEnsembleFinishAdditionalProperties(rawResponse, providerMetadata))
                }
            }
        };
    }

    private AIStreamEvent CreateSyntheticEnsembleTextEvent(
        string type,
        string streamId,
        DateTimeOffset timestamp,
        JsonElement rawResponse,
        JsonElement rawChunk,
        Dictionary<string, object?> providerMetadata,
        string? delta)
        => new()
        {
            ProviderId = GetIdentifier(),
            Metadata = BuildEnsembleStreamMetadata(rawResponse, rawChunk, providerMetadata),
            Event = new AIEventEnvelope
            {
                Type = type,
                Id = streamId,
                Timestamp = timestamp,
                Data = type switch
                {
                    "text-start" => new AITextStartEventData(),
                    "text-end" => new AITextEndEventData(),
                    _ => new AITextDeltaEventData { Delta = delta ?? string.Empty }
                }
            }
        };

    private Dictionary<string, object?> BuildEnsembleStreamMetadata(
        JsonElement rawResponse,
        JsonElement rawChunk,
        Dictionary<string, object?> providerMetadata)
    {
        var metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["chatcompletions.response.raw"] = rawResponse.Clone(),
            ["chatcompletions.stream.raw"] = rawChunk.Clone(),
            [GetIdentifier()] = CloneProviderMetadata(providerMetadata)
        };

        foreach (var prop in rawResponse.EnumerateObject())
            metadata[$"chatcompletions.response.{prop.Name}"] = prop.Value.Clone();

        foreach (var prop in rawChunk.EnumerateObject())
            metadata[$"chatcompletions.stream.{prop.Name}"] = prop.Value.Clone();

        return metadata;
    }

    private Dictionary<string, object?> BuildEnsembleFinishAdditionalProperties(
        JsonElement rawResponse,
        Dictionary<string, object?> providerMetadata)
    {
        var metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["chatcompletions.response.raw"] = rawResponse.Clone(),
            [GetIdentifier()] = CloneProviderMetadata(providerMetadata)
        };

        foreach (var prop in rawResponse.EnumerateObject())
            metadata[$"chatcompletions.response.{prop.Name}"] = prop.Value.Clone();

        return metadata;
    }

    private static Dictionary<string, object?> CloneProviderMetadata(Dictionary<string, object?> providerMetadata)
    {
        var clone = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in providerMetadata)
            clone[item.Key] = item.Value;

        return clone;
    }

    private static Dictionary<string, object?> BuildEnsembleProviderMetadata(JsonElement rawResponse)
    {
        var metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var prop in rawResponse.EnumerateObject())
        {
            if (prop.Name is "id" or "object" or "created" or "model" or "choices" or "usage")
                continue;

            metadata[prop.Name] = prop.Value.Clone();
        }

        return metadata;
    }

    private static JsonElement CreateSyntheticEnsembleChunk(
        string id,
        long created,
        string? model,
        object choice,
        object? usage,
        Dictionary<string, JsonElement>? additionalProperties)
    {
        var chunk = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = id,
            ["object"] = "chat.completion.chunk",
            ["created"] = created,
            ["model"] = model,
            ["choices"] = new[] { choice },
            ["usage"] = usage
        };

        if (additionalProperties is not null)
        {
            foreach (var property in additionalProperties)
                chunk[property.Key] = property.Value.Clone();
        }

        return JsonSerializer.SerializeToElement(chunk, NinjaChatJson);
    }

    private static object CreateEnsembleStartChoice()
        => new
        {
            index = 0,
            delta = new { role = "assistant" },
            finish_reason = (string?)null
        };

    private static object CreateEnsembleTextDeltaChoice(string chunk)
        => new
        {
            index = 0,
            delta = new { content = chunk },
            finish_reason = (string?)null
        };

    private static object CreateEnsembleTextEndChoice()
        => new
        {
            index = 0,
            delta = new { },
            finish_reason = (string?)null
        };

    private static object CreateEnsembleFinishChoice(string finishReason)
        => new
        {
            index = 0,
            delta = new { },
            finish_reason = finishReason
        };

    private static string ExtractAssistantMessageText(ChatCompletion completion)
    {
        foreach (var choice in completion.Choices)
        {
            var root = JsonSerializer.SerializeToElement(choice, NinjaChatJson);
            if (root.ValueKind != JsonValueKind.Object)
                continue;

            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                continue;

            var role = message.TryGetProperty("role", out var roleEl) && roleEl.ValueKind == JsonValueKind.String
                ? roleEl.GetString()
                : null;

            if (!string.IsNullOrWhiteSpace(role)
                && !string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!message.TryGetProperty("content", out var content))
                continue;

            var text = ExtractCompletionMessageText(content);
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return string.Empty;
    }

    private static string? ExtractFinishReason(ChatCompletion completion)
    {
        foreach (var choice in completion.Choices)
        {
            var root = JsonSerializer.SerializeToElement(choice, NinjaChatJson);
            if (root.ValueKind != JsonValueKind.Object)
                continue;

            if (!root.TryGetProperty("finish_reason", out var finishReason) || finishReason.ValueKind != JsonValueKind.String)
                continue;

            var value = finishReason.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static int? ExtractUsageInt(object? usage, params string[] propertyNames)
    {
        if (usage is null)
            return null;

        JsonElement usageElement;
        try
        {
            usageElement = usage is JsonElement json
                ? json
                : JsonSerializer.SerializeToElement(usage, NinjaChatJson);
        }
        catch
        {
            return null;
        }

        if (usageElement.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var propertyName in propertyNames)
        {
            if (!usageElement.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Number)
                continue;

            if (value.TryGetInt32(out var intValue))
                return intValue;

            if (value.TryGetInt64(out var longValue) && longValue is >= int.MinValue and <= int.MaxValue)
                return (int)longValue;
        }

        return null;
    }
}
