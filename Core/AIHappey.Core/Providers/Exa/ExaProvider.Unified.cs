using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Exa;

public partial class ExaProvider
{
    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ApplyAuthHeader();

        var target = ResolveBackendTarget(request.Model);
        var query = BuildUnifiedQuery(request);
        if (string.IsNullOrWhiteSpace(query))
            throw new InvalidOperationException("Exa requires a non-empty query derived from unified input or instructions.");

        var payload = BuildExaPayload(request, target, query, stream: false);
        var endpoint = target.Backend == "answer" ? "answer" : "search";

        using var httpRequest = CreateJsonRequest(endpoint, payload);
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Exa {endpoint} failed ({(int)response.StatusCode}): {body}");

        using var document = JsonDocument.Parse(body);
        return target.Backend == "answer"
            ? CreateAnswerUnifiedResponse(request, target, payload, document.RootElement.Clone())
            : CreateSearchUnifiedResponse(request, target, payload, document.RootElement.Clone());
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ApplyAuthHeader();

        var target = ResolveBackendTarget(request.Model);
        var query = BuildUnifiedQuery(request);
        if (string.IsNullOrWhiteSpace(query))
            throw new InvalidOperationException("Exa requires a non-empty query derived from unified input or instructions.");

        var payload = BuildExaPayload(request, target, query, stream: true);
        var endpoint = target.Backend == "answer" ? "answer" : "search";
        var providerId = GetIdentifier();
        var eventId = request.Id ?? $"exa_{Guid.NewGuid():N}";
        var timestamp = DateTimeOffset.UtcNow;
        var metadata = CreateBaseMetadata(request, target, payload);
        var textStarted = false;
        var emittedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        object? lastUsage = null;

        yield return CreateUnifiedStreamEvent(providerId, eventId, "text-start", new AITextStartEventData(), timestamp, metadata);
        textStarted = true;

        using var httpRequest = CreateJsonRequest(endpoint, payload);
        httpRequest.Headers.Accept.Clear();
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Exa {endpoint} stream failed ({(int)response.StatusCode}): {err}");
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

            using var chunkDoc = JsonDocument.Parse(data);
            var chunk = chunkDoc.RootElement.Clone();
            metadata["exa.stream.last_chunk"] = chunk;

            foreach (var source in CreateSourceEventsFromChunk(providerId, eventId, timestamp, metadata, chunk, emittedSources))
                yield return source;

            if (chunk.TryGetProperty("costDollars", out var costEl) && costEl.ValueKind == JsonValueKind.Object)
                lastUsage = costEl.Clone();

            var delta = target.Backend == "answer"
                ? ExtractAnswerStreamDelta(chunk)
                : ExtractSearchStreamDelta(chunk);

