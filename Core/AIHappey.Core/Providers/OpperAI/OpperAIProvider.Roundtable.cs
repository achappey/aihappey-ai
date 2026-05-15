using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.OpperAI;

public partial class OpperAIProvider
{
    private const string RoundtableModelId = "roundtable";
    private const string RoundtablePath = "v3/roundtable";

    private static readonly JsonSerializerOptions RoundtableJson = new(JsonSerializerOptions.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private static bool IsRoundtableModel(string? model)
        => string.Equals(model, RoundtableModelId, StringComparison.OrdinalIgnoreCase);

    private async Task<AIResponse> ExecuteRoundtableUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ApplyAuthHeader();

        var payload = BuildRoundtablePayload(request, stream: false);
        using var httpRequest = CreateRoundtableRequest(payload, stream: false);
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"OpperAI Roundtable failed ({(int)response.StatusCode}): {body}");

        using var document = JsonDocument.Parse(body);
        return CreateRoundtableUnifiedResponse(request, payload, document.RootElement.Clone());
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamRoundtableUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ApplyAuthHeader();

        var providerId = GetIdentifier();
        var eventId = request.Id ?? $"opperai_roundtable_{Guid.NewGuid():N}";
        var payload = BuildRoundtablePayload(request, stream: true);
        var metadata = CreateRoundtableBaseMetadata(request, payload);
        var timestamp = DateTimeOffset.UtcNow;
        var textStarted = false;
        var sawText = false;
        JsonElement? lastChunk = null;
        object? usage = null;

