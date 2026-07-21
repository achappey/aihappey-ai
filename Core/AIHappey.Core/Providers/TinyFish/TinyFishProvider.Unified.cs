using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.TinyFish;

public partial class TinyFishProvider
{
    private const string AgentModel = "agent";
    private const string FetchModel = "fetch";
    private const string FetchEndpoint = "https://api.fetch.tinyfish.ai/";
    private static readonly JsonSerializerOptions TinyFishJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ApplyAuthHeader();

        var target = ResolveTarget(request);
        return target.Kind == TinyFishTargetKind.Agent
            ? await ExecuteAgentAsync(request, target.Metadata, cancellationToken)
            : await ExecuteFetchAsync(request, target.Metadata, cancellationToken);
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ApplyAuthHeader();

        var target = ResolveTarget(request);
        if (target.Kind == TinyFishTargetKind.Agent)
        {
            await foreach (var streamEvent in StreamAgentAsync(request, target.Metadata, cancellationToken))
                yield return streamEvent;
            yield break;
        }

        var execution = await FetchAsync(target.Metadata, cancellationToken);
        var eventId = request.Id ?? $"tinyfish_fetch_{Guid.NewGuid():N}";
        var timestamp = DateTimeOffset.UtcNow;
        var metadata = CreateFetchMetadata(request, target.Metadata, execution);

        foreach (var error in execution.Errors)
        {
            yield return CreateStreamEvent("data-tinyfish.fetch-error", eventId, new AIDataEventData
            {
                Id = error.Url,
                Data = ToPlainObject(error.Raw),
                Transient = false
            }, timestamp, metadata);
        }

