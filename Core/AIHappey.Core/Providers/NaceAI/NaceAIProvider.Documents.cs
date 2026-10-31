using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.NaceAI;

/// <summary>
/// Perception (Document Intelligence) special models: POST /v1/documents/{parse|classify|split|extract|ground},
/// 202 polling through GET /v1/documents/jobs/{id}, and job cancellation through DELETE.
/// </summary>
public sealed partial class NaceAIProvider
{
    private const string DocumentToolName = "naceai_documents";
    private const int MaxInlineUploadBytes = 64 * 1024 * 1024;
    private static readonly TimeSpan DocumentTimeout = TimeSpan.FromMinutes(15);

    private sealed record DocumentModel(string Operation, string? ParseMode);

    private static readonly Dictionary<string, DocumentModel> DocumentModels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["parse"] = new("parse", "low"),
        ["parse-medium"] = new("parse", "medium"),
        ["parse-high"] = new("parse", "high"),
        ["classify"] = new("classify", null),
        ["split"] = new("split", null),
        ["extract"] = new("extract", null),
        ["ground"] = new("ground", null)
    };

    private static readonly string[] CommonDocumentOptions = ["source", "name", "project_id", "password", "page_ranges", "corpus_sources"];

    private static readonly Dictionary<string, string[]> DocumentOptionsByOperation = new(StringComparer.Ordinal)
    {
        ["parse"] = [.. CommonDocumentOptions, "parse_mode", "quality", "ocr", "figures", "diagrams", "chunking", "spreadsheet", "output"],
        ["classify"] = [.. CommonDocumentOptions, "classes", "granularity", "unknown_policy"],
        ["split"] = [.. CommonDocumentOptions, "classes", "unknown_policy", "output"],
        ["extract"] = [.. CommonDocumentOptions, "schema", "schema_id", "citations"],
        ["ground"] = [.. CommonDocumentOptions, "targets", "options"]
    };

    private static bool IsDocumentModel(string? model) => DocumentModels.ContainsKey(NormalizeModelId(model));

    private async Task<AIResponse> ExecuteDocumentUnifiedAsync(AIRequest request, CancellationToken cancellationToken)
    {
        var model = NormalizeModelId(request.Model);
        var definition = DocumentModels[model];
        var userMessage = request.Input?.Items?.LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));
        var files = userMessage?.Content?.OfType<AIFileContentPart>().ToList() ?? [];
        var userText = string.Join("\n", userMessage?.Content?.OfType<AITextContentPart>().Select(part => part.Text) ?? []).Trim();

        var providerOptions = request.Metadata.GetProviderMetadata<JsonElement>(GetIdentifier());
        var options = ReadDocumentOptions(definition, providerOptions);
        ApplyResponseFormatSchema(definition, request.ResponseFormat, options);

        var sources = new List<(string Name, JsonObject Source)>();
        if (options.Remove("source", out var explicitSource) && explicitSource is JsonObject explicitObject)
            sources.Add(("source", explicitObject));
        else
            for (var index = 0; index < files.Count; index++)
                sources.Add(await ResolveDocumentSourceAsync(files[index], index, cancellationToken));

        if (sources.Count == 0)
            throw new ArgumentException("NaceAI document models require a file or URL in the latest user message, or providerOptions.naceai.source.", nameof(request));
        if (definition.Operation == "ground" && options["targets"] is null)
        {
            if (string.IsNullOrWhiteSpace(userText))
                throw new ArgumentException("NaceAI ground requires providerOptions.naceai.targets or a claim in the user text.", nameof(request));
            options["targets"] = new JsonArray(new JsonObject { ["id"] = "claim-1", ["text"] = userText });
        }

        var output = new List<AIOutputItem>();
        var texts = new List<string>();
        var credits = 0d;
        decimal costUsd = 0;
        var warnings = new List<string>();
        var jobs = new List<JsonObject>();

        foreach (var (name, source) in sources)
        {
            var body = new JsonObject { ["source"] = source };
            foreach (var (key, value) in options)
                body[key] = value?.DeepClone();
            if (definition.Operation == "parse")
            {
                body["parse_mode"] ??= definition.ParseMode;
                // Markdown is the gateway's text rendering of a parsed document.
                var outputOptions = body["output"] as JsonObject ?? new JsonObject();
                if (outputOptions["formats"] is not JsonArray formats || !formats.Any(item => item?.GetValue<string>() == "markdown"))
                    outputOptions["formats"] = new JsonArray("markdown");
                body["output"] = outputOptions;
            }

            var job = await RunDocumentJobAsync(definition.Operation, body, name, cancellationToken);
            jobs.Add(job);
            credits += job["credits"] is JsonValue c && c.TryGetValue<double>(out var cv) ? cv : 0;
            if (job["cost"]?["total_usd"] is JsonValue total
                && decimal.TryParse(total.ToString(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var usd))
                costUsd += usd;

            var result = job["result"] as JsonObject ?? job;
            warnings.AddRange(ReadDisclosures(result));
            var text = definition.Operation == "parse" ? ExtractMarkdown(result) : ExtractStructuredText(definition.Operation, result);
            texts.Add(text);

            output.Add(new AIOutputItem
            {
                Type = "tool-call",
                Role = "assistant",
                Content = [new AIToolCallContentPart
                {
                    Type = "tool-call", ToolCallId = Guid.NewGuid().ToString("n"), ToolName = DocumentToolName,
                    Title = $"NaceAI {definition.Operation}",
                    Input = new { model, operation = definition.Operation, document = name },
                    State = "output-available", ProviderExecuted = true,
                    Output = new CallToolResult { IsError = false, Content = [], StructuredContent = JsonSerializer.SerializeToElement(job) }
                }]
            });
            output.Add(new AIOutputItem
            {
                Type = "message",
                Role = "assistant",
                Content = [new AITextContentPart { Type = "text", Text = text }]
            });
        }

        var metadata = new Dictionary<string, object?>
        {
            ["finishReason"] = "stop",
            ["naceai.operation"] = definition.Operation,
            ["naceai.job_ids"] = jobs.Select(job => job["job_id"]?.GetValue<string>()).ToList(),
            ["naceai.warnings"] = warnings.Count > 0 ? warnings : null
        };

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = $"{GetIdentifier()}/{model}",
            Status = "completed",
            Output = new AIOutput { Items = output },
            Usage = new Dictionary<string, object?> { ["credits"] = credits, ["cost_usd"] = costUsd.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            Metadata = metadata.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value)
        };
    }

    private static JsonObject ReadDocumentOptions(DocumentModel definition, JsonElement providerOptions)
    {
        var body = new JsonObject();
        var allowed = DocumentOptionsByOperation[definition.Operation];
        foreach (var property in providerOptions.ValueKind == JsonValueKind.Object ? providerOptions.EnumerateObject() : default)
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
                throw new ArgumentException($"NaceAI {definition.Operation} does not support provider option '{property.Name}'.");
            body[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        }

        if (body["parse_mode"] is JsonNode mode && mode.GetValue<string>() is not ("low" or "medium" or "high"))
            throw new ArgumentException("NaceAI parse_mode must be low, medium or high.");
        if (body["quality"] is JsonNode quality && quality.GetValue<string>() is not ("auto" or "fast" or "accurate"))
            throw new ArgumentException("NaceAI quality must be auto, fast or accurate.");
        if (body["schema"] is not null && body["schema_id"] is not null)
            throw new ArgumentException("NaceAI extract accepts either schema or schema_id, not both.");
        if (definition.Operation is "classify" or "split" && body["classes"] is not JsonArray { Count: > 0 })
            throw new ArgumentException($"NaceAI {definition.Operation} requires providerOptions.naceai.classes.");
        return body;
    }

    // Structured outputs are the gateway's contract for schemas, so extract accepts response_format json_schema too.
    private static void ApplyResponseFormatSchema(DocumentModel definition, object? responseFormat, JsonObject options)
    {
        if (responseFormat is null)
            return;
        var format = JsonSerializer.SerializeToNode(responseFormat, JsonSerializerOptions.Web) as JsonObject;
        var type = format?["type"]?.GetValue<string>();
        if (type is null or "text")
            return;
        if (definition.Operation != "extract" || type != "json_schema")
            throw new ArgumentException($"NaceAI {definition.Operation} does not support structured response formats.");

        if (options["schema"] is not null || options["schema_id"] is not null)
            return;
        var schema = format!["json_schema"]?["schema"] ?? format["schema"];
        options["schema"] = schema?.DeepClone() ?? throw new ArgumentException("response_format json_schema requires a schema.");
    }

    private async Task<(string Name, JsonObject Source)> ResolveDocumentSourceAsync(
        AIFileContentPart file, int index, CancellationToken cancellationToken)
    {
        var value = file.Data switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
            _ => null
        };
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"NaceAI document {index + 1} must contain base64 data or an HTTPS URL.", nameof(file));

        var filename = string.IsNullOrWhiteSpace(file.Filename) ? $"document-{index + 1}" : file.Filename!;
        value = value.Trim();

        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
                throw new ArgumentException($"NaceAI document {index + 1} must use an https URL.", nameof(file));
            if (string.IsNullOrWhiteSpace(file.Filename))
                filename = Path.GetFileName(url.AbsolutePath) is { Length: > 0 } last ? last : filename;
            return (filename, new JsonObject { ["type"] = "url", ["url"] = value, ["file_name"] = filename });
        }

        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = value.IndexOf(',');
            if (comma < 0 || !value[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"NaceAI document {index + 1} must use a base64 data URL.", nameof(file));
            value = value[(comma + 1)..];
        }

        byte[] bytes;
        try { bytes = Convert.FromBase64String(value); }
        catch (FormatException exception) { throw new ArgumentException($"NaceAI document {index + 1} contains invalid base64.", nameof(file), exception); }
        if (bytes.Length is 0 or > MaxInlineUploadBytes)
            throw new ArgumentException($"NaceAI inline documents must be between 1 byte and {MaxInlineUploadBytes / (1024 * 1024)} MB; use a URL for larger files.", nameof(file));

        return (filename, await UploadDocumentAsync(bytes, filename, file.MediaType, cancellationToken));
    }

    // Three steps: request an upload grant, send the bytes with the one-time upload token, reference the workspace file.
    private async Task<JsonObject> UploadDocumentAsync(byte[] bytes, string filename, string? mediaType, CancellationToken cancellationToken)
    {
        var (grantBody, _) = await SendNaceJsonAsync(HttpMethod.Post, "v1/documents/upload-grants",
            new JsonObject { ["path"] = filename, ["total_size_bytes"] = bytes.Length, ["ttl_seconds"] = 600 },
            "upload grant", null, cancellationToken);
        var grant = grantBody["result"] as JsonObject ?? grantBody;
        var uploadUrl = grant["upload_url"]?.GetValue<string>()
            ?? throw new InvalidOperationException("NaceAI upload grant did not return an upload_url.");
        var token = grant["token"]?.GetValue<string>()
            ?? throw new InvalidOperationException("NaceAI upload grant did not return a token.");

        var target = Uri.TryCreate(uploadUrl, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https" ? absolute : new Uri(_client.BaseAddress!, uploadUrl);
        // The upload token is only ever sent to NaceAI's own host.
        if (target.Scheme != Uri.UriSchemeHttps || !string.Equals(target.Host, _client.BaseAddress!.Host, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("NaceAI returned an upload location outside NaceAI's API host.");

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(mediaType) ? "application/octet-stream" : mediaType);
        form.Add(file, "file", filename);
        var metadata = new StringContent(
            new JsonObject { ["path"] = filename, ["total_size_bytes"] = bytes.Length }.ToJsonString(),
            Encoding.UTF8, "application/json");
        form.Add(metadata, "metadata");

        // A dedicated client keeps the bearer key out of the upload request.
        using var uploadClient = _httpClientFactory.CreateClient();
        using var upload = new HttpRequestMessage(HttpMethod.Post, target) { Content = form };
        upload.Headers.TryAddWithoutValidation("X-Upload-Token", token);
        using var response = await uploadClient.SendAsync(upload, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw CreateNaceException("document upload", response, raw);

        var uploaded = JsonNode.Parse(raw) as JsonObject ?? throw new InvalidOperationException("NaceAI upload returned an invalid response.");
        var fileNode = (uploaded["result"] as JsonObject ?? uploaded)["file"] as JsonObject ?? uploaded["result"] as JsonObject ?? uploaded;
        var workspaceId = fileNode["workspace_id"]?.GetValue<string>() ?? grant["workspace_id"]?.GetValue<string>();
        var fileId = fileNode["file_id"]?.GetValue<string>() ?? fileNode["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(fileId))
            throw new InvalidOperationException("NaceAI upload did not return a workspace file reference.");

        return new JsonObject { ["type"] = "workspace_file", ["workspace_id"] = workspaceId, ["file_id"] = fileId };
    }

    private async Task<JsonObject> RunDocumentJobAsync(string operation, JsonObject body, string document, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string> { ["Idempotency-Key"] = Guid.NewGuid().ToString("n") };
        var (job, status) = await SendNaceJsonAsync(HttpMethod.Post, $"v1/documents/{operation}?wait_seconds=60", body,
            $"{operation} '{document}'", headers, cancellationToken);
        var jobId = job["job_id"]?.GetValue<string>();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DocumentTimeout);
        var delay = TimeSpan.FromSeconds(1);
        try
        {
            while (!IsTerminalJobStatus(job["status"]?.GetValue<string>()))
            {
                if (string.IsNullOrWhiteSpace(jobId))
                    throw new InvalidOperationException("NaceAI accepted the document job without a job_id.");

                await Task.Delay(delay, timeout.Token);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 1.5, 5000));
                (job, _) = await SendNaceJsonAsync(HttpMethod.Get, $"v1/documents/jobs/{Uri.EscapeDataString(jobId)}", null,
                    $"{operation} job status", null, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (jobId is not null)
        {
            await CancelDocumentJobAsync(jobId);
            throw;
        }

        _ = status;
        return job["status"]?.GetValue<string>() == "succeeded"
            ? job
            : throw new InvalidOperationException($"NaceAI {operation} for '{document}' ended with status '{job["status"]}': {job.ToJsonString()}");
    }

    private static bool IsTerminalJobStatus(string? status)
        => status is "succeeded" or "failed" or "cancelled" or "canceled" or "expired";

    // Best effort: a job that is cancelled before completion is not charged.
    private async Task CancelDocumentJobAsync(string jobId)
    {
        try
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"v1/documents/jobs/{Uri.EscapeDataString(jobId)}");
            using var response = await _client.SendAsync(request, cleanup.Token);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
        }
    }

    private static IEnumerable<string> ReadDisclosures(JsonObject result)
        => (result["disclosures"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(item => item["message"]?.GetValue<string>() ?? item["code"]?.GetValue<string>())
            .OfType<string>();

    private static string ExtractMarkdown(JsonObject result)
    {
        if (result["document"] is JsonObject document)
        {
            if (ReadString(document, "markdown") is { } markdown)
                return markdown;
            if (ReadString(document, "text") is { } text)
                return text;
            if (document["pages"] is JsonArray documentPages)
                return JoinPages(documentPages);
        }

        if (ReadString(result, "markdown") is { } direct)
            return direct;
        if (result["pages"] is JsonArray pages)
            return JoinPages(pages);
        return string.Empty;
    }

    private static string JoinPages(JsonArray pages)
        => string.Join("\n\n", pages.OfType<JsonObject>()
            .Select(page => ReadString(page, "markdown") ?? ReadString(page, "text"))
            .Where(text => !string.IsNullOrEmpty(text)));

    private static string? ReadString(JsonObject value, string name)
        => value[name] is JsonValue node && node.TryGetValue<string>(out var text) ? text : null;

    // classify, split, extract and ground return JSON; extract returns just its data object.
    private static string ExtractStructuredText(string operation, JsonObject result)
    {
        JsonNode? selected = operation switch
        {
            "extract" => result["data"] ?? result,
            "classify" => result["units"] ?? result,
            "split" => result["segments"] ?? result,
            "ground" => result["targets"] ?? result,
            _ => result
        };
        return selected.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private async Task<(JsonObject Body, System.Net.HttpStatusCode Status)> SendNaceJsonAsync(
        HttpMethod method, string path, JsonObject? body, string operation,
        Dictionary<string, string>? headers, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var message = new HttpRequestMessage(method, path);
        message.Headers.Accept.ParseAdd("application/json");
        foreach (var (name, value) in headers ?? [])
            message.Headers.TryAddWithoutValidation(name, value);
        if (body is not null)
            message.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw CreateNaceException(operation, response, raw);

        try
        {
            return (JsonNode.Parse(raw) as JsonObject ?? throw new JsonException("Expected a JSON object."), response.StatusCode);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"NaceAI {operation} returned an invalid JSON object.", exception);
        }
    }

    private static HttpRequestException CreateNaceException(string operation, HttpResponseMessage response, string raw)
    {
        var requestId = response.Headers.TryGetValues("x-request-id", out var ids) ? ids.FirstOrDefault() : null;
        var retryAfter = response.Headers.TryGetValues("retry-after-ms", out var ms) ? ms.FirstOrDefault() + "ms"
            : response.Headers.RetryAfter?.ToString();
        return new HttpRequestException(
            $"NaceAI {operation} failed ({(int)response.StatusCode} {response.ReasonPhrase})"
            + (requestId is null ? "" : $" [request {requestId}]")
            + (retryAfter is null ? "" : $" [Retry-After: {retryAfter}]")
            + $": {raw}",
            null,
            response.StatusCode);
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamDocumentUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = await ExecuteDocumentUnifiedAsync(request, cancellationToken);
        foreach (var streamEvent in ToStreamEvents(response, request.Model, DocumentToolName))
            yield return streamEvent;
    }

    // Special models finish before they stream, so the finished response is replayed as protocol events.
    private IEnumerable<AIStreamEvent> ToStreamEvents(AIResponse response, string? requestModel, string toolName)
    {
        foreach (var item in response.Output?.Items ?? [])
        {
            var now = DateTimeOffset.UtcNow;
            if (item.Content?.OfType<AIToolCallContentPart>().FirstOrDefault() is { } tool)
            {
                yield return StreamEvent(tool.ToolCallId, "tool-input-available", new AIToolInputAvailableEventData
                {
                    ToolName = toolName,
                    Title = tool.Title,
                    Input = tool.Input ?? throw new InvalidOperationException("NaceAI document response omitted its tool input."),
                    ProviderExecuted = true
                }, now);
                yield return StreamEvent(tool.ToolCallId, "tool-output-available", new AIToolOutputAvailableEventData
                {
                    ToolName = toolName,
                    Output = tool.Output ?? throw new InvalidOperationException("NaceAI document response omitted its tool output."),
                    ProviderExecuted = true
                }, now);
                continue;
            }

            var id = Guid.NewGuid().ToString("n");
            foreach (var text in (item.Content ?? []).OfType<AITextContentPart>())
            {
                yield return StreamEvent(id, "text-start", new AITextStartEventData(), now);
                if (!string.IsNullOrEmpty(text.Text))
                    yield return StreamEvent(id, "text-delta", new AITextDeltaEventData { Delta = text.Text }, now);
                yield return StreamEvent(id, "text-end", new AITextEndEventData(), now);
            }

            foreach (var file in (item.Content ?? []).OfType<AIFileContentPart>())
                yield return StreamEvent(id, "file", new { mediaType = file.MediaType, filename = file.Filename, url = file.Data }, now);
        }

        yield return CreateFinishEvent(response, requestModel);
    }

    private AIStreamEvent CreateFinishEvent(AIResponse response, string? requestModel)
    {
        var completedAt = DateTimeOffset.UtcNow;
        var failed = response.Status == "failed";
        return StreamEvent(Guid.NewGuid().ToString("n"), "finish", new AIFinishEventData
        {
            FinishReason = failed ? "error" : "stop",
            Model = response.Model,
            CompletedAt = completedAt.ToUnixTimeSeconds(),
            MessageMetadata = AIFinishMessageMetadata.Create(response.Model ?? requestModel ?? "naceai", completedAt, response.Usage)
        }, completedAt, response.Metadata);
    }

    private AIStreamEvent StreamEvent(string? id, string type, object data, DateTimeOffset timestamp,
        Dictionary<string, object?>? metadata = null)
        => new()
        {
            ProviderId = GetIdentifier(),
            Event = new AIEventEnvelope { Type = type, Id = id, Timestamp = timestamp, Data = data },
            Metadata = metadata
        };
}