        using var httpRequest = CreateRoundtableRequest(payload, stream: true);
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"OpperAI Roundtable stream failed ({(int)response.StatusCode}): {errorBody}");
        }

        if (!IsServerSentEventResponse(response))
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var fallbackDocument = JsonDocument.Parse(body);
            var fallbackRoot = fallbackDocument.RootElement.Clone();
            lastChunk = fallbackRoot;
            MergeRoundtableResponseMetadata(metadata, fallbackRoot);

            var fallbackText = ExtractRoundtableResponseText(fallbackRoot);
            if (!string.IsNullOrEmpty(fallbackText))
            {
                yield return CreateRoundtableStreamEvent(providerId, eventId, "text-start", new AITextStartEventData(), timestamp, metadata);
                yield return CreateRoundtableStreamEvent(providerId, eventId, "text-delta", new AITextDeltaEventData { Delta = fallbackText }, timestamp, metadata);
                yield return CreateRoundtableStreamEvent(providerId, eventId, "text-end", new AITextEndEventData(), timestamp, metadata);
                sawText = true;
            }

            usage = ExtractRoundtableUsage(fallbackRoot);
            yield return CreateRoundtableFinishEvent(providerId, eventId, request, fallbackRoot, usage, metadata);
            yield break;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                break;

            if (line.Length == 0 || line.StartsWith(':'))
                continue;

            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            var data = line["data:".Length..].Trim();
            if (data.Length == 0)
                continue;

            if (data.Equals("[DONE]", StringComparison.OrdinalIgnoreCase))
                break;

            JsonElement chunk;

            using var chunkDocument = JsonDocument.Parse(data);
            chunk = chunkDocument.RootElement.Clone();

            lastChunk = chunk;
            metadata["opperai.roundtable.stream.last_chunk"] = chunk.Clone();
            MergeRoundtableResponseMetadata(metadata, chunk);
            usage = ExtractRoundtableUsage(chunk) ?? usage;

            if (TryExtractRoundtableError(chunk, out var errorText))
            {
                yield return CreateRoundtableStreamEvent(
                    providerId,
                    eventId,
                    "error",
                    new AIErrorEventData { ErrorText = errorText },
                    DateTimeOffset.UtcNow,
                    metadata);
                continue;
            }

            var delta = ExtractRoundtableStreamDelta(chunk);
            if (!string.IsNullOrEmpty(delta))
            {
                if (!textStarted)
                {
                    yield return CreateRoundtableStreamEvent(providerId, eventId, "text-start", new AITextStartEventData(), timestamp, metadata);
                    textStarted = true;
                }

                yield return CreateRoundtableStreamEvent(
                    providerId,
                    eventId,
                    "text-delta",
                    new AITextDeltaEventData { Delta = delta },
                    DateTimeOffset.UtcNow,
                    metadata);
                sawText = true;
                continue;
            }

            if (!IsRoundtableTerminalChunk(chunk))
            {
                yield return CreateRoundtableStreamEvent(
                    providerId,
                    eventId,
                    "data",
                    new AIDataEventData
                    {
                        Id = eventId,
                        Data = chunk.Clone()
                    },
                    DateTimeOffset.UtcNow,
                    metadata);
            }
        }

        if (textStarted)
            yield return CreateRoundtableStreamEvent(providerId, eventId, "text-end", new AITextEndEventData(), DateTimeOffset.UtcNow, metadata);
        else if (!sawText && lastChunk is { ValueKind: JsonValueKind.Object } finalRoot)
        {
            var finalText = ExtractRoundtableResponseText(finalRoot);
            if (!string.IsNullOrEmpty(finalText))
            {
                yield return CreateRoundtableStreamEvent(providerId, eventId, "text-start", new AITextStartEventData(), timestamp, metadata);
                yield return CreateRoundtableStreamEvent(providerId, eventId, "text-delta", new AITextDeltaEventData { Delta = finalText }, DateTimeOffset.UtcNow, metadata);
                yield return CreateRoundtableStreamEvent(providerId, eventId, "text-end", new AITextEndEventData(), DateTimeOffset.UtcNow, metadata);
            }
        }

        yield return CreateRoundtableFinishEvent(
            providerId,
            eventId,
            request,
            lastChunk,
            usage,
            metadata);
    }

    private HttpRequestMessage CreateRoundtableRequest(JsonObject payload, bool stream)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, RoundtablePath)
        {
            Content = new StringContent(payload.ToJsonString(RoundtableJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(stream ? "text/event-stream" : MediaTypeNames.Application.Json));
        return request;
    }

    private JsonObject BuildRoundtablePayload(AIRequest request, bool stream)
    {
        var providerOptions = ExtractRoundtableProviderOptions(request.Metadata);
        var payload = providerOptions is null ? [] : JsonElementObjectToJsonObject(providerOptions.Value);

        payload["input"] = BuildRoundtableInput(request);
        payload["stream"] = stream;

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            payload["instructions"] = request.Instructions;

        if (!payload.TryGetPropertyValue("models", out var modelsNode)
            || modelsNode is not JsonArray modelsArray
            || modelsArray.Count == 0)
        {
            throw new ArgumentException("OpperAI Roundtable requires provider option metadata.opperai.models with at least one model.", nameof(request));
        }

        return payload;
    }

    private static JsonElement? ExtractRoundtableProviderOptions(Dictionary<string, object?>? metadata)
    {
        if (metadata is null || !metadata.TryGetValue("opperai", out var raw) || raw is null)
            return null;

        var element = raw switch
        {
            JsonElement json => json.Clone(),
            JsonObject jsonObject => JsonSerializer.SerializeToElement(jsonObject, RoundtableJson),
            Dictionary<string, object?> dictionary => JsonSerializer.SerializeToElement(dictionary, RoundtableJson),
            _ => JsonSerializer.SerializeToElement(raw, RoundtableJson)
        };

        return element.ValueKind == JsonValueKind.Object
            ? element
            : null;
    }

    private static JsonObject JsonElementObjectToJsonObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return [];

        return JsonNode.Parse(element.GetRawText()) as JsonObject ?? [];
    }

    private static string BuildRoundtableInput(AIRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Input?.Text))
            return request.Input.Text!;

        var conversation = BuildRoundtableConversation(request);
        if (!string.IsNullOrWhiteSpace(conversation))
            return conversation;

        return request.Instructions ?? string.Empty;
    }

    private static string BuildRoundtableConversation(AIRequest request)
    {
        var lines = new List<string>();
        foreach (var item in request.Input?.Items ?? [])
        {
            var text = ExtractRoundtableText(item.Content);
            if (!string.IsNullOrWhiteSpace(text))
                lines.Add($"{item.Role ?? "user"}: {text}");
        }

        return string.Join("\n\n", lines);
    }

    private static string ExtractRoundtableText(IEnumerable<AIContentPart>? parts)
        => string.Join("\n", (parts ?? [])
            .Select(part => part switch
            {
                AITextContentPart text => text.Text,
                AIReasoningContentPart reasoning => reasoning.Text,
                AIFileContentPart file when file.Data is string text => text,
                _ => null
            })
            .Where(text => !string.IsNullOrWhiteSpace(text)));

    private AIResponse CreateRoundtableUnifiedResponse(AIRequest request, JsonObject payload, JsonElement root)
    {
        var text = ExtractRoundtableResponseText(root);
        var metadata = CreateRoundtableBaseMetadata(request, payload, root);
        var usage = ExtractRoundtableUsage(root);

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = RoundtableModelId,
            Status = HasRoundtableErrors(root) && string.IsNullOrWhiteSpace(text) ? "failed" : "completed",
            Usage = usage,
            Metadata = metadata,
            Output = new AIOutput
            {
                Items =
                [
                    new AIOutputItem
                    {
                        Type = "message",
                        Role = "assistant",
                        Content =
                        [
                            new AITextContentPart
                            {
                                Type = "text",
                                Text = text,
                                Metadata = new Dictionary<string, object?>
                                {
                                    ["opperai.roundtable.id"] = TryGetString(root, "id"),
                                    ["opperai.roundtable.data"] = CloneProperty(root, "data")
                                }
                            }
                        ],
                        Metadata = new Dictionary<string, object?>
                        {
                            ["opperai.roundtable.model_results"] = CloneProperty(root, "model_results")
                        }
                    }
                ],
                Metadata = new Dictionary<string, object?>
                {
                    ["opperai.roundtable.raw"] = root.Clone(),
                    ["opperai.roundtable.meta"] = CloneProperty(root, "meta"),
                    ["opperai.roundtable.model_results"] = CloneProperty(root, "model_results")
                }
            }
        };
    }

    private Dictionary<string, object?> CreateRoundtableBaseMetadata(
        AIRequest request,
        JsonObject payload,
        JsonElement? root = null)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["opperai.roundtable"] = true,
            ["opperai.roundtable.model"] = request.Model,
            ["opperai.roundtable.request.payload"] = JsonSerializer.SerializeToElement(payload, RoundtableJson)
        };

        if (root is { ValueKind: JsonValueKind.Object } rootElement)
            MergeRoundtableResponseMetadata(metadata, rootElement);

        return metadata;
    }

    private static void MergeRoundtableResponseMetadata(Dictionary<string, object?> metadata, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return;

        metadata["opperai.roundtable.raw"] = root.Clone();
        metadata["opperai.roundtable.id"] = TryGetString(root, "id");
        metadata["opperai.roundtable.meta"] = CloneProperty(root, "meta");
        metadata["opperai.roundtable.model_results"] = CloneProperty(root, "model_results");

        if (root.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
        {
            metadata["opperai.roundtable.resolution"] = TryGetString(meta, "resolution");
            metadata["opperai.roundtable.models_used"] = CloneProperty(meta, "models_used");
            metadata["opperai.roundtable.total_cost"] = CloneProperty(meta, "total_cost");
            metadata["opperai.roundtable.total_duration_ms"] = CloneProperty(meta, "total_duration_ms");
            metadata["opperai.roundtable.trace_uuid"] = TryGetString(meta, "trace_uuid");
        }
    }

    private static AIStreamEvent CreateRoundtableStreamEvent(
        string providerId,
        string eventId,
        string type,
        object data,
        DateTimeOffset timestamp,
        Dictionary<string, object?>? metadata)
        => new()
        {
            ProviderId = providerId,
            Event = new AIEventEnvelope
            {
                Type = type,
                Id = eventId,
                Timestamp = timestamp,
                Data = data
            },
            Metadata = metadata
        };

    private static AIStreamEvent CreateRoundtableFinishEvent(
        string providerId,
        string eventId,
        AIRequest request,
        JsonElement? responseRoot,
        object? usage,
        Dictionary<string, object?> metadata)
        => CreateRoundtableStreamEvent(
            providerId,
            eventId,
            "finish",
            new AIFinishEventData
            {
                FinishReason = "stop",
                Model = RoundtableModelId,
                CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Response = responseRoot is { } root ? root.Clone() : null,
                MessageMetadata = AIFinishMessageMetadata.Create(
                    RoundtableModelId,
                    DateTimeOffset.UtcNow,
                    usage: usage ?? new Dictionary<string, object?>(),
                    temperature: request.Temperature,
                    gateway: CreateRoundtableGatewayMetadata(responseRoot))
            },
            DateTimeOffset.UtcNow,
            metadata);

    private static AIFinishGatewayMetadata? CreateRoundtableGatewayMetadata(JsonElement? root)
    {
        var cost = TryExtractTotalCost(root);
        return cost is null ? null : new AIFinishGatewayMetadata { Cost = cost };
    }

    private static decimal? TryExtractTotalCost(JsonElement? root)
    {
        if (root is not { ValueKind: JsonValueKind.Object } rootElement)
            return null;

        if (rootElement.TryGetProperty("meta", out var meta)
            && meta.ValueKind == JsonValueKind.Object
            && meta.TryGetProperty("total_cost", out var cost)
            && cost.ValueKind == JsonValueKind.Number
            && cost.TryGetDecimal(out var totalCost))
            return totalCost;

        return null;
    }

    private static object? ExtractRoundtableUsage(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        if (root.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
        {
            if (meta.TryGetProperty("summary_usage", out var summaryUsage) && summaryUsage.ValueKind == JsonValueKind.Object)
                return summaryUsage.Clone();

            return meta.Clone();
        }

        return null;
    }

    private static string ExtractRoundtableResponseText(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return root.ValueKind == JsonValueKind.String ? root.GetString() ?? string.Empty : root.GetRawText();

        if (root.TryGetProperty("data", out var data))
            return JsonElementToText(data);

        return ExtractRoundtableStreamDelta(root) ?? string.Empty;
    }

    private static string? ExtractRoundtableStreamDelta(JsonElement chunk)
    {
        if (chunk.ValueKind == JsonValueKind.String)
            return chunk.GetString();

        if (chunk.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var key in new[] { "delta", "text_delta", "data", "text", "output", "content" })
        {
            if (chunk.TryGetProperty(key, out var value))
            {
                var text = JsonElementToText(value);
                if (!string.IsNullOrEmpty(text))
                    return text;
            }
        }

        if (chunk.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "content", "text", "data" })
            {
                if (message.TryGetProperty(key, out var value))
                {
                    var text = JsonElementToText(value);
                    if (!string.IsNullOrEmpty(text))
                        return text;
                }
            }
        }

        return null;
    }

    private static bool TryExtractRoundtableError(JsonElement chunk, out string errorText)
    {
        errorText = string.Empty;
        if (chunk.ValueKind != JsonValueKind.Object)
            return false;

        if (chunk.TryGetProperty("error", out var error)
            && error.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            errorText = JsonElementToText(error);
            return !string.IsNullOrWhiteSpace(errorText);
        }

        if (chunk.TryGetProperty("type", out var type)
            && string.Equals(type.GetString(), "error", StringComparison.OrdinalIgnoreCase))
        {
            errorText = chunk.TryGetProperty("message", out var message)
                ? JsonElementToText(message)
                : chunk.GetRawText();
            return true;
        }

        return false;
    }

    private static bool IsRoundtableTerminalChunk(JsonElement chunk)
    {
        if (chunk.ValueKind != JsonValueKind.Object)
            return false;

        if (chunk.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.String)
        {
            var value = type.GetString();
            return string.Equals(value, "done", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(value, "finish", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(value, "completed", StringComparison.OrdinalIgnoreCase);
        }

        return chunk.TryGetProperty("meta", out _)
               && chunk.TryGetProperty("model_results", out _);
    }

    private static bool HasRoundtableErrors(JsonElement root)
    {
        if (!root.TryGetProperty("model_results", out var results) || results.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var result in results.EnumerateArray())
        {
            if (result.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(error.GetString()))
                return true;
        }

        return false;
    }

    private static string JsonElementToText(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            _ => value.GetRawText()
        };

    private static string? TryGetString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static object? CloneProperty(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var value)
            ? value.Clone()
            : null;

    private static bool IsServerSentEventResponse(HttpResponseMessage response)
        => string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase);

}
