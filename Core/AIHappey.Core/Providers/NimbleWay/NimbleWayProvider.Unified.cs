using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.NimbleWay;

public partial class NimbleWayProvider
{
    private const string AgentModelPrefix = "agent/";

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        var (model, payload) = BuildRequest(request, streaming: false);
        cancellationToken.ThrowIfCancellationRequested();
        if (model == "search")
        {
            var result = await SendJsonAsync(HttpMethod.Post, "v2/search", payload, cancellationToken);
            return MapResponse(request, model, result, result);
        }

        var created = await CreateRunAsync(model, payload, cancellationToken);
        var path = RunPath(created);
        var run = await WaitForRunAsync(path, created, cancellationToken);
        var output = await SendJsonAsync(HttpMethod.Get, $"{path}/result", null, cancellationToken);
        return MapResponse(request, model, output, JsonSerializer.SerializeToElement(new { request = payload, create = created, run, result = output }));
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (model, payload) = BuildRequest(request, streaming: true);
        cancellationToken.ThrowIfCancellationRequested();
        var streamId = request.Id ?? $"nimbleway_{Guid.NewGuid():N}";
        var started = DateTimeOffset.UtcNow;
        AIStreamEvent Event(string type, string id, object data, Dictionary<string, object?>? metadata = null)
        {
            var mapped = metadata is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(metadata);
            mapped["chatcompletions.stream.id"] = streamId;
            mapped["chatcompletions.stream.model"] = $"{GetIdentifier()}/{model}";
            mapped["chatcompletions.stream.created"] = started.ToUnixTimeSeconds();
            return new AIStreamEvent
            {
                ProviderId = GetIdentifier(),
                Event = new AIEventEnvelope { Type = type, Id = id, Timestamp = DateTimeOffset.UtcNow, Data = data, Metadata = mapped },
                Metadata = mapped
            };
        }
        AIResponse response;
        if (model == "search")
        {
            var result = await SendJsonAsync(HttpMethod.Post, "v2/search", payload, cancellationToken);
            streamId = request.Id ?? String(result, "request_id") ?? streamId;
            response = MapResponse(request, model, result, result);
        }
        else
        {
            var created = await CreateRunAsync(model, payload, cancellationToken);
            var path = RunPath(created);
            var id = RequiredString(created, "id");
            streamId = request.Id ?? id;
            yield return Event("tool-input-start", id, new AIToolInputStartEventData
            { ToolName = "agent_run", ProviderExecuted = true, ProviderMetadata = NestedMetadata(created) });
            yield return Event("tool-input-available", id, new AIToolInputAvailableEventData
            { ToolName = "agent_run", Input = payload, ProviderExecuted = true, ProviderMetadata = NestedMetadata(created) });

            var events = new List<JsonElement>();
            // Honor an explicit opt-out; polling remains authoritative even after the SSE connection closes.
            if (JsonSerializer.SerializeToElement(payload["enable_events"]).ValueKind == JsonValueKind.True)
            {
                await foreach (var raw in ReadRunEventsAsync(path, cancellationToken))
                {
                    events.Add(raw);
                    yield return Event("tool-output-available", id, new AIToolOutputAvailableEventData
                    {
                        ToolName = "agent_run", Output = raw, ProviderExecuted = true,
                        Preliminary = true, ProviderMetadata = NestedMetadata(raw)
                    });
                }
            }
            var run = await WaitForRunAsync(path, created, cancellationToken);
            var output = await SendJsonAsync(HttpMethod.Get, $"{path}/result", null, cancellationToken);
            response = MapResponse(request, model, output,
                JsonSerializer.SerializeToElement(new { request = payload, create = created, run, result = output, events }));
            yield return Event("tool-output-available", id, new AIToolOutputAvailableEventData
            { ToolName = "agent_run", Output = output, ProviderExecuted = true, ProviderMetadata = NestedMetadata(output) });
        }