        var emittedImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in execution.Results)
        {
            var resultId = CreateResultId(eventId, result.Url);
            var resultMetadata = CreateResultMetadata(metadata, result);
            var text = ToFetchResultText(result);

            yield return CreateStreamEvent("text-start", resultId, new AITextStartEventData(), timestamp, resultMetadata);
            yield return CreateStreamEvent("text-delta", resultId, new AITextDeltaEventData { Delta = text }, timestamp, resultMetadata);
            yield return CreateStreamEvent("text-end", resultId, new AITextEndEventData(), timestamp, resultMetadata);
            yield return CreateSourceStreamEvent(resultId, result, timestamp, resultMetadata);

            foreach (var imageUrl in result.ImageLinks.Where(url => emittedImages.Add(url)))
            {
                var image = await TryDownloadImageAsync(imageUrl, result, cancellationToken);
                if (image is not null)
                    yield return CreateFileStreamEvent(resultId, image, timestamp, resultMetadata);
            }
        }

        yield return CreateFinishStreamEvent(eventId, request, "stop", timestamp, metadata);
    }

    private async Task<AIResponse> ExecuteAgentAsync(AIRequest request, TinyFishProviderMetadata metadata, CancellationToken cancellationToken)
    {
        var goal = BuildPrompt(request);
        if (string.IsNullOrWhiteSpace(goal))
            throw new InvalidOperationException("TinyFish agent requires a non-empty goal derived from unified input or instructions.");

        var payload = BuildAgentPayload(metadata, goal, request.ResponseFormat);
        using var httpRequest = CreateJsonRequest(HttpMethod.Post, "v1/automation/run", payload);
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"TinyFish agent run failed ({(int)response.StatusCode}): {body}");

        var run = ParseAgentRun(body);
        var text = run.Status.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase)
            ? ToDisplayText(run.Result)
            : run.Error ?? $"TinyFish agent run ended with status '{run.Status}'.";
        var responseMetadata = CreateAgentMetadata(request, metadata, payload, run);
        var items = new List<AIOutputItem>
        {
            CreateMessageItem(text, new Dictionary<string, object?> { ["tinyfish.agent.result"] = run.Result })
        };
        items.Add(CreateSourceOutputItem(metadata.Url!, metadata.Url!, "target_url"));

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = ToUnifiedModel(AgentModel),
            Status = run.Status.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase) ? "completed" : "failed",
            Usage = new Dictionary<string, object?> { ["steps"] = run.NumOfSteps },
            Metadata = responseMetadata,
            Output = new AIOutput { Items = items }
        };
    }

    private async Task<AIResponse> ExecuteFetchAsync(AIRequest request, TinyFishProviderMetadata metadata, CancellationToken cancellationToken)
    {
        var execution = await FetchAsync(metadata, cancellationToken);
        var output = new List<AIOutputItem>();
        var emittedImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in execution.Results)
        {
            output.Add(CreateMessageItem(ToFetchResultText(result), CreateFetchResultPartMetadata(result)));
            output.Add(CreateSourceOutputItem(result.FinalUrl ?? result.Url, result.Title ?? result.FinalUrl ?? result.Url, "fetch_result", result));

            foreach (var imageUrl in result.ImageLinks.Where(url => emittedImages.Add(url)))
            {
                var image = await TryDownloadImageAsync(imageUrl, result, cancellationToken);
                if (image is not null)
                    output.Add(CreateImageOutputItem(image));
            }
        }

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = ToUnifiedModel(FetchModel),
            Status = execution.Errors.Count == 0 ? "completed" : execution.Results.Count > 0 ? "completed" : "failed",
            Usage = new Dictionary<string, object?> { ["results"] = execution.Results.Count, ["errors"] = execution.Errors.Count },
            Metadata = CreateFetchMetadata(request, metadata, execution),
            Output = new AIOutput { Items = output }
        };
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamAgentAsync(
        AIRequest request,
        TinyFishProviderMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var goal = BuildPrompt(request);
        if (string.IsNullOrWhiteSpace(goal))
            throw new InvalidOperationException("TinyFish agent requires a non-empty goal derived from unified input or instructions.");

        var payload = BuildAgentPayload(metadata, goal, request.ResponseFormat);
        var eventId = request.Id ?? $"tinyfish_agent_{Guid.NewGuid():N}";
        var baseMetadata = new Dictionary<string, object?>
        {
            ["tinyfish.request.payload"] = payload,
            ["tinyfish.target.url"] = metadata.Url!
        };

        using var httpRequest = CreateJsonRequest(HttpMethod.Post, "v1/automation/run-sse", payload);
        httpRequest.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"TinyFish agent stream failed ({(int)response.StatusCode}): {error}");
        }

        TinyFishAgentRun? completed = null;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                break;
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            var data = line["data:".Length..].Trim();
            if (string.IsNullOrWhiteSpace(data))
                continue;

            TinyFishSseEvent? tinyFishEvent;
            try { tinyFishEvent = JsonSerializer.Deserialize<TinyFishSseEvent>(data, TinyFishJson); }
            catch (JsonException) { continue; }
            if (tinyFishEvent is null)
                continue;

            var timestamp = tinyFishEvent.Timestamp ?? DateTimeOffset.UtcNow;
            var eventMetadata = new Dictionary<string, object?>(baseMetadata)
            {
                ["tinyfish.stream.event"] = JsonSerializer.SerializeToElement(tinyFishEvent, TinyFishJson)
            };
            if (tinyFishEvent.Type is "PROGRESS" or "STARTED" or "STREAMING_URL")
            {
                yield return CreateStreamEvent("data-tinyfish.agent", tinyFishEvent.RunId ?? eventId, new AIDataEventData
                {
                    Id = tinyFishEvent.RunId ?? eventId,
                    Data = ToPlainObject(JsonSerializer.SerializeToElement(tinyFishEvent, TinyFishJson)),
                    Transient = tinyFishEvent.Type != "COMPLETE"
                }, timestamp, eventMetadata);
            }

            if (!string.Equals(tinyFishEvent.Type, "COMPLETE", StringComparison.OrdinalIgnoreCase))
                continue;

            completed = new TinyFishAgentRun
            {
                RunId = tinyFishEvent.RunId,
                Status = tinyFishEvent.Status ?? "FAILED",
                Result = tinyFishEvent.Result,
                Error = tinyFishEvent.Error,
                Raw = JsonSerializer.SerializeToElement(tinyFishEvent, TinyFishJson)
            };
            break;
        }

        if (completed is null)
            throw new InvalidOperationException("TinyFish agent stream ended before a COMPLETE event was received.");

        var finalMetadata = CreateAgentMetadata(request, metadata, payload, completed);
        var text = completed.Status.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase)
            ? ToDisplayText(completed.Result)
            : completed.Error ?? $"TinyFish agent run ended with status '{completed.Status}'.";
        var timestampFinal = DateTimeOffset.UtcNow;
        if (completed.Status.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase))
        {
            yield return CreateStreamEvent("text-start", eventId, new AITextStartEventData(), timestampFinal, finalMetadata);
            yield return CreateStreamEvent("text-delta", eventId, new AITextDeltaEventData { Delta = text }, timestampFinal, finalMetadata);
            yield return CreateStreamEvent("text-end", eventId, new AITextEndEventData(), timestampFinal, finalMetadata);
            yield return CreateSourceStreamEvent(eventId, new TinyFishFetchResult { Url = metadata.Url!, FinalUrl = metadata.Url!, Title = metadata.Url! }, timestampFinal, finalMetadata, "target_url");
        }
        else
        {
            yield return CreateStreamEvent("error", eventId, new AIErrorEventData { ErrorText = text }, timestampFinal, finalMetadata);
        }

        yield return CreateFinishStreamEvent(eventId, request, completed.Status.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase) ? "stop" : "error", timestampFinal, finalMetadata);
    }

    private async Task<TinyFishFetchExecution> FetchAsync(TinyFishProviderMetadata metadata, CancellationToken cancellationToken)
    {
        var payload = BuildFetchPayload(metadata);
        using var httpRequest = CreateJsonRequest(HttpMethod.Post, FetchEndpoint, payload);
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"TinyFish fetch failed ({(int)response.StatusCode}): {body}");

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement.Clone();
        return new TinyFishFetchExecution
        {
            Payload = payload,
            Raw = root,
            Results = root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array
                ? results.EnumerateArray().Select(ParseFetchResult).ToList()
                : [],
            Errors = root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array
                ? errors.EnumerateArray().Select(ParseFetchError).ToList()
                : []
        };
    }

    private TinyFishTarget ResolveTarget(AIRequest request)
    {
        var model = NormalizeModel(request.Model);
        var metadata = request.Metadata.GetProviderMetadata<TinyFishProviderMetadata>(GetIdentifier())
            ?? throw new InvalidOperationException("TinyFish requires provider metadata.");

        return model switch
        {
            AgentModel when IsHttpUrl(metadata.Url) => new TinyFishTarget(TinyFishTargetKind.Agent, metadata),
            AgentModel => throw new InvalidOperationException("TinyFish agent requires provider metadata 'url' containing one valid absolute HTTP(S) URL."),
            FetchModel when metadata.Urls is { Count: > 0 } && metadata.Urls.All(IsHttpUrl) && metadata.Urls.Count <= 10 => new TinyFishTarget(TinyFishTargetKind.Fetch, metadata),
            FetchModel => throw new InvalidOperationException("TinyFish fetch requires provider metadata 'urls' containing one to ten valid absolute HTTP(S) URLs."),
            _ => throw new InvalidOperationException($"Unsupported TinyFish model '{request.Model}'. Supported models are '{ToUnifiedModel(AgentModel)}' and '{ToUnifiedModel(FetchModel)}'.")
        };
    }

    private static bool IsHttpUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private string ToUnifiedModel(string model) => $"{GetIdentifier()}/{model}";

    private string NormalizeModel(string? model)
    {
        var normalized = model?.Trim() ?? string.Empty;
        var prefix = GetIdentifier() + "/";
        return normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? normalized[prefix.Length..] : normalized;
    }

    private static Dictionary<string, object?> BuildAgentPayload(TinyFishProviderMetadata metadata, string goal, object? responseFormat)
    {
        var payload = ConvertExtraProperties(metadata.AdditionalProperties);
        payload["url"] = metadata.Url;
        payload["goal"] = goal;
        if (responseFormat is not null && !payload.ContainsKey("output_schema"))
            payload["output_schema"] = responseFormat;
        return payload;
    }

    private static Dictionary<string, object?> BuildFetchPayload(TinyFishProviderMetadata metadata)
    {
        var payload = ConvertExtraProperties(metadata.AdditionalProperties);
        payload["urls"] = metadata.Urls;
        payload.TryAdd("format", metadata.Format ?? "markdown");
        payload.TryAdd("image_links", metadata.ImageLinks ?? true);
        return payload;
    }

    private static Dictionary<string, object?> ConvertExtraProperties(Dictionary<string, JsonElement>? properties)
        => properties?.ToDictionary(item => item.Key, item => ToPlainObject(item.Value), StringComparer.OrdinalIgnoreCase) ?? [];

    private static string BuildPrompt(AIRequest request)
    {
        var sections = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            sections.Add(request.Instructions.Trim());
        if (!string.IsNullOrWhiteSpace(request.Input?.Text))
            sections.Add(request.Input.Text.Trim());
        if (request.Input?.Items is not null)
        {
            sections.AddRange(request.Input.Items
                .Where(item => string.IsNullOrWhiteSpace(item.Type) || item.Type.Equals("message", StringComparison.OrdinalIgnoreCase))
                .SelectMany(item => item.Content ?? [])
                .OfType<AITextContentPart>()
                .Select(part => part.Text?.Trim())
                .Where(text => !string.IsNullOrWhiteSpace(text))!);
        }
        return string.Join("\n\n", sections);
    }

    private HttpRequestMessage CreateJsonRequest(HttpMethod method, string endpoint, object payload)
        => new(method, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, TinyFishJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

    private static TinyFishAgentRun ParseAgentRun(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement.Clone();
        return new TinyFishAgentRun
        {
            RunId = GetString(root, "run_id"),
            Status = GetString(root, "status") ?? "FAILED",
            Result = CloneProperty(root, "result"),
            Error = GetErrorText(root),
            NumOfSteps = GetInt(root, "num_of_steps"),
            Raw = root
        };
    }

    private static TinyFishFetchResult ParseFetchResult(JsonElement raw)
        => new()
        {
            Url = GetString(raw, "url") ?? string.Empty,
            FinalUrl = GetString(raw, "final_url"),
            Title = GetString(raw, "title"),
            Description = GetString(raw, "description"),
            Text = CloneProperty(raw, "text"),
            ImageLinks = GetStringArray(raw, "image_links"),
            Raw = raw.Clone()
        };

    private static TinyFishFetchError ParseFetchError(JsonElement raw)
        => new() { Url = GetString(raw, "url") ?? string.Empty, Error = GetString(raw, "error"), Status = GetInt(raw, "status"), Raw = raw.Clone() };

    private async Task<TinyFishDownloadedImage?> TryDownloadImageAsync(string imageUrl, TinyFishFetchResult result, CancellationToken cancellationToken)
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
                mediaType = GuessImageMediaType(imageUrl);
            return new TinyFishDownloadedImage(imageUrl, mediaType!, GuessFilename(imageUrl, mediaType!), Convert.ToBase64String(bytes), result);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static string GuessImageMediaType(string url) => Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", ".svg" => "image/svg+xml", _ => "image/png"
    };

    private static string GuessFilename(string url, string mediaType)
    {
        var filename = Path.GetFileName(new Uri(url).AbsolutePath);
        return string.IsNullOrWhiteSpace(filename) ? $"tinyfish-image.{mediaType.Split('/').Last().Replace("jpeg", "jpg")}" : filename;
    }

    private AIOutputItem CreateMessageItem(string text, Dictionary<string, object?>? metadata = null)
        => new() { Type = "message", Role = "assistant", Content = [new AITextContentPart { Type = "text", Text = text }], Metadata = metadata };

    private AIOutputItem CreateSourceOutputItem(string url, string title, string type, TinyFishFetchResult? result = null)
        => new()
        {
            Type = "source-url",
            Content = [new AITextContentPart { Type = "text", Text = url }],
            Metadata = CreateSourceMetadata(url, title, type, result)
        };

    private AIOutputItem CreateImageOutputItem(TinyFishDownloadedImage image)
        => new()
        {
            Type = "message",
            Role = "assistant",
            Content = [new AIFileContentPart { Type = "file", 
                MediaType = image.MediaType, Filename = image.Filename, Data = image.Base64 }],
            Metadata = CreateImageMetadata(image)
        };

    private AIStreamEvent CreateSourceStreamEvent(string id, TinyFishFetchResult result, DateTimeOffset timestamp, Dictionary<string, object?> metadata, string type = "fetch_result")
        => CreateStreamEvent("source-url", $"{id}_source", new AISourceUrlEventData
        {
            SourceId = result.FinalUrl ?? result.Url,
            Url = result.FinalUrl ?? result.Url,
            Title = result.Title ?? result.FinalUrl ?? result.Url,
            Type = "url_citation",
            ProviderMetadata = CreateScopedMetadata(CreateSourceMetadata(result.FinalUrl ?? result.Url, result.Title ?? result.FinalUrl ?? result.Url, type, result))
        }, timestamp, metadata);

    private AIStreamEvent CreateFileStreamEvent(string id, TinyFishDownloadedImage image, DateTimeOffset timestamp, Dictionary<string, object?> metadata)
        => CreateStreamEvent("file", $"{id}_image_{Guid.NewGuid():N}", new AIFileEventData
        {
            MediaType = image.MediaType,
            Filename = image.Filename,
            Url = image.Base64,
            ProviderMetadata = CreateScopedMetadata(CreateImageMetadata(image))
        }, timestamp, metadata);

    private AIStreamEvent CreateFinishStreamEvent(string id, AIRequest request, string finishReason, DateTimeOffset timestamp, Dictionary<string, object?> metadata)
        => CreateStreamEvent("finish", id, new AIFinishEventData
        {
            FinishReason = finishReason,
            Model = request.Model ?? ToUnifiedModel(FetchModel),
            CompletedAt = timestamp.ToUnixTimeSeconds(),
            MessageMetadata = AIFinishMessageMetadata.Create(request.Model ?? ToUnifiedModel(FetchModel), timestamp, usage: new Dictionary<string, object?>())
        }, timestamp, metadata);

    private AIStreamEvent CreateStreamEvent(string type, string id, object data, DateTimeOffset timestamp, Dictionary<string, object?> metadata)
        => new()
        {
            ProviderId = GetIdentifier(),
            Event = new AIEventEnvelope { Type = type, Id = id, Timestamp = timestamp, Data = data },
            Metadata = metadata
        };

    private Dictionary<string, object?> CreateAgentMetadata(AIRequest request, TinyFishProviderMetadata metadata, Dictionary<string, object?> payload, TinyFishAgentRun run)
        => new()
        {
            ["tinyfish.target.url"] = metadata.Url,
            ["tinyfish.request.payload"] = payload,
            ["tinyfish.run.id"] = run.RunId,
            ["tinyfish.run.status"] = run.Status,
            ["tinyfish.run.steps"] = run.NumOfSteps,
            ["tinyfish.run.result"] = run.Result,
            ["tinyfish.run.error"] = run.Error,
            ["tinyfish.response.raw"] = run.Raw
        };

    private static Dictionary<string, object?> CreateFetchMetadata(AIRequest request, TinyFishProviderMetadata metadata, TinyFishFetchExecution execution)
        => new()
        {
            ["tinyfish.request.payload"] = execution.Payload,
            ["tinyfish.request.urls"] = metadata.Urls,
            ["tinyfish.fetch.results"] = execution.Results.Select(result => result.Raw).ToList(),
            ["tinyfish.fetch.errors"] = execution.Errors.Select(error => error.Raw).ToList(),
            ["tinyfish.response.raw"] = execution.Raw
        };

    private static Dictionary<string, object?> CreateResultMetadata(Dictionary<string, object?> metadata, TinyFishFetchResult result)
    {
        var resultMetadata = new Dictionary<string, object?>(metadata) { ["tinyfish.fetch.result"] = result.Raw };
        return resultMetadata;
    }

    private static Dictionary<string, object?> CreateFetchResultPartMetadata(TinyFishFetchResult result)
        => new() { ["tinyfish.fetch.result"] = result.Raw, ["tinyfish.source.url"] = result.FinalUrl ?? result.Url, ["tinyfish.source.title"] = result.Title };

    private Dictionary<string, object?> CreateSourceMetadata(string url, string title, string type, TinyFishFetchResult? result)
        => new()
        {
            ["chatcompletions.source.url"] = url,
            ["chatcompletions.source.title"] = title,
            ["messages.source.url"] = url,
            ["messages.source.title"] = title,
            ["tinyfish.source.type"] = type,
            ["tinyfish.fetch.result"] = result?.Raw
        };

    private Dictionary<string, object?> CreateImageMetadata(TinyFishDownloadedImage image)
        => new()
        {
            ["tinyfish.image.url"] = image.Url,
            ["tinyfish.source.url"] = image.Result.FinalUrl ?? image.Result.Url,
            ["tinyfish.source.title"] = image.Result.Title,
            ["tinyfish.fetch.result"] = image.Result.Raw
        };

    private Dictionary<string, Dictionary<string, object>> CreateScopedMetadata(Dictionary<string, object?> values)
        => new()
        {
            [GetIdentifier()] = values.Where(item => item.Value is not null).ToDictionary(item => item.Key, item => item.Value!)
        };

    private static string ToFetchResultText(TinyFishFetchResult result)
    {
        var title = result.Title ?? result.FinalUrl ?? result.Url;
        var url = result.FinalUrl ?? result.Url;
        var body = result.Text.ValueKind switch
        {
            JsonValueKind.String => result.Text.GetString() ?? string.Empty,
            JsonValueKind.Undefined or JsonValueKind.Null => result.Description ?? string.Empty,
            _ => result.Text.GetRawText()
        };
        return string.IsNullOrWhiteSpace(body) ? $"[{title}]({url})" : $"[{title}]({url})\n\n{body}";
    }

    private static string ToDisplayText(JsonElement result)
        => result.ValueKind switch
        {
            JsonValueKind.String => result.GetString() ?? string.Empty,
            JsonValueKind.Undefined or JsonValueKind.Null => string.Empty,
            _ => JsonSerializer.Serialize(result, new JsonSerializerOptions(TinyFishJson) { WriteIndented = true })
        };

    private static string CreateResultId(string eventId, string url) => $"{eventId}_{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..12].ToLowerInvariant()}";
    private static string? GetString(JsonElement element, string name) => element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static int? GetInt(JsonElement element, string name) => element.TryGetProperty(name, out var property) && property.TryGetInt32(out var value) ? value : null;
    private static JsonElement CloneProperty(JsonElement element, string name) => element.TryGetProperty(name, out var property) ? property.Clone() : default;
    private static List<string> GetStringArray(JsonElement element, string name) => element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Array ? property.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String && IsHttpUrl(item.GetString())).Select(item => item.GetString()!).Distinct(StringComparer.OrdinalIgnoreCase).ToList() : [];
    private static string? GetErrorText(JsonElement element) => element.TryGetProperty("error", out var error) ? error.ValueKind == JsonValueKind.String ? error.GetString() : error.ValueKind == JsonValueKind.Object && GetString(error, "message") is { } message ? message : error.GetRawText() : null;
    private static object? ToPlainObject(JsonElement element) => element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? null : JsonSerializer.Deserialize<object>(element.GetRawText(), TinyFishJson);

    private enum TinyFishTargetKind { Agent, Fetch }
    private sealed record TinyFishTarget(TinyFishTargetKind Kind, TinyFishProviderMetadata Metadata);
    private sealed class TinyFishProviderMetadata
    {
        [JsonPropertyName("url")] public string? Url { get; init; }
        [JsonPropertyName("urls")] public List<string>? Urls { get; init; }
        [JsonPropertyName("format")] public string? Format { get; init; }
        [JsonPropertyName("image_links")] public bool? ImageLinks { get; init; }
        [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
    }
    private sealed class TinyFishAgentRun { public string? RunId { get; init; } public string Status { get; init; } = "FAILED"; public JsonElement Result { get; init; } public string? Error { get; init; } public int? NumOfSteps { get; init; } public JsonElement Raw { get; init; } }
    private sealed class TinyFishSseEvent { [JsonPropertyName("type")] public string? Type { get; init; } [JsonPropertyName("run_id")] public string? RunId { get; init; } [JsonPropertyName("status")] public string? Status { get; init; } [JsonPropertyName("result")] public JsonElement Result { get; init; } [JsonPropertyName("error")] public string? Error { get; init; } [JsonPropertyName("timestamp")] public DateTimeOffset? Timestamp { get; init; } }
    private sealed class TinyFishFetchExecution { public required Dictionary<string, object?> Payload { get; init; } public required JsonElement Raw { get; init; } public List<TinyFishFetchResult> Results { get; init; } = []; public List<TinyFishFetchError> Errors { get; init; } = []; }
    private sealed class TinyFishFetchResult { public string Url { get; init; } = string.Empty; public string? FinalUrl { get; init; } public string? Title { get; init; } public string? Description { get; init; } public JsonElement Text { get; init; } public List<string> ImageLinks { get; init; } = []; public JsonElement Raw { get; init; } }
    private sealed class TinyFishFetchError { public string Url { get; init; } = string.Empty; public string? Error { get; init; } public int? Status { get; init; } public JsonElement Raw { get; init; } }
    private sealed record TinyFishDownloadedImage(string Url, string MediaType, string Filename, string Base64, TinyFishFetchResult Result);
}
