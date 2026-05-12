using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Abstractions.Http;
using AIHappey.Common.Extensions;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;

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
        var capture = GetExaBackendCapture(request, GetIdentifier());

        using var httpRequest = CreateJsonRequest(endpoint, payload);
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Exa {endpoint} failed ({(int)response.StatusCode}): {body}");

        await ProviderBackendCapture.CaptureJsonAsync($"exa-{endpoint}", response, body, capture, cancellationToken);

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
        var responseRoot = default(JsonElement);
        var textStarted = false;
        var emittedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        object? lastUsage = null;
        var capture = GetExaBackendCapture(request, providerId);

        using var httpRequest = CreateJsonRequest(endpoint, payload);
        httpRequest.Headers.Accept.Clear();
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Exa {endpoint} stream failed ({(int)response.StatusCode}): {err}");
        }

        if (target.Backend == "search" && !IsServerSentEventResponse(response))
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            await ProviderBackendCapture.CaptureJsonAsync($"exa-{endpoint}", response, body, capture, cancellationToken);

            using var fallbackDoc = JsonDocument.Parse(body);
            responseRoot = fallbackDoc.RootElement.Clone();
            metadata["exa.stream.last_chunk"] = responseRoot;
            MergeResponseMetadata(metadata, responseRoot);

            if (responseRoot.TryGetProperty("costDollars", out var fallbackCostEl) && fallbackCostEl.ValueKind == JsonValueKind.Object)
                lastUsage = fallbackCostEl.Clone();

            var fallbackText = ExtractSearchText(responseRoot);
            if (!string.IsNullOrWhiteSpace(fallbackText))
            {
                yield return CreateUnifiedStreamEvent(providerId, eventId, "text-start", new AITextStartEventData(), timestamp, metadata);
                yield return CreateUnifiedStreamEvent(
                    providerId,
                    eventId,
                    "text-delta",
                    new AITextDeltaEventData { Delta = fallbackText },
                    timestamp,
                    metadata);
                yield return CreateUnifiedStreamEvent(providerId, eventId, "text-end", new AITextEndEventData(), timestamp, metadata);
            }

            foreach (var source in CreateAllSourceEventsFromResponse(providerId, eventId, DateTimeOffset.UtcNow, metadata, responseRoot, emittedSources))
                yield return source;

            yield return CreateFinishStreamEvent(providerId, eventId, request, responseRoot, lastUsage, metadata);
            yield break;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        await using var captureSink = ProviderBackendCapture.BeginStreamCapture($"exa-{endpoint}", response, capture);

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                break;

            if (captureSink is not null)
                await captureSink.WriteLineAsync(line, cancellationToken);

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
            responseRoot = chunk;
            MergeResponseMetadata(metadata, chunk);

            foreach (var source in CreateSourceEventsFromChunk(providerId, eventId, timestamp, metadata, chunk, emittedSources))
                yield return source;

            if (chunk.TryGetProperty("costDollars", out var costEl) && costEl.ValueKind == JsonValueKind.Object)
                lastUsage = costEl.Clone();

            var delta = target.Backend == "answer"
                ? ExtractAnswerStreamDelta(chunk)
                : ExtractSearchStreamDelta(chunk);

            if (!string.IsNullOrEmpty(delta))
            {
                if (!textStarted)
                {
                    yield return CreateUnifiedStreamEvent(providerId, eventId, "text-start", new AITextStartEventData(), timestamp, metadata);
                    textStarted = true;
                }

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
        {
            yield return CreateUnifiedStreamEvent(providerId, eventId, "text-end", new AITextEndEventData(), timestamp, metadata);
        }
        else if (target.Backend == "search" && responseRoot.ValueKind == JsonValueKind.Object)
        {
            var synthesizedText = ExtractSearchText(responseRoot);
            if (!string.IsNullOrWhiteSpace(synthesizedText))
            {
                yield return CreateUnifiedStreamEvent(providerId, eventId, "text-start", new AITextStartEventData(), timestamp, metadata);
                yield return CreateUnifiedStreamEvent(
                    providerId,
                    eventId,
                    "text-delta",
                    new AITextDeltaEventData { Delta = synthesizedText },
                    timestamp,
                    metadata);
                yield return CreateUnifiedStreamEvent(providerId, eventId, "text-end", new AITextEndEventData(), timestamp, metadata);
            }
        }

        if (target.Backend == "search" && responseRoot.ValueKind == JsonValueKind.Object)
        {
            foreach (var source in CreateAllSourceEventsFromResponse(providerId, eventId, DateTimeOffset.UtcNow, metadata, responseRoot, emittedSources))
                yield return source;
        }

        yield return CreateFinishStreamEvent(providerId, eventId, request, responseRoot, lastUsage, metadata);
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

        if (TryGetSearchResults(root, out var results))
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
                    ["exa.results"] = results.Clone()
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
            MergeResponseMetadata(metadata, rootEl);

        return metadata;
    }

    private static void MergeResponseMetadata(Dictionary<string, object?> metadata, JsonElement rootEl)
    {
        if (rootEl.ValueKind != JsonValueKind.Object)
            return;

        {
            metadata["exa.response.raw"] = rootEl.Clone();
            metadata["exa.requestId"] = CloneProperty(rootEl, "requestId");
            metadata["exa.searchType"] = CloneProperty(rootEl, "searchType");
            metadata["exa.resolvedSearchType"] = CloneProperty(rootEl, "resolvedSearchType");
            metadata["exa.costDollars"] = CloneProperty(rootEl, "costDollars");
            if (TryGetSearchResults(rootEl, out var results))
                metadata["exa.results"] = results.Clone();
            metadata["exa.grounding"] = rootEl.TryGetProperty("output", out var output)
                ? CloneProperty(output, "grounding")
                : null;
        }
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

    private AIStreamEvent CreateFinishStreamEvent(
        string providerId,
        string eventId,
        AIRequest request,
        JsonElement responseRoot,
        object? lastUsage,
        Dictionary<string, object?> metadata)
        => CreateUnifiedStreamEvent(
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
                    usage: lastUsage ?? new Dictionary<string, object?>(),
                    gateway: CreateGatewayMetadata(lastUsage ?? (responseRoot.ValueKind == JsonValueKind.Object ? CloneProperty(responseRoot, "costDollars") : null)))
            },
            DateTimeOffset.UtcNow,
            metadata);

    private static bool IsServerSentEventResponse(HttpResponseMessage response)
        => string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase);

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

    private static string ExtractSearchText(JsonElement root)
    {
        var outputContent = ExtractSearchOutputContent(root);
        return string.IsNullOrWhiteSpace(outputContent) ? BuildSearchResultsText(root) : outputContent;
    }

    private static string BuildSearchResultsText(JsonElement root)
    {
        if (!TryGetSearchResults(root, out var results))
            return string.Empty;

        var lines = new List<string>();
        foreach (var result in results.EnumerateArray())
        {
            var title = TryGetString(result, "title");
            var url = TryGetString(result, "url");
            var summary = TryGetString(result, "summary");
            var textContent = TryGetString(result, "text");
            var highlights = TryGetStringArray(result, "highlights").ToList();
            var body = string.Join("\n\n", highlights.Where(h => !string.IsNullOrWhiteSpace(h)));
            if (string.IsNullOrWhiteSpace(body))
                body = !string.IsNullOrWhiteSpace(summary) ? summary : textContent;

            var header = !string.IsNullOrWhiteSpace(url)
                ? $"[{(string.IsNullOrWhiteSpace(title) ? url : title)}]({url})"
                : title;

            if (!string.IsNullOrWhiteSpace(header))
                lines.Add(header!);
            if (!string.IsNullOrWhiteSpace(body))
                lines.Add(body!);

            if (!string.IsNullOrWhiteSpace(header) || !string.IsNullOrWhiteSpace(body))
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

    private static IEnumerable<JsonElement> ExtractExaResults(Dictionary<string, object?>? metadata)
    {
        if (metadata is null || !metadata.TryGetValue("exa.results", out var raw) || raw is null)
            yield break;

        var element = raw is JsonElement json
            ? json
            : JsonSerializer.SerializeToElement(raw, JsonWeb);

        if (element.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
                yield return item.Clone();
        }
    }

    private async Task<FileUIPart?> DownloadResultImageFilePartAsync(
        JsonElement result,
        string imageUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, imageUrl);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length == 0)
                return null;

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (string.IsNullOrWhiteSpace(mediaType) || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                mediaType = GuessImageMediaTypeFromUrl(imageUrl) ?? MediaTypeNames.Image.Png;

            return new FileUIPart
            {
                MediaType = mediaType,
                Url = Convert.ToBase64String(bytes),
                ProviderMetadata = new Dictionary<string, Dictionary<string, object>?>
                {
                    [GetIdentifier()] = new Dictionary<string, object>
                    {
                        ["image_url"] = imageUrl,
                        ["source_url"] = TryGetString(result, "url") ?? string.Empty,
                        ["title"] = TryGetString(result, "title") ?? string.Empty,
                        ["raw"] = result.Clone()
                    }
                }
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static AIHappey.Unified.Models.AIFinishGatewayMetadata? CreateGatewayMetadata(object? usage)
    {
        if (!TryExtractCost(usage, out var cost))
            return null;

        return new AIFinishGatewayMetadata { Cost = cost };
    }

    private static bool TryExtractCost(object? value, out decimal cost)
    {
        cost = 0m;
        if (value is null)
            return false;

        var json = value is JsonElement element ? element : JsonSerializer.SerializeToElement(value, JsonWeb);
        if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty("total", out var total))
            return false;

        if (total.ValueKind == JsonValueKind.Number && total.TryGetDecimal(out cost))
            return true;

        return total.ValueKind == JsonValueKind.String && decimal.TryParse(total.GetString(), out cost);
    }

    private static IEnumerable<AIStreamEvent> CreateAllSourceEventsFromResponse(
        string providerId,
        string eventId,
        DateTimeOffset timestamp,
        Dictionary<string, object?> metadata,
        JsonElement root,
        HashSet<string> emittedSources)
    {
        if (!TryGetSearchResults(root, out var results))
            yield break;

        foreach (var evt in CreateSourceEvents(providerId, eventId, timestamp, metadata, results, "search_result", emittedSources))
            yield return evt;
    }

    private static string? GetDownloadFileName(HttpResponseMessage response, string url, string mediaType)
    {
        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName;

        if (!string.IsNullOrWhiteSpace(fileName))
            return fileName.Trim('"');

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        fileName = Path.GetFileName(uri.LocalPath);
        if (!string.IsNullOrWhiteSpace(fileName))
            return fileName;

        return GuessImageFileExtension(mediaType) is { } extension
            ? $"exa-image{extension}"
            : null;
    }

    private static string? GuessImageMediaTypeFromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        return Path.GetExtension(uri.LocalPath).ToLowerInvariant() switch
        {
            ".png" => MediaTypeNames.Image.Png,
            ".jpg" or ".jpeg" => MediaTypeNames.Image.Jpeg,
            ".gif" => MediaTypeNames.Image.Gif,
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".svg" => "image/svg+xml",
            _ => null
        };
    }

    private static string? GuessImageFileExtension(string mediaType)
        => mediaType.ToLowerInvariant() switch
        {
            MediaTypeNames.Image.Png => ".png",
            MediaTypeNames.Image.Jpeg => ".jpg",
            "image/jpg" => ".jpg",
            MediaTypeNames.Image.Gif => ".gif",
            "image/webp" => ".webp",
            "image/bmp" => ".bmp",
            "image/svg+xml" => ".svg",
            _ => null
        };

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

    private static bool TryGetSearchResults(JsonElement root, out JsonElement results)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("results", out results)
            && results.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("results", out results)
            && results.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        results = default;
        return false;
    }

    private static ProviderBackendCaptureRequest? GetExaBackendCapture(AIRequest request, string providerId)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            return request.Metadata?.GetProviderOption<ProviderBackendCaptureRequest>(providerId, "capture")
                ?? request.Metadata?.GetProviderOption<ProviderBackendCaptureRequest>(providerId, "backend_capture");
        }
        catch
        {
            return null;
        }
    }
}