        var textId = model == "search" ? streamId : $"{streamId}_text";
        var metadata = response.Metadata!;
        var providerMetadata = new Dictionary<string, object> { [GetIdentifier()] = metadata[GetIdentifier()]! };
        yield return Event("text-start", textId, new AITextStartEventData { ProviderMetadata = providerMetadata }, metadata);
        var text = response.Output!.Items![0].Content!.OfType<AITextContentPart>().Single().Text;
        yield return Event("text-delta", textId, new AITextDeltaEventData { Delta = text, ProviderMetadata = providerMetadata }, metadata);
        yield return Event("text-end", textId, new AITextEndEventData { ProviderMetadata = providerMetadata }, metadata);
        foreach (var source in response.Output.Items.Skip(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = (string)source.Metadata!["chatcompletions.source.url"]!;
            yield return Event("source-url", textId, new AISourceUrlEventData
            {
                SourceId = url, Url = url, Title = source.Metadata["chatcompletions.source.title"]?.ToString(),
                Type = "url_citation", ProviderMetadata = NestedMetadata((JsonElement)source.Metadata[GetIdentifier()]!)
            }, metadata);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var timestamp = DateTimeOffset.UtcNow;
        var finishMetadata = new Dictionary<string, object?>(metadata)
        { ["chatcompletions.stream.raw"] = JsonSerializer.SerializeToElement(new { provider_metadata = providerMetadata }) };
        yield return Event("finish", textId, new AIFinishEventData
        {
            FinishReason = "stop", Model = response.Model, CompletedAt = timestamp.ToUnixTimeSeconds(),
            MessageMetadata = AIFinishMessageMetadata.Create(response.Model!, timestamp,
                temperature: request.Temperature, additionalProperties: new() { [GetIdentifier()] = metadata[GetIdentifier()] })
        }, finishMetadata);
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamImmutableRunEventsAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var index = 0;
        await foreach (var part in StreamUnifiedAsync(request, cancellationToken))
        {
            if (part.Event.Data is not AIToolOutputAvailableEventData { Preliminary: true } progress)
            {
                yield return part;
                continue;
            }

            // Messages/Responses results are immutable. Give each live event its own
            // input/result pair so progress cannot finalize the main run prematurely.
            var id = $"{part.Event.Id}_event_{index++}";
            yield return new AIStreamEvent
            {
                ProviderId = GetIdentifier(), Metadata = part.Metadata,
                Event = new AIEventEnvelope
                {
                    Type = "tool-input-available", Id = id, Timestamp = part.Event.Timestamp, Metadata = part.Event.Metadata,
                    Data = new AIToolInputAvailableEventData
                    {
                        ToolName = "agent_run_event", Input = progress.Output,
                        ProviderExecuted = true, ProviderMetadata = progress.ProviderMetadata
                    }
                }
            };
            cancellationToken.ThrowIfCancellationRequested();
            yield return new AIStreamEvent
            {
                ProviderId = GetIdentifier(), Metadata = part.Metadata,
                Event = new AIEventEnvelope
                {
                    Type = "tool-output-available", Id = id, Timestamp = part.Event.Timestamp, Metadata = part.Event.Metadata,
                    Data = new AIToolOutputAvailableEventData
                    {
                        ToolName = "agent_run_event", Output = progress.Output,
                        ProviderExecuted = true, Preliminary = false, ProviderMetadata = progress.ProviderMetadata
                    }
                }
            };
        }
    }

    private (string Model, Dictionary<string, object?> Payload) BuildRequest(AIRequest request, bool streaming)
    {
        ArgumentNullException.ThrowIfNull(request);
        var model = request.Model?.Trim() ?? "search";
        var prefix = GetIdentifier() + "/";
        if (model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            model = model[prefix.Length..];
        if (model != "search" && (!model.StartsWith(AgentModelPrefix, StringComparison.Ordinal) || model.Length == AgentModelPrefix.Length))
            throw new NotSupportedException($"Unsupported NimbleWay model '{request.Model}'.");

        var prompt = BuildPrompt(request);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("NimbleWay requires non-empty text input.");
        var payload = ProviderOptions(request);
        payload[model == "search" ? "query" : "input"] = prompt;
        if (model != "search")
        {
            payload.TryAdd("enable_events", streaming);
            var responseFormat = request.ResponseFormat;
            if (responseFormat is null && request.Metadata?.TryGetValue("messages.request.output_config", out var config) == true && config is not null)
            {
                var outputConfig = JsonSerializer.SerializeToElement(config, JsonSerializerOptions.Web);
                if (outputConfig.ValueKind == JsonValueKind.Object && outputConfig.TryGetProperty("format", out var format))
                    responseFormat = format.Clone();
            }
            if (!payload.ContainsKey("output_schema") && responseFormat is not null)
            {
                var format = JsonSerializer.SerializeToElement(responseFormat, JsonSerializerOptions.Web);
                if (format.ValueKind == JsonValueKind.Object)
                {
                    if (format.TryGetProperty("json_schema", out var wrapped) && wrapped.ValueKind == JsonValueKind.Object)
                        format = wrapped;
                    if (format.TryGetProperty("schema", out var schema))
                        payload["output_schema"] = schema.Clone();
                }
            }
        }
        return (model, payload);
    }

    private Dictionary<string, object?> ProviderOptions(AIRequest request)
    {
        if (request.Metadata?.TryGetValue(GetIdentifier(), out var direct) == true)
            return ObjectOptions(direct);
        foreach (var key in new[] { "chatcompletions.request.metadata", "messages.request.metadata",
            "chatcompletions.request.provider_metadata", "messages.request.provider_metadata" })
        {
            if (request.Metadata?.TryGetValue(key, out var value) != true || value is null)
                continue;
            var root = JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(GetIdentifier(), out var provider))
                return ObjectOptions(provider);
        }
        return [];
    }

    private static Dictionary<string, object?> ObjectOptions(object? value)
    {
        var raw = JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
        if (raw.ValueKind == JsonValueKind.Null)
            return [];
        if (raw.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("NimbleWay provider metadata must be a JSON object.");
        return raw.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone());
    }

    private static string BuildPrompt(AIRequest request)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            lines.Add(request.Instructions);
        if (!string.IsNullOrWhiteSpace(request.Input?.Text))
            lines.Add(request.Input.Text);
        else
        {
            var messages = request.Input?.Items?.Where(item => item.Type == "message"
                && item.Role is "user" or "assistant" or "system" or "developer").ToList() ?? [];
            foreach (var message in messages)
            {
                var text = string.Join("\n", (message.Content ?? []).OfType<AITextContentPart>().Select(part => part.Text));
                if (!string.IsNullOrWhiteSpace(text))
                    lines.Add(messages.Count == 1 ? text : $"{message.Role}: {text}");
            }
        }
        return string.Join("\n\n", lines);
    }