            if (!string.IsNullOrEmpty(delta))
            {
                yield return CreateUnifiedStreamEvent(
                    providerId,
                    eventId,
                    "text-delta",
                    new AITextDeltaEventData { Delta = delta },
                    timestamp,
                    metadata);
            }
        }

        if (textStarted)
            yield return CreateUnifiedStreamEvent(providerId, eventId, "text-end", new AITextEndEventData(), timestamp, metadata);

        yield return CreateUnifiedStreamEvent(
            providerId,
            eventId,
            "finish",
            new AIFinishEventData
            {
                FinishReason = "stop",
                Model = ToProviderModelId(request.Model),
                CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                MessageMetadata = AIFinishMessageMetadata.Create(
                    ToProviderModelId(request.Model),
                    DateTimeOffset.UtcNow,
                    usage: lastUsage ?? new Dictionary<string, object?>())
            },
            DateTimeOffset.UtcNow,
            metadata);
    }

    private HttpRequestMessage CreateJsonRequest(string endpoint, JsonObject payload)
    {
        var json = payload.ToJsonString(JsonWeb);
        return new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };
    }

    private JsonObject BuildExaPayload(AIRequest request, ExaBackendTarget target, string query, bool stream)
    {
        var payload = new JsonObject
        {
            ["query"] = query,
            ["stream"] = stream
        };

        MergeProviderMetadata(payload, request.Metadata);

        payload["query"] = query;
        payload["stream"] = stream;

        var outputSchema = TryExtractOutputSchema(request.ResponseFormat);
        if (outputSchema is not null)
            payload["outputSchema"] = JsonSerializer.SerializeToNode(outputSchema, JsonWeb);

        if (target.Backend == "answer")
        {
            if (!payload.ContainsKey("text"))
                payload["text"] = true;

            return payload;
        }

        payload["type"] = target.NativeType;

        var systemPrompt = BuildUnifiedSystemPrompt(request);
        if (!string.IsNullOrWhiteSpace(systemPrompt) && !payload.ContainsKey("systemPrompt"))
            payload["systemPrompt"] = systemPrompt;

        if (!payload.ContainsKey("contents"))
        {
            payload["contents"] = new JsonObject
            {
                ["highlights"] = true,
                ["text"] = false
            };
        }

        return payload;
    }

    private void MergeProviderMetadata(JsonObject payload, Dictionary<string, object?>? metadata)
    {
        if (metadata is null || !metadata.TryGetValue(GetIdentifier(), out var raw) || raw is null)
            return;

        JsonObject? providerOptions = raw switch
        {
            JsonElement element => JsonElementObjectToJsonObject(element),
            JsonObject jsonObject => jsonObject,
            Dictionary<string, object?> dictionary => JsonSerializer.SerializeToNode(dictionary, JsonWeb) as JsonObject,
            _ => JsonSerializer.SerializeToNode(raw, JsonWeb) as JsonObject
        };

        if (providerOptions is null)
            return;

        foreach (var kvp in providerOptions)
            payload[kvp.Key] = kvp.Value?.DeepClone();
    }

    private AIResponse CreateAnswerUnifiedResponse(AIRequest request, ExaBackendTarget target, JsonObject payload, JsonElement root)
    {
        var answerEl = root.TryGetProperty("answer", out var answer) ? answer.Clone() : default;
        var answerObject = answerEl.ValueKind == JsonValueKind.Undefined
            ? null
            : JsonSerializer.Deserialize<object>(answerEl.GetRawText(), JsonWeb);
        var text = ToOutputText(answerObject);
        var metadata = CreateBaseMetadata(request, target, payload, root);
        var outputItems = new List<AIOutputItem>
        {
            CreateMessageOutputItem(text, answerEl.ValueKind == JsonValueKind.Undefined ? null : answerEl)
        };

        if (root.TryGetProperty("citations", out var citations) && citations.ValueKind == JsonValueKind.Array)
        {
            foreach (var source in CreateSourceOutputItems(citations, "citation"))
                outputItems.Add(source);
        }

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = ToProviderModelId(request.Model),
            Status = "completed",
            Usage = CloneProperty(root, "costDollars"),
            Metadata = metadata,
            Output = new AIOutput
            {
                Items = outputItems,
                Metadata = new Dictionary<string, object?>
                {
                    ["exa.answer"] = answerEl.ValueKind == JsonValueKind.Undefined ? null : answerEl,
                    ["exa.citations"] = CloneProperty(root, "citations")
                }
            }
        };
    }

    private AIResponse CreateSearchUnifiedResponse(AIRequest request, ExaBackendTarget target, JsonObject payload, JsonElement root)
    {
        var outputContent = ExtractSearchOutputContent(root);
        var text = string.IsNullOrWhiteSpace(outputContent) ? BuildSearchResultsText(root) : outputContent;
        var metadata = CreateBaseMetadata(request, target, payload, root);
        var outputItems = new List<AIOutputItem>
        {
            CreateMessageOutputItem(text, CloneProperty(root, "output"))
        };

        if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var source in CreateSourceOutputItems(results, "search_result"))
                outputItems.Add(source);
        }

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = ToProviderModelId(request.Model),
            Status = "completed",
            Usage = CloneProperty(root, "costDollars"),
            Metadata = metadata,
            Output = new AIOutput
            {
                Items = outputItems,
                Metadata = new Dictionary<string, object?>
                {
                    ["exa.output"] = CloneProperty(root, "output"),
                    ["exa.results"] = CloneProperty(root, "results")
                }
            }
        };
    }

    private Dictionary<string, object?> CreateBaseMetadata(
        AIRequest request,
        ExaBackendTarget target,
        JsonObject payload,
        JsonElement? root = null)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["exa.backend"] = target.Backend,
            ["exa.model"] = target.LocalModel,
            ["exa.request.payload"] = JsonSerializer.SerializeToElement(payload, JsonWeb),
            ["exa.requested_model"] = request.Model
        };

        if (root is { ValueKind: JsonValueKind.Object } rootEl)
        {
            metadata["exa.response.raw"] = rootEl.Clone();
            metadata["exa.requestId"] = CloneProperty(rootEl, "requestId");
            metadata["exa.searchType"] = CloneProperty(rootEl, "searchType");
            metadata["exa.costDollars"] = CloneProperty(rootEl, "costDollars");
            metadata["exa.grounding"] = rootEl.TryGetProperty("output", out var output)
                ? CloneProperty(output, "grounding")
                : null;
        }

        return metadata;
    }

    private static AIOutputItem CreateMessageOutputItem(string text, object? raw)
        => new()
        {
            Type = "message",
            Role = "assistant",
            Content =
            [
                new AITextContentPart
                {
                    Type = "text",
                    Text = text,
                    Metadata = raw is null
                        ? null
                        : new Dictionary<string, object?> { ["exa.raw"] = raw }
                }
            ]
        };

    private static IEnumerable<AIOutputItem> CreateSourceOutputItems(JsonElement sources, string sourceType)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources.EnumerateArray())
        {
            var url = TryGetString(source, "url") ?? TryGetString(source, "id");
            if (string.IsNullOrWhiteSpace(url) || !seen.Add(url))
                continue;

            var title = TryGetString(source, "title") ?? url;
            yield return new AIOutputItem
            {
                Type = "source-url",
                Content =
                [
                    new AITextContentPart
                    {
                        Type = "text",
                        Text = title,
                        Metadata = new Dictionary<string, object?>
                        {
                            ["source.url"] = url,
                            ["source.title"] = title,
                            ["source.type"] = sourceType,
                            ["source.raw"] = source.Clone()
                        }
                    }
                ],
                Metadata = new Dictionary<string, object?>
                {
                    ["source.url"] = url,
                    ["source.title"] = title,
                    ["source.type"] = sourceType,
                    ["source.raw"] = source.Clone(),
                    ["url"] = url,
                    ["title"] = title
                }
            };
        }
    }

    private static IEnumerable<AIStreamEvent> CreateSourceEventsFromChunk(
        string providerId,
        string eventId,
        DateTimeOffset timestamp,
        Dictionary<string, object?> metadata,
        JsonElement chunk,
        HashSet<string> emittedSources)
    {
        if (chunk.TryGetProperty("citations", out var citations) && citations.ValueKind == JsonValueKind.Array)
        {
            foreach (var evt in CreateSourceEvents(providerId, eventId, timestamp, metadata, citations, "citation", emittedSources))
                yield return evt;
        }

        if (chunk.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var evt in CreateSourceEvents(providerId, eventId, timestamp, metadata, results, "search_result", emittedSources))
                yield return evt;
        }
    }

    private static IEnumerable<AIStreamEvent> CreateSourceEvents(
        string providerId,
        string eventId,
        DateTimeOffset timestamp,
        Dictionary<string, object?> metadata,
        JsonElement sources,
        string sourceType,
        HashSet<string> emittedSources)
    {
        foreach (var source in sources.EnumerateArray())
        {
            var url = TryGetString(source, "url") ?? TryGetString(source, "id");
            if (string.IsNullOrWhiteSpace(url) || !emittedSources.Add(url))
                continue;

            var title = TryGetString(source, "title") ?? url;
            yield return CreateUnifiedStreamEvent(
                providerId,
                eventId,
                "source-url",
                new AISourceUrlEventData
                {
                    SourceId = url,
                    Url = url,
                    Title = title,
                    Type = sourceType,
                    ProviderMetadata = new Dictionary<string, Dictionary<string, object>>
                    {
                        [providerId] = new Dictionary<string, object>
                        {
                            ["raw"] = source.Clone()
                        }
                    }
                },
                timestamp,
                metadata);
        }
    }

    private static AIStreamEvent CreateUnifiedStreamEvent(
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

    private static string BuildUnifiedQuery(AIRequest request)
    {
        var latestUser = request.Input?.Items?
            .Where(i => string.Equals(i.Role, "user", StringComparison.OrdinalIgnoreCase))
            .Select(i => ExtractUnifiedText(i.Content))
            .LastOrDefault(t => !string.IsNullOrWhiteSpace(t));

        if (!string.IsNullOrWhiteSpace(latestUser))
            return latestUser!;

        if (!string.IsNullOrWhiteSpace(request.Input?.Text))
            return request.Input.Text!;

        var conversation = BuildUnifiedConversationText(request);
        if (!string.IsNullOrWhiteSpace(conversation))
            return conversation;

        return request.Instructions ?? string.Empty;
    }

    private static string BuildUnifiedSystemPrompt(AIRequest request)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            parts.Add(request.Instructions!);

        foreach (var item in request.Input?.Items ?? [])
        {
            if (!string.Equals(item.Role, "system", StringComparison.OrdinalIgnoreCase))
                continue;

            var text = ExtractUnifiedText(item.Content);
            if (!string.IsNullOrWhiteSpace(text))
                parts.Add(text);
        }

        return string.Join("\n\n", parts);
    }

    private static string BuildUnifiedConversationText(AIRequest request)
    {
        var lines = new List<string>();
        foreach (var item in request.Input?.Items ?? [])
        {
            var text = ExtractUnifiedText(item.Content);
            if (!string.IsNullOrWhiteSpace(text))
                lines.Add($"{item.Role ?? "user"}: {text}");
        }

        return string.Join("\n\n", lines);
    }

    private static string ExtractUnifiedText(IEnumerable<AIContentPart>? parts)
        => string.Join("\n", (parts ?? []).OfType<AITextContentPart>().Select(p => p.Text).Where(t => !string.IsNullOrWhiteSpace(t)));

    private static string? ExtractAnswerStreamDelta(JsonElement chunk)
    {
        if (chunk.TryGetProperty("answer", out var answer))
            return answer.ValueKind == JsonValueKind.String ? answer.GetString() : answer.GetRawText();

        return ExtractSearchStreamDelta(chunk);
    }

    private static string? ExtractSearchStreamDelta(JsonElement chunk)
    {
        if (chunk.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.TryGetProperty("delta", out var delta)
                    && delta.ValueKind == JsonValueKind.Object
                    && delta.TryGetProperty("content", out var content))
                {
                    return content.ValueKind == JsonValueKind.String ? content.GetString() : content.GetRawText();
                }
            }
        }

        if (chunk.TryGetProperty("output", out var output)
            && output.ValueKind == JsonValueKind.Object
            && output.TryGetProperty("content", out var outputContent))
        {
            return outputContent.ValueKind == JsonValueKind.String ? outputContent.GetString() : outputContent.GetRawText();
        }

        return null;
    }

    private static string ExtractSearchOutputContent(JsonElement root)
    {
        if (root.TryGetProperty("output", out var output)
            && output.ValueKind == JsonValueKind.Object
            && output.TryGetProperty("content", out var content))
        {
            return content.ValueKind == JsonValueKind.String ? content.GetString() ?? string.Empty : content.GetRawText();
        }

        return string.Empty;
    }

    private static string BuildSearchResultsText(JsonElement root)
    {
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var lines = new List<string>();
        foreach (var result in results.EnumerateArray())
        {
            var title = TryGetString(result, "title");
            var url = TryGetString(result, "url");
            var summary = TryGetString(result, "summary");
            var highlights = TryGetStringArray(result, "highlights").ToList();
            var text = !string.IsNullOrWhiteSpace(summary)
                ? summary
                : highlights.FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(title))
                lines.Add(title!);
            if (!string.IsNullOrWhiteSpace(url))
                lines.Add(url!);
            if (!string.IsNullOrWhiteSpace(text))
                lines.Add(text!);

            if (lines.Count > 0)
                lines.Add(string.Empty);
        }

        return string.Join("\n", lines).Trim();
    }

    private static string? TryGetString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(propertyName, out var property)
           && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static IEnumerable<string> TryGetStringArray(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                yield return item.GetString()!;
        }
    }
}
