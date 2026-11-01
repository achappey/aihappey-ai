using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Jenova;

public sealed partial class JenovaProvider
{
    private static readonly JsonElement EmptyObject = JsonSerializer.SerializeToElement(new Dictionary<string, object?>());

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var call = CreateCall(request, stream: false);
        using var response = await SendAsync(HttpMethod.Post, call.Path, call.Body, call.ApiKey, cancellationToken, call.IdempotencyKey);
        var rawText = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(rawText);
        var raw = document.RootElement.Clone();

        if ((int)response.StatusCode == 202)
            return CreateAcceptedResponse(request, raw);

        return CreateMessageResponse(request, raw);
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var call = CreateCall(request, stream: true);
        var run = new JenovaRun(call.SessionId);
        try
        {
            await foreach (var item in StreamCoreAsync(request, call, run, cancellationToken)
                               .WithCancellation(cancellationToken))
                yield return item;
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested && !run.Ended
                && run.SessionId is not null && run.RunId is not null)
                await CancelRunAsync(call, run.SessionId, run.RunId);
        }
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamCoreAsync(
        AIRequest request,
        JenovaCall call,
        JenovaRun run,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var message = CreateHttpRequest(HttpMethod.Post, call.Path, call.Body, call.ApiKey, call.IdempotencyKey);
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw await CreateHttpErrorAsync(response, cancellationToken);

        var sessionId = call.SessionId;
        var runId = response.Headers.TryGetValues("X-Run-Id", out var runIds) ? runIds.FirstOrDefault() : null;
        run.SessionId = sessionId;
        run.RunId = runId;
        var textStarted = false;
        var reasoningStarted = false;
        var ended = false;
        var textId = request.Id ?? $"jenova-{Guid.NewGuid():N}";
        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            var accepted = await ReadJsonAsync(response, cancellationToken);
            sessionId = String(accepted, "session_id") ?? sessionId;
            runId = String(accepted, "run_id") ?? runId;
            run.SessionId = sessionId;
            run.RunId = runId;
            run.Ended = true;
            yield return NativeEvent("data-jenova-accepted", runId ?? textId, accepted, sessionId, runId);
            yield return new AIStreamEvent
            {
                ProviderId = GetIdentifier(),
                Metadata = Metadata(accepted, sessionId, runId),
                Event = new AIEventEnvelope
                {
                    Type = "finish",
                    Id = runId ?? textId,
                    Timestamp = DateTimeOffset.UtcNow,
                    Data = new AIFinishEventData
                    {
                        FinishReason = "other",
                        Model = call.Model ?? request.Model,
                        Response = accepted,
                        MessageMetadata = AIFinishMessageMetadata.Create(
                            call.Model ?? request.Model ?? call.Agent,
                            DateTimeOffset.UtcNow,
                            additionalProperties: new Dictionary<string, object?> { ["jenova"] = accepted })
                    }
                }
            };
            yield break;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        await foreach (var frame in ReadSseFramesAsync(reader, cancellationToken))
        {
            if (frame.Event == "ping")
            {
                yield return NativeEvent(
                    "data-jenova-ping",
                    textId,
                    JsonSerializer.SerializeToElement(frame.Data, JsonOptions),
                    sessionId,
                    runId,
                    transient: true);
                continue;
            }

            using var document = JsonDocument.Parse(frame.Data);
            var raw = document.RootElement.Clone();
            sessionId = String(raw, "session_id") ?? sessionId;
            runId = String(raw, "run_id") ?? runId;
            run.SessionId = sessionId;
            run.RunId = runId;
            var eventId = runId ?? textId;

            switch (frame.Event)
            {
                case "stream_delta":
                    var delta = String(raw, "chunk_content") ?? string.Empty;
                    if (reasoningStarted)
                    {
                        reasoningStarted = false;
                        yield return NativeEvent("reasoning-end", eventId, raw, sessionId, runId);
                    }
                    if (!textStarted)
                    {
                        textStarted = true;
                        yield return NativeEvent("text-start", eventId, raw, sessionId, runId);
                    }
                    if (delta.Length > 0)
                        yield return new AIStreamEvent
                        {
                            ProviderId = GetIdentifier(),
                            Metadata = Metadata(raw, sessionId, runId),
                            Event = new AIEventEnvelope
                            {
                                Type = "text-delta",
                                Id = eventId,
                                Timestamp = DateTimeOffset.UtcNow,
                                Data = new AITextDeltaEventData { Delta = delta, ProviderMetadata = Scoped(raw, sessionId, runId) },
                                Metadata = Metadata(raw, sessionId, runId)
                            }
                        };
                    break;
                case "stream_thinking":
                    var thought = String(raw, "content") ?? string.Empty;
                    if (textStarted)
                    {
                        textStarted = false;
                        yield return NativeEvent("text-end", eventId, raw, sessionId, runId);
                    }
                    if (!reasoningStarted)
                    {
                        reasoningStarted = true;
                        yield return NativeEvent("reasoning-start", eventId, raw, sessionId, runId);
                    }
                    if (thought.Length > 0)
                        yield return new AIStreamEvent
                        {
                            ProviderId = GetIdentifier(),
                            Metadata = Metadata(raw, sessionId, runId),
                            Event = new AIEventEnvelope
                            {
                                Type = "reasoning-delta",
                                Id = eventId,
                                Timestamp = DateTimeOffset.UtcNow,
                                Data = new AIReasoningDeltaEventData
                                {
                                    Delta = thought,
                                    ProviderMetadata = ScopedNested(raw, sessionId, runId)
                                },
                                Metadata = Metadata(raw, sessionId, runId)
                            }
                        };
                    break;
                case "stream_error":
                    yield return new AIStreamEvent
                    {
                        ProviderId = GetIdentifier(),
                        Metadata = Metadata(raw, sessionId, runId),
                        Event = new AIEventEnvelope
                        {
                            Type = "error",
                            Id = eventId,
                            Timestamp = DateTimeOffset.UtcNow,
                            Data = new AIErrorEventData { ErrorText = String(raw, "message") ?? String(raw, "code") ?? raw.GetRawText() },
                            Metadata = Metadata(raw, sessionId, runId)
                        }
                    };
                    break;
                case "message_completed":
                    yield return NativeEvent("data-jenova-message_completed", eventId, raw, sessionId, runId);
                    var messageId = String(raw, "message_id");
                    if (string.Equals(String(raw, "type"), "external", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(String(Property(raw, "from"), "type"), "agent", StringComparison.OrdinalIgnoreCase)
                        && sessionId is not null && messageId is not null)
                    {
                        using var completed = await SendAsync(
                            HttpMethod.Get,
                            $"sessions/{Uri.EscapeDataString(sessionId)}/messages/{Uri.EscapeDataString(messageId)}",
                            null,
                            call.ApiKey,
                            cancellationToken);
                        var completedRaw = await ReadJsonAsync(completed, cancellationToken);
                        var files = CreateFileParts(completedRaw);
                        foreach (var file in files)
                            yield return new AIStreamEvent
                            {
                                ProviderId = GetIdentifier(),
                                Metadata = Metadata(completedRaw, sessionId, runId),
                                Event = new AIEventEnvelope
                                {
                                    Type = "file",
                                    Id = messageId,
                                    Timestamp = DateTimeOffset.UtcNow,
                                    Data = new AIFileEventData
                                    {
                                        MediaType = file.MediaType ?? "application/octet-stream",
                                        Url = file.Data?.ToString() ?? string.Empty,
                                        Filename = file.Filename,
                                        ProviderMetadata = ScopedNested(completedRaw, sessionId, runId)
                                    },
                                    Metadata = Metadata(completedRaw, sessionId, runId)
                                }
                            };
                    }
                    break;
                case "stream_ended":
                    if (reasoningStarted)
                    {
                        reasoningStarted = false;
                        yield return NativeEvent("reasoning-end", eventId, raw, sessionId, runId);
                    }
                    if (textStarted)
                    {
                        textStarted = false;
                        yield return NativeEvent("text-end", eventId, raw, sessionId, runId);
                    }

                    var succeeded = Boolean(raw, "success") == true;
                    var stopReason = String(raw, "stop_reason") ?? (succeeded ? "end_run" : "error");
                    run.Ended = true;
                    if (!succeeded)
                        yield return new AIStreamEvent
                        {
                            ProviderId = GetIdentifier(),
                            Metadata = Metadata(raw, sessionId, runId),
                            Event = new AIEventEnvelope
                            {
                                Type = "error",
                                Id = eventId,
                                Timestamp = DateTimeOffset.UtcNow,
                                Data = new AIErrorEventData { ErrorText = stopReason },
                                Metadata = Metadata(raw, sessionId, runId)
                            }
                        };
                    yield return new AIStreamEvent
                    {
                        ProviderId = GetIdentifier(),
                        Metadata = Metadata(raw, sessionId, runId),
                        Event = new AIEventEnvelope
                        {
                            Type = "finish",
                            Id = eventId,
                            Timestamp = DateTimeOffset.UtcNow,
                            Data = new AIFinishEventData
                            {
                                FinishReason = succeeded ? stopReason : "error",
                                Model = call.Model ?? request.Model,
                                Response = raw,
                                MessageMetadata = AIFinishMessageMetadata.Create(
                                    call.Model ?? request.Model ?? call.Agent,
                                    DateTimeOffset.UtcNow,
                                    usage: Property(raw, "usage"),
                                    additionalProperties: new Dictionary<string, object?> { ["jenova"] = raw })
                            },
                            Metadata = Metadata(raw, sessionId, runId)
                        }
                    };
                    ended = true;
                    break;
                default:
                    yield return NativeEvent(
                        frame.Event is "stream_started" or "stream_progress" or "mcp_connection"
                            or "mcp_connection_resolved" or "warning"
                            ? $"data-jenova-{frame.Event}"
                            : "data-jenova-event",
                        eventId,
                        raw,
                        sessionId,
                        runId,
                        transient: frame.Event == "stream_progress");
                    break;
            }

            if (ended)
                yield break;
        }

        if (!ended)
            throw new IOException("Jenova SSE stream ended before the stream_ended event.");
    }

    private async Task CancelRunAsync(JenovaCall call, string sessionId, string runId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await SendAsync(
            HttpMethod.Post,
            $"sessions/{Uri.EscapeDataString(sessionId)}/cancel",
            JsonSerializer.SerializeToElement(new { run_id = runId }, JsonOptions),
            call.ApiKey,
            timeout.Token);
    }

    private JenovaCall CreateCall(AIRequest request, bool stream)
    {
        var apiKey = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("No Jenova API key.");

        if (!string.IsNullOrWhiteSpace(request.Instructions)
            || request.Tools is { Count: > 0 }
            || request.Temperature.HasValue
            || request.TopP.HasValue
            || request.MaxOutputTokens.HasValue
            || request.MaxToolCalls.HasValue
            || request.ToolChoice is not null
            || request.ResponseFormat is not null
            || request.ParallelToolCalls.HasValue
            || !string.IsNullOrWhiteSpace(request.Verbosity))
            throw new NotSupportedException("Jenova uses agent-configured instructions, tools, and generation settings; these request-level options are not supported.");

        var options = ProviderOptions(request);
        var sessionId = String(options, "session_id");
        var inputItems = request.Input?.Items ?? [];
        if (inputItems.Any(item => string.Equals(item.Role, "system", StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(item.Role, "developer", StringComparison.OrdinalIgnoreCase)))
            throw new NotSupportedException("Jenova instructions are configured on the agent; request-level system and developer messages are not supported.");
        if (sessionId is null
            && (inputItems.Count(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase)) > 1
                || inputItems.Any(item => string.Equals(item.Role, "assistant", StringComparison.OrdinalIgnoreCase))))
            throw new ArgumentException("Jenova chat history requires an explicit jenova.session_id; the gateway does not retain conversation state.", nameof(request));

        var route = ParseModel(request.Model, String(options, "agent"));
        var agent = route.Agent;
        var model = String(options, "model") ?? route.Model;
        var ephemeral = Boolean(options, "ephemeral") == true;
        if (ephemeral && sessionId is not null)
            throw new ArgumentException("Jenova ephemeral requests cannot continue an existing session.", nameof(request));
        if (ephemeral && !stream)
            throw new ArgumentException("Jenova ephemeral requests require streaming.", nameof(request));

        var payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (options.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in options.EnumerateObject())
            {
                if (property.Name is "session_id" or "idempotency_key" or "Idempotency-Key")
                    continue;
                payload[property.Name] = property.Value.Clone();
            }
        }

        if (sessionId is null)
            payload["agent"] = JsonSerializer.SerializeToElement(agent, JsonOptions);
        else
            payload.Remove("agent");
        payload["stream"] = JsonSerializer.SerializeToElement(stream, JsonOptions);
        if (model is not null)
            payload["model"] = JsonSerializer.SerializeToElement(model, JsonOptions);
        if (sessionId is not null)
        {
            payload.Remove("ephemeral");
            payload.Remove("session_name");
        }

        var (content, fileUrls) = GetUserInput(request);
        if (!string.IsNullOrWhiteSpace(content))
            payload["content"] = JsonSerializer.SerializeToElement(content, JsonOptions);
        if (fileUrls.Count > 0)
            payload["file_urls"] = JsonSerializer.SerializeToElement(fileUrls, JsonOptions);
        if (content is null && fileUrls.Count == 0
            && !payload.TryGetValue("content", out _)
            && !payload.TryGetValue("file_urls", out _))
            throw new ArgumentException("Jenova requires user text or HTTPS file URLs.", nameof(request));

        var body = JsonSerializer.SerializeToElement(payload, JsonOptions);
        var idempotencyKey = request.Headers?.FirstOrDefault(header =>
            string.Equals(header.Key, "Idempotency-Key", StringComparison.OrdinalIgnoreCase)).Value
            ?? String(options, "idempotency_key")
            ?? String(options, "Idempotency-Key");
        return new JenovaCall(
            apiKey,
            agent,
            model,
            sessionId,
            idempotencyKey,
            sessionId is null ? "messages" : $"sessions/{Uri.EscapeDataString(sessionId)}/messages",
            body);
    }

    private AIResponse CreateMessageResponse(AIRequest request, JsonElement raw)
    {
        if (String(raw, "status") is "processing" or "queued" or "cancelled")
            return CreateAcceptedResponse(request, raw);

        var content = new List<AIContentPart>();
        if (String(raw, "content") is { Length: > 0 } text)
            content.Add(new AITextContentPart
            {
                Type = "text",
                Text = text,
                Metadata = new Dictionary<string, object?> { ["jenova.raw"] = raw }
            });
        content.AddRange(CreateFileParts(raw));
        var model = String(raw, "model") ?? request.Model;
        var metadata = Metadata(raw, String(raw, "session_id"), String(raw, "run_id"));
        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = model,
            Status = String(raw, "stop_reason") ?? "completed",
            Output = new AIOutput
            {
                Items = content.Count == 0 ? null : [new AIOutputItem { Role = "assistant", Content = content, Metadata = metadata }],
                Metadata = metadata
            },
            Usage = Usage(raw),
            Metadata = metadata
        };
    }

    private AIResponse CreateAcceptedResponse(AIRequest request, JsonElement raw)
    {
        var metadata = Metadata(raw, String(raw, "session_id"), String(raw, "run_id"));
        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = String(raw, "model") ?? request.Model,
            Status = String(raw, "status") ?? "processing",
            Output = new AIOutput { Metadata = metadata },
            Usage = Usage(raw),
            Metadata = metadata
        };
    }

    private static List<AIFileContentPart> CreateFileParts(JsonElement message)
    {
        var files = new List<AIFileContentPart>();
        if (!message.TryGetProperty("files", out var rawFiles) || rawFiles.ValueKind != JsonValueKind.Array)
            return files;
        foreach (var file in rawFiles.EnumerateArray())
        {
            var url = String(file, "url");
            if (string.IsNullOrWhiteSpace(url))
                continue;
            var name = String(file, "name");
            var format = String(file, "format");
            files.Add(new AIFileContentPart
            {
                Type = "file",
                Filename = name,
                MediaType = MediaTypeFor(format),
                Data = url,
                Metadata = new Dictionary<string, object?> { ["jenova.raw"] = file.Clone() }
            });
        }
        return files;
    }

    private static string? MediaTypeFor(string? format) => format?.ToLowerInvariant() switch
    {
        "jpg" or "jpeg" => "image/jpeg",
        "png" => "image/png",
        "webp" => "image/webp",
        "pdf" => "application/pdf",
        "json" => "application/json",
        "csv" => "text/csv",
        "txt" or "md" or "log" => "text/plain",
        _ => null
    };

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        JsonElement? payload,
        string apiKey,
        CancellationToken cancellationToken,
        string? idempotencyKey = null)
    {
        using var request = CreateHttpRequest(method, path, payload, apiKey, idempotencyKey);
        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var exception = await CreateHttpErrorAsync(response, cancellationToken);
            response.Dispose();
            throw exception;
        }
        return response;
    }

    private static HttpRequestMessage CreateHttpRequest(
        HttpMethod method, string path, JsonElement? payload, string apiKey, string? idempotencyKey)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        if (payload.HasValue)
            request.Content = new StringContent(payload.Value.GetRawText(), Encoding.UTF8, "application/json");
        return request;
    }

    private static async Task<HttpRequestException> CreateHttpErrorAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var detail = body;
        try
        {
            using var document = JsonDocument.Parse(body);
            var error = Property(document.RootElement, "error");
            var code = String(error, "code");
            var errorMessage = String(error, "message");
            detail = code is null ? errorMessage ?? body : errorMessage is null ? code : $"{code}: {errorMessage}";
        }
        catch (JsonException)
        {
        }

        return new HttpRequestException(
            $"Jenova request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {detail}",
            null,
            response.StatusCode);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.Clone();
    }

    private static JsonElement ProviderOptions(AIRequest request)
    {
        if (request.Metadata?.TryGetValue("jenova", out var value) != true || value is null)
            return EmptyObject;
        var options = value is JsonElement json ? json : JsonSerializer.SerializeToElement(value, JsonOptions);
        if (options.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Jenova provider metadata must be a JSON object.", nameof(request));
        return options;
    }

    private static (string Agent, string? Model) ParseModel(string? model, string? metadataAgent)
    {
        var segments = model?.Split('/');
        if (segments is { Length: 3 } && segments[0].Equals("jenova", StringComparison.OrdinalIgnoreCase))
        {
            if (segments[1] == "agent" && IsSlug(segments[2]))
                return (segments[2], null);
            if (segments[1] == "model" && IsSlug(segments[2]) && IsSlug(metadataAgent))
                return (metadataAgent!, segments[2]);
        }
        if (IsSlug(metadataAgent))
            return (metadataAgent!, null);
        throw new ArgumentException("Select a Jenova agent model 'jenova/agent/{agent}' or provide jenova.agent for a model override.", nameof(model));
    }

    private static bool IsSlug(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static (string? Content, List<string> FileUrls) GetUserInput(AIRequest request)
    {
        var user = request.Input?.Items?
            .LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));
        if (user is null)
            return (request.Input?.Text, []);

        var content = string.Join("\n", user.Content?.OfType<AITextContentPart>()
            .Select(part => part.Text).Where(text => !string.IsNullOrWhiteSpace(text)) ?? []);
        var fileParts = user.Content?.OfType<AIFileContentPart>().ToList() ?? [];
        var urls = fileParts
            .Select(part =>
            {
                var value = part.Data switch
                {
                    string stringValue => stringValue,
                    Uri uriValue => uriValue.ToString(),
                    JsonElement jsonValue when jsonValue.ValueKind == JsonValueKind.String => jsonValue.GetString(),
                    _ => null
                };
                if (part.Data is null)
                    return null;
                if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                    || uri.Scheme != Uri.UriSchemeHttps)
                    throw new ArgumentException("Jenova file inputs must be publicly accessible HTTPS URLs.", nameof(request));
                return value;
            })
            .Where(value => value is not null)
            .Select(value => value!)
            .Distinct(StringComparer.Ordinal)
            .ToList() ?? [];
        return (content.Length == 0 ? null : content, urls);
    }

    private static AIUsage? Usage(JsonElement raw)
    {
        var usage = Property(raw, "usage");
        if (usage.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return null;
        return new AIUsage
        {
            AdditionalProperties = usage.ValueKind == JsonValueKind.Object
                ? usage.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal)
                : new Dictionary<string, JsonElement> { ["raw"] = usage.Clone() }
        };
    }

    private AIStreamEvent NativeEvent(
        string type, string id, JsonElement raw, string? sessionId, string? runId, bool transient = false)
    {
        var metadata = Metadata(raw, sessionId, runId);
        object data = type switch
        {
            "text-start" => new AITextStartEventData { ProviderMetadata = Scoped(raw, sessionId, runId) },
            "text-end" => new AITextEndEventData { ProviderMetadata = Scoped(raw, sessionId, runId) },
            "reasoning-start" => new AIReasoningStartEventData { ProviderMetadata = ScopedNested(raw, sessionId, runId) },
            "reasoning-end" => new AIReasoningEndEventData { ProviderMetadata = ScopedNested(raw, sessionId, runId) },
            _ => new AIDataEventData { Id = id, Data = raw.Clone(), Transient = transient }
        };
        return new AIStreamEvent
        {
            ProviderId = GetIdentifier(),
            Metadata = metadata,
            Event = new AIEventEnvelope
            {
                Type = type,
                Id = id,
                Timestamp = DateTimeOffset.UtcNow,
                Data = data,
                Metadata = metadata
            }
        };
    }

    private static Dictionary<string, object?> Metadata(JsonElement raw, string? sessionId, string? runId)
        => new()
        {
            ["jenova"] = new Dictionary<string, object?>
            {
                ["session_id"] = sessionId,
                ["run_id"] = runId,
                ["raw"] = raw.Clone()
            }
        };

    private static Dictionary<string, object> Scoped(JsonElement raw, string? sessionId, string? runId)
        => new() { ["jenova"] = new Dictionary<string, object> { ["raw"] = raw.Clone(), ["session_id"] = sessionId!, ["run_id"] = runId! } };

    private static Dictionary<string, Dictionary<string, object>> ScopedNested(JsonElement raw, string? sessionId, string? runId)
        => new() { ["jenova"] = Scoped(raw, sessionId, runId)["jenova"] as Dictionary<string, object> ?? [] };

    private static async IAsyncEnumerable<SseFrame> ReadSseFramesAsync(
        StreamReader reader, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var eventName = "message";
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                    yield return new SseFrame(eventName, data.ToString().TrimEnd('\n'));
                eventName = "message";
                data.Clear();
                continue;
            }

            if (line.StartsWith(':'))
                continue;
            var separator = line.IndexOf(':');
            var field = separator < 0 ? line : line[..separator];
            var value = separator < 0 ? string.Empty : line[(separator + 1)..].TrimStart(' ');
            if (field == "event")
                eventName = value;
            else if (field == "data")
                data.Append(value).Append('\n');
        }

        if (data.Length > 0)
            yield return new SseFrame(eventName, data.ToString().TrimEnd('\n'));
    }

    private static JsonElement Property(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value
            : default;

    private static string? String(JsonElement element, string name)
    {
        var value = Property(element, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static bool? Boolean(JsonElement element, string name)
    {
        var value = Property(element, name);
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    }

    private static IEnumerable<JsonElement> Array(JsonElement element, string name)
    {
        var value = Property(element, name);
        return value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Select(item => item.Clone()) : [];
    }

    private sealed record JenovaCall(
        string ApiKey, string Agent, string? Model, string? SessionId, string? IdempotencyKey,
        string Path, JsonElement Body);

    private sealed class JenovaRun(string? sessionId)
    {
        public string? SessionId { get; set; } = sessionId;
        public string? RunId { get; set; }
        public bool Ended { get; set; }
    }

    private sealed record SseFrame(string Event, string Data);
}