    private Task<JsonElement> CreateRunAsync(string model, Dictionary<string, object?> payload, CancellationToken ct)
    {
        var agent = model[AgentModelPrefix.Length..];
        if (agent.StartsWith("wsa_", StringComparison.Ordinal))
            return SendJsonAsync(HttpMethod.Post, $"v2/agents/{Uri.EscapeDataString(agent)}/runs", payload, ct);
        payload["agent_name"] = agent;
        return SendJsonAsync(HttpMethod.Post, "v2/agents/runs", payload, ct);
    }

    private static string RunPath(JsonElement run)
        => $"v2/agents/{Uri.EscapeDataString(RequiredString(run, "web_search_agent_id"))}/runs/{Uri.EscapeDataString(RequiredString(run, "id"))}";

    private async Task<JsonElement> WaitForRunAsync(string path, JsonElement run, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var status = RequiredString(run, "status");
            if (status == "completed")
                return run;
            if (status is "failed" or "cancelled")
                throw new InvalidOperationException($"NimbleWay agent run {status}: {run.GetRawText()}");
            if (status is not ("queued" or "running"))
                throw new InvalidOperationException($"Unknown NimbleWay run status '{status}': {run.GetRawText()}");
            run = await SendJsonAsync(HttpMethod.Get, path, null, ct);
            if (String(run, "status") is "queued" or "running")
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    private async Task<JsonElement> SendJsonAsync(HttpMethod method, string path, Dictionary<string, object?>? payload, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        ApplyAuthHeader(request);
        if (payload is not null)
            request.Content = JsonContent.Create(payload, options: JsonSerializerOptions.Web);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"NimbleWay {path} failed ({(int)response.StatusCode}): {body}", null, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"NimbleWay {path} returned a non-object response.");
        return document.RootElement.Clone();
    }

