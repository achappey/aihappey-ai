using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.YouCom;

public partial class YouComProvider
{
    private const string AnswerPath = "v1/answer";
    private const string ResearchPath = "v1/research";
    private const string FinanceResearchPath = "v1/finance_research";
    private static readonly JsonSerializerOptions YouComJson = new(JsonSerializerOptions.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(YouCom)} API key.");

        _client.DefaultRequestHeaders.Authorization = null;
        _client.DefaultRequestHeaders.Remove("X-API-Key");
        _client.DefaultRequestHeaders.Add("X-API-Key", key);
    }

    private static string NormalizeModel(string? model)
    {
        var value = model?.Trim() ?? string.Empty;
        var slash = value.IndexOf('/');
        return (slash >= 0 ? value[(slash + 1)..] : value).ToLowerInvariant();
    }

    private static string BuildPrompt(AIRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Input?.Text))
            return request.Input.Text!;

        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            lines.Add($"system: {request.Instructions}");

        foreach (var item in request.Input?.Items ?? [])
        {
            var text = string.Join("\n", (item.Content ?? []).OfType<AITextContentPart>()
                .Select(part => part.Text).Where(text => !string.IsNullOrWhiteSpace(text)));
            if (!string.IsNullOrWhiteSpace(text))
                lines.Add($"{item.Role ?? "user"}: {text}");
        }

        return string.Join("\n\n", lines);
    }

    private static string BuildAnswerQuery(AIRequest request)
    {
        var lastUserMessage = request.Input?.Items?
            .LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));

        if (lastUserMessage is not null)
        {
            return string.Join("\n", (lastUserMessage.Content ?? [])
                .OfType<AITextContentPart>()
                .Select(part => part.Text)
                .Where(text => !string.IsNullOrWhiteSpace(text)));
        }

        return request.Input?.Text ?? string.Empty;
    }

    private static Dictionary<string, object?> ReadOptions(AIRequest request)
    {
        if (request.Metadata is null)
            return [];

        if (!request.Metadata.TryGetValue("youcom", out var nested) || nested is null)
            return new Dictionary<string, object?>(request.Metadata, StringComparer.OrdinalIgnoreCase);

        try
        {
            return JsonSerializer.SerializeToElement(nested, YouComJson)
                .Deserialize<Dictionary<string, object?>>(YouComJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static object? GetOption(Dictionary<string, object?> options, params string[] names)
    {
        foreach (var name in names)
        {
            var match = options.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase));
            if (match.Key is not null)
                return ToPlainObject(match.Value);
        }
        return null;
    }

    private static object? ToPlainObject(object? value)
    {
        if (value is not JsonElement element)
            return value;
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ToPlainObject(p.Value)),
            JsonValueKind.Array => element.EnumerateArray().Select(item => ToPlainObject(item)).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static object? ExtractOutputSchema(AIRequest request)
    {
        var schema = request.ResponseFormat.GetJSONSchema()?.JsonSchema?.Schema;
        return schema is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined }
            ? ToPlainObject(schema.Value)
            : null;
    }

    private static Dictionary<string, object?> BuildPayload(AIRequest request, string model, string input)
    {
        var options = ReadOptions(request);
        var payload = new Dictionary<string, object?>();

        if (model == "answer")
        {
            payload["query"] = input;
            Copy(options, payload, "freshness", "freshness");
            Copy(options, payload, "country", "country");
            Copy(options, payload, "language", "language");
            Copy(options, payload, "safesearch", "safesearch", "safeSearch");
            Copy(options, payload, "include_domains", "include_domains", "includeDomains");
            Copy(options, payload, "exclude_domains", "exclude_domains", "excludeDomains");
            Copy(options, payload, "boost_domains", "boost_domains", "boostDomains");
            return payload;
        }

        if (model.StartsWith("finance-", StringComparison.Ordinal))
        {
            payload["input"] = input;
            payload["research_effort"] = model["finance-".Length..] switch
            {
                "exhaustive" => "exhaustive",
                _ => "deep"
            };
            return payload;
        }

        payload["input"] = input;
        payload["research_effort"] = model["research-".Length..];
        var sourceControl = GetOption(options, "source_control", "sourceControl");
        if (sourceControl is not null)
            payload["source_control"] = sourceControl;
        var outputSchema = ExtractOutputSchema(request) ?? GetOption(options, "output_schema", "outputSchema");
        if (outputSchema is not null)
            payload["output_schema"] = outputSchema;
        if (model == "research-frontier")
            payload["background"] = true;
        return payload;
    }

    private static void Copy(Dictionary<string, object?> source, Dictionary<string, object?> target,
        string targetName, params string[] sourceNames)
    {
        var value = GetOption(source, sourceNames);
        if (value is not null)
            target[targetName] = value;
    }

    private async Task<JsonElement> SendJsonAsync(HttpMethod method, string path, object? payload,
        string operation, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(method, path);
        if (payload is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(payload, YouComJson), Encoding.UTF8, MediaTypeNames.Application.Json);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"You.com {operation} failed ({(int)response.StatusCode}): {text}");
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException($"You.com {operation} returned an empty response.");
        return JsonSerializer.Deserialize<JsonElement>(text, YouComJson).Clone();
    }

    private static string? String(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static YouComResult ParseResult(JsonElement root, string model)
    {
        if (model == "answer")
        {
            var sources = new List<YouComSource>();
            if (root.TryGetProperty("citations", out var citations) && citations.ValueKind == JsonValueKind.Array)
                foreach (var item in citations.EnumerateArray())
                    if (String(item, "source") is { Length: > 0 } url)
                        sources.Add(new(url, null, GetStrings(item, "excerpts"), item.Clone()));
            if (root.TryGetProperty("results", out var results) && results.TryGetProperty("web", out var web))
                foreach (var item in web.EnumerateArray())
                    if (String(item, "url") is { Length: > 0 } url && sources.All(s => !string.Equals(s.Url, url, StringComparison.OrdinalIgnoreCase)))
                        sources.Add(new(url, String(item, "title"), GetStrings(item, "snippets"), item.Clone()));
            return new(String(root, "answer") ?? string.Empty, null, sources, [], root.Clone());
        }

        var output = root.TryGetProperty("output", out var outputElement) ? outputElement : root;
        object? structured = null;
        var text = string.Empty;
        if (output.TryGetProperty("content", out var content))
        {
            if (content.ValueKind == JsonValueKind.String) text = content.GetString() ?? string.Empty;
            else { structured = ToPlainObject(content); text = content.GetRawText(); }
        }
        var researchSources = new List<YouComSource>();
        if (output.TryGetProperty("sources", out var array) && array.ValueKind == JsonValueKind.Array)
            foreach (var item in array.EnumerateArray())
                if (String(item, "url") is { Length: > 0 } url)
                    researchSources.Add(new(url, String(item, "title"), GetStrings(item, "snippets"), item.Clone()));
        var warnings = root.TryGetProperty("warnings", out var warningArray) && warningArray.ValueKind == JsonValueKind.Array
            ? warningArray.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList()
            : [];
        return new(text, structured, researchSources, warnings, root.Clone());
    }

    private static List<string> GetStrings(JsonElement element, string name)
        => element.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList()
            : [];

    private static AIOutputItem SourceItem(YouComSource source) => new()
    {
        Type = "source-url",
        Role = "assistant",
        Content = [new AITextContentPart { Type = "text", Text = source.Title ?? source.Url }],
        Metadata = new Dictionary<string, object?>
        {
            ["chatcompletions.source.url"] = source.Url,
            ["chatcompletions.source.title"] = source.Title,
            ["messages.source.url"] = source.Url,
            ["messages.source.title"] = source.Title,
            ["youcom.source.snippets"] = source.Snippets,
            ["youcom.source.raw"] = source.Raw
        }
    };

    private AIResponse ToUnifiedResponse(AIRequest request, string model, YouComResult result)
    {
        var items = new List<AIOutputItem>();
        if (!string.IsNullOrWhiteSpace(result.Text) || result.Structured is not null)
            items.Add(new AIOutputItem
            {
                Type = "message",
                Role = "assistant",
                Content = [new AITextContentPart { Type = "text", Text = result.Text, Metadata = result.Structured is null ? null : new() { ["youcom.structured_output"] = result.Structured } }]
            });
        items.AddRange(result.Sources.Select(SourceItem));
        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = model.ToModelId(GetIdentifier()),
            Status = "completed",
            Usage = new AIUsage(),
            Output = new AIOutput { Items = items },
            Metadata = new Dictionary<string, object?>
            {
                ["youcom.model"] = model,
                ["youcom.warnings"] = result.Warnings,
                ["youcom.structured_output"] = result.Structured,
                ["youcom.response.raw"] = result.Raw
            }
        };
    }

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Tools?.Count > 0)
            throw new NotSupportedException("You.com endpoint models do not accept conversation tool definitions.");
        var model = NormalizeModel(request.Model);
        var input = model == "answer" ? BuildAnswerQuery(request) : BuildPrompt(request);
        if (string.IsNullOrWhiteSpace(input))
            throw new InvalidOperationException("You.com requires non-empty text input.");

        if (model == "research-frontier")
        {
            AIResponse? completed = null;
            await foreach (var evt in StreamFrontierAsync(request, input, cancellationToken))
                if (evt.Event.Output is { } output)
                    completed = new AIResponse { ProviderId = GetIdentifier(), Model = request.Model, Status = "completed", Output = output, Usage = new AIUsage(), Metadata = evt.Metadata };
            return completed ?? throw new InvalidOperationException("You.com frontier research completed without output.");
        }
        var path = model == "answer" ? AnswerPath : model.StartsWith("finance-", StringComparison.Ordinal) ? FinanceResearchPath : ResearchPath;
        if (model != "answer" && !model.StartsWith("research-", StringComparison.Ordinal) && !model.StartsWith("finance-", StringComparison.Ordinal))
            throw new NotSupportedException($"Unsupported You.com model '{request.Model}'.");
        var root = await SendJsonAsync(HttpMethod.Post, path, BuildPayload(request, model, input), model, cancellationToken);
        return ToUnifiedResponse(request, model, ParseResult(root, model));
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var input = BuildPrompt(request);

        if (NormalizeModel(request.Model) == "research-frontier")
        {
            await foreach (var evt in StreamFrontierAsync(request, input, cancellationToken)) yield return evt;
            yield break;
        }

        var response = await ExecuteUnifiedAsync(request, cancellationToken);
        foreach (var evt in ToSyntheticEvents(response, request)) yield return evt;
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamFrontierAsync(AIRequest request, string input,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var queued = await SendJsonAsync(HttpMethod.Post, ResearchPath, BuildPayload(request, "research-frontier", input), "frontier submission", cancellationToken);
        var taskId = String(queued, "task_id") ?? throw new InvalidOperationException("You.com frontier response omitted task_id.");
        var streamUrl = String(queued, "stream_url") ?? $"{ResearchPath}/{Uri.EscapeDataString(taskId)}/stream";
        yield return Event("data-youcom-research-task", taskId, new AIDataEventData { Id = taskId, Data = ToPlainObject(queued)!, Transient = true }, null);

        JsonElement? completed = null;
        ApplyAuthHeader();
        using (var streamRequest = new HttpRequestMessage(HttpMethod.Get, streamUrl))
        using (var response = await _client.SendAsync(streamRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var reader = new StreamReader(stream);
                while (await reader.ReadLineAsync(cancellationToken) is { } line)
                {
                    if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                    var data = line[5..].Trim();
                    if (data is "" or "[DONE]") continue;
                    JsonElement? json = null;
                    try { json = JsonSerializer.Deserialize<JsonElement>(data, YouComJson).Clone(); }
                    catch (JsonException) { }
                    if (json is not { } parsed) continue;
                    if (parsed.TryGetProperty("output", out _)) completed = parsed;
                    else yield return Event("data-youcom-research-progress", taskId, new AIDataEventData { Id = taskId, Data = ToPlainObject(parsed)!, Transient = true }, null);
                }
            }
        }

        completed ??= await PollFrontierAsync(taskId, cancellationToken);
        var unified = ToUnifiedResponse(request, "research-frontier", ParseResult(completed.Value, "research-frontier"));
        foreach (var evt in ToSyntheticEvents(unified, request, includeOutputOnFinish: true)) yield return evt;
    }

    private async Task<JsonElement> PollFrontierAsync(string taskId, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(2);
        while (true)
        {
            var task = await SendJsonAsync(HttpMethod.Get, $"{ResearchPath}/{Uri.EscapeDataString(taskId)}", null, "frontier polling", cancellationToken);
            var status = String(task, "status");
            if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase) || task.TryGetProperty("output", out _)) return task;
            if (status is "failed" or "cancelled") throw new InvalidOperationException($"You.com frontier task '{taskId}' ended with status '{status}'.");
            await Task.Delay(delay, cancellationToken);
            delay = TimeSpan.FromSeconds(Math.Min(10, delay.TotalSeconds * 1.5));
        }
    }

    private IEnumerable<AIStreamEvent> ToSyntheticEvents(AIResponse response, AIRequest request, bool includeOutputOnFinish = false)
    {
        var id = request.Id ?? $"youcom_{Guid.NewGuid():N}";
        var timestamp = DateTimeOffset.UtcNow;
        var text = string.Concat(response.Output?.Items?.Where(i => i.Type == "message").SelectMany(i => i.Content ?? []).OfType<AITextContentPart>().Select(p => p.Text) ?? []);
        if (!string.IsNullOrEmpty(text))
        {
            yield return Event("text-start", id, new AITextStartEventData(), response.Metadata, timestamp);
            yield return Event("text-delta", id, new AITextDeltaEventData { Delta = text }, response.Metadata, timestamp);
            yield return Event("text-end", id, new AITextEndEventData(), response.Metadata, timestamp);
        }
        foreach (var item in response.Output?.Items ?? [])
        {
            if (item.Type == "source-url" && item.Metadata?.TryGetValue("chatcompletions.source.url", out var rawUrl) == true && rawUrl?.ToString() is { Length: > 0 } url)
                yield return Event("source-url", url, new AISourceUrlEventData { SourceId = url, Url = url, Title = item.Metadata.GetValueOrDefault("chatcompletions.source.title")?.ToString(), Type = "url_citation" }, response.Metadata, timestamp);
            foreach (var file in (item.Content ?? []).OfType<AIFileContentPart>())
                if (file.Data?.ToString() is { Length: > 0 } fileUrl)
                    yield return Event("file", $"{id}-file-{Guid.NewGuid():N}", new AIFileEventData { MediaType = file.MediaType ?? "application/octet-stream", Url = fileUrl, Filename = file.Filename }, response.Metadata, timestamp);
        }
        if (response.Metadata?.GetValueOrDefault("youcom.structured_output") is { } structured)
            yield return Event("data-youcom.structured-output", id, new AIDataEventData { Id = id, Data = structured }, response.Metadata, timestamp);
        yield return new AIStreamEvent
        {
            ProviderId = GetIdentifier(),
            Metadata = response.Metadata,
            Event = new AIEventEnvelope
            {
                Type = "finish",
                Id = id,
                Timestamp = timestamp,
                Output = includeOutputOnFinish ? response.Output : null,
                Data = new AIFinishEventData { FinishReason = "stop", Model = response.Model, CompletedAt = timestamp.ToUnixTimeSeconds(), InputTokens = 0, OutputTokens = 0, TotalTokens = 0, MessageMetadata = AIFinishMessageMetadata.Create(response.Model, timestamp, inputTokens: 0, outputTokens: 0, totalTokens: 0, temperature: request.Temperature) }
            }
        };
    }

    private AIStreamEvent Event(string type, string id, object data, Dictionary<string, object?>? metadata,
        DateTimeOffset? timestamp = null) => new()
        {
            ProviderId = GetIdentifier(),
            Metadata = metadata,
            Event = new AIEventEnvelope { Type = type, Id = id, Timestamp = timestamp ?? DateTimeOffset.UtcNow, Data = data, Metadata = metadata }
        };

    private sealed record YouComSource(string Url, string? Title, IReadOnlyList<string> Snippets, JsonElement Raw);
    private sealed record YouComResult(string Text, object? Structured, List<YouComSource> Sources, List<string> Warnings, JsonElement Raw);
}