    private async IAsyncEnumerable<JsonElement> ReadRunEventsAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{path}/events");
        ApplyAuthHeader(request);
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"NimbleWay events failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(ct)}", null, response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var data = new List<string>();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Count > 0)
                {
                    yield return ParseEvent(data);
                    data.Clear();
                }
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
                data.Add(line[5..].TrimStart());
        }
        if (data.Count > 0)
            yield return ParseEvent(data);
    }

    private static JsonElement ParseEvent(List<string> lines)
    {
        using var document = JsonDocument.Parse(string.Join("\n", lines));
        return document.RootElement.Clone();
    }

    private AIResponse MapResponse(AIRequest request, string model, JsonElement result, JsonElement raw)
    {
        var sources = new List<JsonElement>();
        var text = new StringBuilder();
        if (model == "search")
        {
            if (String(result, "answer") is { } answer)
                text.AppendLine(answer);
            if (!result.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("NimbleWay search response is missing its results array.");
            foreach (var item in results.EnumerateArray())
            {
                if (String(item, "url") is { Length: > 0 } url)
                {
                    sources.Add(item.Clone());
                    text.AppendLine().AppendLine($"Title: {String(item, "title") ?? url}").AppendLine($"URL: {url}");
                }
                var content = String(item, "content");
                if (!string.IsNullOrWhiteSpace(content))
                    text.AppendLine(content);
                else if (String(item, "description") is { } description)
                    text.AppendLine(description);
            }
        }
        else
        {
            if (!result.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Object
                || !output.TryGetProperty("content", out var content) || content.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                throw new InvalidOperationException("NimbleWay completed run is missing its output content.");
            text.Append(content.ValueKind == JsonValueKind.String ? content.GetString() : content.GetRawText());
            if (output.TryGetProperty("trust", out var trust) && trust.ValueKind == JsonValueKind.Object)
            {
                if (trust.TryGetProperty("sources", out var sourceList) && sourceList.ValueKind == JsonValueKind.Array)
                    sources.AddRange(sourceList.EnumerateArray().Select(source => source.Clone()));
                if (trust.TryGetProperty("claims", out var claims) && claims.ValueKind == JsonValueKind.Array)
                    foreach (var claim in claims.EnumerateArray())
                        if (claim.TryGetProperty("citations", out var citations) && citations.ValueKind == JsonValueKind.Array)
                            sources.AddRange(citations.EnumerateArray().Select(source => source.Clone()));
            }
        }

        var scoped = new Dictionary<string, object?> { [GetIdentifier()] = raw };
        var metadata = new Dictionary<string, object?>(scoped)
        {
            ["responses.completed_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["responses.temperature"] = request.Temperature,
            ["chatcompletions.response.raw"] = JsonSerializer.SerializeToElement(new { provider_metadata = scoped })
        };
        var responseId = model == "search" ? String(result, "request_id")
            : raw.TryGetProperty("create", out var created) ? String(created, "id") : null;
        if (!string.IsNullOrWhiteSpace(responseId))
        {
            metadata["chatcompletions.response.id"] = responseId;
            metadata["responses.id"] = responseId;
            metadata["messages.response.id"] = responseId;
        }
        var items = new List<AIOutputItem>
        {
            new()
            {
                Role = "assistant",
                Content = [new AITextContentPart { Type = "text", Text = text.ToString().Trim(), Metadata = scoped }],
                Metadata = new()
                {
                    ["chatcompletions.choice.finish_reason"] = "stop",
                    ["chatcompletions.message.provider_metadata"] = scoped
                }
            }
        };
        foreach (var source in sources.Where(source => !string.IsNullOrWhiteSpace(String(source, "url")))
            .DistinctBy(source => String(source, "url")))
        {
            items.Add(new AIOutputItem
            {
                Type = "source-url",
                Metadata = new()
                {
                    ["chatcompletions.source.url"] = String(source, "url"),
                    ["chatcompletions.source.title"] = String(source, "title"),
                    [GetIdentifier()] = source
                }
            });
        }
        return new AIResponse
        {
            ProviderId = GetIdentifier(), Model = $"{GetIdentifier()}/{model}", Status = "completed",
            Output = new AIOutput { Items = items }, Metadata = metadata
        };
    }

    private Dictionary<string, Dictionary<string, object>> NestedMetadata(JsonElement raw)
        => new() { [GetIdentifier()] = raw.ValueKind == JsonValueKind.Object
            ? raw.EnumerateObject().ToDictionary(property => property.Name, property => (object)property.Value.Clone())
            : new() { ["raw"] = raw } };

    private static string? String(JsonElement value, string key)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;

    private static string RequiredString(JsonElement value, string key)
        => String(value, key) is { Length: > 0 } text ? text
            : throw new InvalidOperationException($"NimbleWay response is missing '{key}': {value.GetRawText()}");
}
