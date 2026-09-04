using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Agent37;

public partial class Agent37Provider
{
    private const string Agent37SessionToolName = "create_agent37_session";
    private static readonly JsonSerializerOptions Agent37Json = JsonSerializerOptions.Web;
    private static readonly HashSet<string> Agent37Harnesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "hermes", "openclaw", "claude-code", "codex", "grok", "opencode"
    };

    private sealed record Agent37Route(string InstanceId, string? Agent, string? Model)
    {
        public string BaseUrl => $"https://{InstanceId}.agent37.app";
    }

    private sealed record Agent37Attachment(string Filename, string MediaType, byte[] Bytes);

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var route = ParseAgent37Route(request.Model);
        var sessionId = TryFindAgent37SessionId(request, out var recovered) ? recovered : null;
        var files = await UploadAgent37AttachmentsAsync(route, request, cancellationToken);
        var payload = BuildAgent37Payload(request, route, sessionId, files, stream: false);
        var raw = await SendAgent37JsonAsync(route, HttpMethod.Post, "/v1/responses", payload,
            "send message", cancellationToken);

        var returnedSessionId = GetString(raw, "session_id")
            ?? throw new InvalidOperationException("Agent37 response did not include session_id.");
        var output = new List<AIContentPart>();
        if (sessionId is null)
            output.Add(CreateAgent37SessionToolPart(returnedSessionId, route, raw));

        var text = GetString(raw, "output_text");
        if (!string.IsNullOrEmpty(text))
        {
            output.Add(new AITextContentPart
            {
                Type = "text",
                Text = text,
                Metadata = Agent37RawMetadata(raw)
            });
        }

        var error = GetObject(raw, "error");
        if (error.HasValue)
        {
            output.Add(new AIToolCallContentPart
            {
                ToolCallId = $"agent37-error-{GetString(raw, "id") ?? Guid.NewGuid().ToString("N")}",
                ToolName = "agent37_error",
                Title = "Agent37 error",
                Type = "tool-call",
                Input = new { },
                Output = new CallToolResult { IsError = true, StructuredContent = error.Value.Clone() },
                ProviderExecuted = true,
                State = "output-error",
                Metadata = Agent37RawMetadata(error.Value)
            });
        }

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = FormatAgent37ModelId(route),
            Status = GetString(raw, "status") ?? "completed",
            Output = new AIOutput
            {
                Items = output.Count == 0 ? null :
                [
                    new AIOutputItem { Type = "message", Role = "assistant", Content = output,
                        Metadata = CreateAgent37ResponseMetadata(raw, route) }
                ],
                Metadata = CreateAgent37ResponseMetadata(raw, route)
            },
            Usage = CreateAgent37Usage(GetObject(raw, "usage")),
            Metadata = CreateAgent37ResponseMetadata(raw, route)
        };
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var route = ParseAgent37Route(request.Model);
        var existingSession = TryFindAgent37SessionId(request, out var recovered) ? recovered : null;
        var files = await UploadAgent37AttachmentsAsync(route, request, cancellationToken);
        var payload = BuildAgent37Payload(request, route, existingSession, files, stream: true);

        using var httpRequest = CreateAgent37Request(route, HttpMethod.Post, "/v1/responses");
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        httpRequest.Content = new StringContent(JsonSerializer.Serialize(payload, Agent37Json), Encoding.UTF8,
            MediaTypeNames.Application.Json);
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureAgent37SuccessAsync(response, "stream response", cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        string? sessionId = existingSession;
        string? responseId = null;
        var sessionEmitted = existingSession is not null;
        var textStarted = false;
        var reasoningStarted = false;
        var textId = $"agent37-text-{Guid.NewGuid():N}";
        var reasoningId = $"agent37-reasoning-{Guid.NewGuid():N}";
        var activeTools = new Dictionary<string, Queue<string>>(StringComparer.OrdinalIgnoreCase);

        await foreach (var frame in ReadAgent37SseAsync(reader, cancellationToken))
        {
            var type = frame.Event;
            var data = frame.Data;
            var now = DateTimeOffset.UtcNow;
            var metadata = CreateAgent37EventMetadata(type, data, route, sessionId, responseId);

            if (type == "response.created")
            {
                sessionId = GetString(data, "session_id") ?? sessionId;
                responseId = GetString(data, "id") ?? responseId;
                if (!sessionEmitted && !string.IsNullOrWhiteSpace(sessionId))
                {
                    foreach (var evt in CreateAgent37SessionToolEvents(sessionId!, route, data, now))
                        yield return evt;
                    sessionEmitted = true;
                }
                yield return Agent37Event("data-agent37-response-created", responseId,
                    new AIDataEventData { Id = responseId, Data = data.Clone(), Transient = false }, now, metadata);
                continue;
            }

            if (type == "response.output_text.delta")
            {
                if (!textStarted)
                {
                    textStarted = true;
                    yield return Agent37Event("text-start", textId,
                        new AITextStartEventData { ProviderMetadata = FlattenAgent37Metadata(type, data) }, now, metadata);
                }
                yield return Agent37Event("text-delta", textId,
                    new AITextDeltaEventData
                    {
                        Delta = GetString(data, "text") ?? string.Empty,
                        ProviderMetadata = FlattenAgent37Metadata(type, data)
                    }, now, metadata);
                continue;
            }

            if (type == "response.reasoning.delta")
            {
                if (!reasoningStarted)
                {
                    reasoningStarted = true;
                    yield return Agent37Event("reasoning-start", reasoningId,
                        new AIReasoningStartEventData { ProviderMetadata = ScopedAgent37Metadata(type, data) }, now, metadata);
                }
                yield return Agent37Event("reasoning-delta", reasoningId,
                    new AIReasoningDeltaEventData
                    {
                        Delta = GetString(data, "text") ?? string.Empty,
                        ProviderMetadata = ScopedAgent37Metadata(type, data)
                    }, now, metadata);
                continue;
            }

            if (type == "response.tool_call.started")
            {
                var tool = GetString(data, "tool") ?? "agent37_tool";
                var callId = $"agent37-{NormalizeAgent37Name(tool)}-{Guid.NewGuid():N}";
                if (!activeTools.TryGetValue(tool, out var queue)) activeTools[tool] = queue = new Queue<string>();
                queue.Enqueue(callId);
                yield return Agent37Event("tool-input-available", callId,
                    new AIToolInputAvailableEventData
                    {
                        ToolName = NormalizeAgent37Name(tool),
                        Title = GetString(data, "label") ?? tool,
                        Input = data.Clone(),
                        ProviderExecuted = true,
                        ProviderMetadata = ScopedAgent37Metadata(type, data)
                    }, now, metadata);
                continue;
            }

            if (type is "response.tool_call.completed" or "response.tool_call.failed")
            {
                var tool = GetString(data, "tool") ?? "agent37_tool";
                var callId = activeTools.TryGetValue(tool, out var queue) && queue.Count > 0
                    ? queue.Dequeue()
                    : $"agent37-{NormalizeAgent37Name(tool)}-{Guid.NewGuid():N}";
                if (type.EndsWith("failed", StringComparison.Ordinal))
                {
                    yield return Agent37Event("tool-output-error", callId,
                        new AIToolOutputErrorEventData
                        {
                            ToolCallId = callId,
                            ErrorText = GetString(data, "error") ?? $"Agent37 tool '{tool}' failed.",
                            ProviderExecuted = true,
                            Dynamic = true,
                            ProviderMetadata = ScopedAgent37Metadata(type, data)
                        }, now, metadata);
                }
                else
                {
                    yield return Agent37Event("tool-output-available", callId,
                        new AIToolOutputAvailableEventData
                        {
                            ToolName = NormalizeAgent37Name(tool),
                            Output = new CallToolResult { StructuredContent = data.Clone() },
                            ProviderExecuted = true,
                            Dynamic = true,
                            ProviderMetadata = ScopedAgent37Metadata(type, data)
                        }, now, metadata);
                }
                continue;
            }

            if (type == "response.failed")
            {
                if (reasoningStarted)
                    yield return Agent37Event("reasoning-end", reasoningId,
                        new AIReasoningEndEventData { ProviderMetadata = ScopedAgent37Metadata(type, data) }, now, metadata);
                if (textStarted)
                    yield return Agent37Event("text-end", textId,
                        new AITextEndEventData { ProviderMetadata = FlattenAgent37Metadata(type, data) }, now, metadata);
                yield return Agent37Event("error", responseId,
                    new AIErrorEventData { ErrorText = ExtractAgent37Error(data) }, now, metadata);
                yield break;
            }

            if (type == "response.completed")
            {
                if (reasoningStarted)
                    yield return Agent37Event("reasoning-end", reasoningId,
                        new AIReasoningEndEventData { ProviderMetadata = ScopedAgent37Metadata(type, data) }, now, metadata);
                if (textStarted)
                    yield return Agent37Event("text-end", textId,
                        new AITextEndEventData { ProviderMetadata = FlattenAgent37Metadata(type, data) }, now, metadata);
                var usage = GetObject(data, "usage");
                var inputTokens = GetInt(usage, "input_tokens");
                var outputTokens = GetInt(usage, "output_tokens");
                yield return Agent37Event("finish", responseId ?? sessionId,
                    new AIFinishEventData
                    {
                        FinishReason = "stop",
                        Model = FormatAgent37ModelId(route),
                        CompletedAt = now.ToUnixTimeSeconds(),
                        InputTokens = inputTokens,
                        OutputTokens = outputTokens,
                        TotalTokens = AddTokens(inputTokens, outputTokens),
                        MessageMetadata = AIFinishMessageMetadata.Create(FormatAgent37ModelId(route), now,
                            usage, inputTokens: inputTokens, outputTokens: outputTokens,
                            totalTokens: AddTokens(inputTokens, outputTokens),
                            gateway: CreateAgent37GatewayMetadata(usage),
                            additionalProperties: new Dictionary<string, object?>
                            {
                                [GetIdentifier()] = new
                                {
                                    session_id = sessionId,
                                    response_id = responseId,
                                    context = GetObject(data, "context"),
                                    raw = data.Clone()
                                }
                            })
                    }, now, metadata);
                yield break;
            }
        }
    }

    internal static (string InstanceId, string? Agent, string? Model) ParseAgent37ModelSlug(string? model)
    {
        var route = ParseAgent37Route(model);
        return (route.InstanceId, route.Agent, route.Model);
    }

    private static Agent37Route ParseAgent37Route(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Agent37 requires model slug 'agent37/{instanceId}' or 'agent37/{instanceId}/{agent}/{model}'.", nameof(model));
        var value = model.Trim().Trim('/');
        if (value.StartsWith("agent37/", StringComparison.OrdinalIgnoreCase)) value = value[8..];
        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || string.IsNullOrWhiteSpace(segments[0]))
            throw new ArgumentException("Agent37 model slug is missing the instance id.", nameof(model));
        if (segments.Length is 2)
            throw new ArgumentException("Agent37 compound model slug must include both agent and model.", nameof(model));
        if (segments.Length >= 3 && !Agent37Harnesses.Contains(segments[1]))
            throw new ArgumentException($"Unsupported Agent37 harness '{segments[1]}'.", nameof(model));
        return new Agent37Route(segments[0], segments.Length >= 3 ? segments[1] : null,
            segments.Length >= 3 ? string.Join('/', segments.Skip(2)) : null);
    }

    private Dictionary<string, object?> BuildAgent37Payload(AIRequest request, Agent37Route route,
        string? sessionId, List<string> files, bool stream)
    {
        var payload = new Dictionary<string, object?>
        {
            ["input"] = ExtractLatestAgent37UserText(request),
            ["stream"] = stream
        };
        if (files.Count > 0) payload["files"] = files;
        if (!string.IsNullOrWhiteSpace(sessionId)) payload["session_id"] = sessionId;
        if (!string.IsNullOrWhiteSpace(route.Agent)) payload["agent"] = route.Agent;
        if (!string.IsNullOrWhiteSpace(route.Model)) payload["model"] = route.Model;

        var options = GetAgent37Options(request);
        CopySafeAgent37Option(options, payload, "provider");
        CopySafeAgent37Option(options, payload, "reasoning_effort", "reasoningEffort");
        CopySafeAgent37Option(options, payload, "mode");
        CopySafeAgent37Option(options, payload, "metadata");
        if (route.Agent is null) CopySafeAgent37Option(options, payload, "agent");
        if (route.Model is null) CopySafeAgent37Option(options, payload, "model");
        return payload;
    }

    private static string ExtractLatestAgent37UserText(AIRequest request)
    {
        var latest = request.Input?.Items?.LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));
        var text = string.Join("\n", latest?.Content?.OfType<AITextContentPart>()
            .Select(part => part.Text).Where(value => !string.IsNullOrWhiteSpace(value)) ?? []);
        text = string.IsNullOrWhiteSpace(text) ? request.Input?.Text ?? request.Instructions : text;
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Agent37 requires a non-empty latest user message.");
        return text;
    }

    private async Task<List<string>> UploadAgent37AttachmentsAsync(Agent37Route route, AIRequest request,
        CancellationToken cancellationToken)
    {
        var latest = request.Input?.Items?.LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));
        var attachments = (latest?.Content?.OfType<AIFileContentPart>() ?? []).Select(DecodeAgent37Attachment).ToList();
        var paths = new List<string>(attachments.Count);
        for (var index = 0; index < attachments.Count; index++)
        {
            var attachment = attachments[index];
            var safeName = SanitizeAgent37Filename(attachment.Filename);
            var path = $"/home/user/.agent37-gateway/workspace/aihappey/{Guid.NewGuid():N}-{index}-{safeName}";
            using var upload = CreateAgent37Request(route, HttpMethod.Put,
                $"/v1/files/content?path={Uri.EscapeDataString(path)}&overwrite=false");
            upload.Content = new ByteArrayContent(attachment.Bytes);
            upload.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(attachment.MediaType);
            using var response = await _client.SendAsync(upload, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            await EnsureAgent37SuccessAsync(response, $"upload '{attachment.Filename}'", cancellationToken);
            var raw = await ReadAgent37JsonAsync(response, cancellationToken);
            paths.Add(GetString(raw, "path") ?? path);
        }
        return paths;
    }

    private static Agent37Attachment DecodeAgent37Attachment(AIFileContentPart file, int index)
    {
        var filename = string.IsNullOrWhiteSpace(file.Filename) ? $"attachment-{index + 1}" : file.Filename!.Trim();
        var value = file.Data?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"Agent37 attachment '{filename}' has no data.");
        var mediaType = string.IsNullOrWhiteSpace(file.MediaType) ? MediaTypeNames.Application.Octet : file.MediaType!;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            throw new NotSupportedException($"Agent37 attachment '{filename}' must be raw base64 or a base64 data URL.");
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = value.IndexOf(',');
            if (comma < 0 || !value[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Agent37 attachment '{filename}' has an invalid data URL.");
            var header = value[5..comma];
            var semicolon = header.IndexOf(';');
            if (semicolon > 0) mediaType = header[..semicolon];
            value = value[(comma + 1)..];
        }
        try { return new Agent37Attachment(filename, mediaType, Convert.FromBase64String(value)); }
        catch (FormatException ex) { throw new ArgumentException($"Agent37 attachment '{filename}' contains invalid base64.", ex); }
    }

    private HttpRequestMessage CreateAgent37Request(Agent37Route route, HttpMethod method, string path)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("No Agent37 API key.");
        var request = new HttpRequestMessage(method, route.BaseUrl + path);
        request.Headers.Add("X-Agent37-Key", key);
        return request;
    }

    private async Task<JsonElement> SendAgent37JsonAsync(Agent37Route route, HttpMethod method, string path,
        object? payload, string operation, CancellationToken cancellationToken)
    {
        using var request = CreateAgent37Request(route, method, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));
        if (payload is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(payload, Agent37Json), Encoding.UTF8, MediaTypeNames.Application.Json);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureAgent37SuccessAsync(response, operation, cancellationToken);
        return await ReadAgent37JsonAsync(response, cancellationToken);
    }

    private static async Task EnsureAgent37SuccessAsync(HttpResponseMessage response, string operation,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException($"Agent37 {operation} failed with status {(int)response.StatusCode}: {raw}",
            null, response.StatusCode);
    }

    private static async Task<JsonElement> ReadAgent37JsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(raw)) return JsonSerializer.SerializeToElement(new { }, Agent37Json);
        return JsonSerializer.Deserialize<JsonElement>(raw, Agent37Json).Clone();
    }

    private bool TryFindAgent37SessionId(AIRequest request, out string sessionId)
    {
        var options = GetAgent37Options(request);
        sessionId = GetString(options, "session_id") ?? GetString(options, "sessionId")
            ?? GetDictionaryString(request.Input?.Metadata, "session_id")
            ?? GetDictionaryString(request.Input?.Metadata, "sessionId") ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(sessionId)) return true;
        foreach (var item in request.Input?.Items ?? [])
        {
            if (TryExtractAgent37Session(item.Metadata, out sessionId)) return true;
            foreach (var tool in item.Content?.OfType<AIToolCallContentPart>() ?? [])
            {
                if (tool.ProviderExecuted != true) continue;
                if (TryExtractAgent37Session(tool.Output, out sessionId)
                    || TryExtractAgent37Session(tool.Metadata, out sessionId)
                    || (string.Equals(tool.ToolName, Agent37SessionToolName, StringComparison.OrdinalIgnoreCase)
                        && TryExtractAgent37Session(tool.Input, out sessionId))) return true;
            }
        }
        sessionId = string.Empty;
        return false;
    }

    private static bool TryExtractAgent37Session(object? value, out string sessionId)
    {
        sessionId = string.Empty;
        if (value is null) return false;
        var element = value is JsonElement json ? json : JsonSerializer.SerializeToElement(value, Agent37Json);
        if (element.ValueKind != JsonValueKind.Object) return false;
        foreach (var name in new[] { "structuredContent", "output", "agent37", "session" })
            if (TryGetProperty(element, name, out var nested) && TryExtractAgent37Session(nested, out sessionId)) return true;
        sessionId = GetString(element, "session_id") ?? GetString(element, "sessionId") ?? string.Empty;
        return !string.IsNullOrWhiteSpace(sessionId);
    }

    private AIToolCallContentPart CreateAgent37SessionToolPart(string sessionId, Agent37Route route, JsonElement raw)
        => new()
        {
            ToolCallId = BuildAgent37SessionToolCallId(sessionId),
            ToolName = Agent37SessionToolName,
            Title = "Create Agent37 session",
            Type = "tool-call",
            Input = new { instance_id = route.InstanceId, agent = route.Agent, model = route.Model },
            Output = CreateAgent37SessionResult(sessionId, route, raw),
            ProviderExecuted = true,
            State = "output-available",
            Metadata = CreateAgent37SessionMetadata(sessionId, route, raw)
        };

    private IEnumerable<AIStreamEvent> CreateAgent37SessionToolEvents(string sessionId, Agent37Route route,
        JsonElement raw, DateTimeOffset timestamp)
    {
        var callId = BuildAgent37SessionToolCallId(sessionId);
        var providerMetadata = ScopedAgent37Metadata(Agent37SessionToolName, raw);
        yield return Agent37Event("tool-input-available", callId, new AIToolInputAvailableEventData
        {
            ToolName = Agent37SessionToolName,
            Title = "Create Agent37 session",
            Input = new { instance_id = route.InstanceId, agent = route.Agent, model = route.Model },
            ProviderExecuted = true,
            ProviderMetadata = providerMetadata
        }, timestamp, null);
        yield return Agent37Event("tool-output-available", callId, new AIToolOutputAvailableEventData
        {
            ToolName = Agent37SessionToolName,
            Output = CreateAgent37SessionResult(sessionId, route, raw),
            ProviderExecuted = true,
            ProviderMetadata = providerMetadata
        }, timestamp, null);
    }

    private static CallToolResult CreateAgent37SessionResult(string sessionId, Agent37Route route, JsonElement raw)
        => new()
        {
            Content = [],
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                type = Agent37SessionToolName,
                sessionId,
                session_id = sessionId,
                instance_id = route.InstanceId,
                agent = route.Agent,
                model = route.Model,
                raw = raw.Clone()
            }, Agent37Json)
        };

    private Dictionary<string, object?> CreateAgent37SessionMetadata(string sessionId, Agent37Route route, JsonElement raw)
        => new()
        {
            [GetIdentifier()] = new
            {
                type = Agent37SessionToolName,
                sessionId,
                session_id = sessionId,
                instance_id = route.InstanceId,
                agent = route.Agent,
                model = route.Model,
                raw = raw.Clone()
            },
            ["type"] = Agent37SessionToolName,
            ["sessionId"] = sessionId,
            ["session_id"] = sessionId,
            ["tool_name"] = Agent37SessionToolName
        };

    private AIStreamEvent Agent37Event(string type, string? id, object data, DateTimeOffset timestamp,
        Dictionary<string, object?>? metadata) => new()
        {
            ProviderId = GetIdentifier(),
            Event = new AIEventEnvelope { Type = type, Id = id, Timestamp = timestamp, Data = data },
            Metadata = metadata
        };

    private static async IAsyncEnumerable<(string Event, JsonElement Data)> ReadAgent37SseAsync(
        StreamReader reader, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? eventName = null;
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                if (!string.IsNullOrWhiteSpace(eventName) && data.Length > 0)
                {
                    JsonElement parsed;
                    try { parsed = JsonSerializer.Deserialize<JsonElement>(data.ToString(), Agent37Json).Clone(); }
                    catch (JsonException) { eventName = null; data.Clear(); continue; }
                    yield return (eventName, parsed);
                }
                eventName = null; data.Clear(); continue;
            }
            if (line.StartsWith(':')) continue;
            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase)) eventName = line[6..].Trim();
            else if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                if (data.Length > 0) data.AppendLine();
                data.Append(line[5..].TrimStart());
            }
        }
    }

    private static Dictionary<string, object?>? GetAgent37Options(AIRequest request)
    {
        if (request.Metadata is null || !request.Metadata.TryGetValue("agent37", out var value) || value is null) return null;
        var element = value is JsonElement json ? json : JsonSerializer.SerializeToElement(value, Agent37Json);
        return element.ValueKind == JsonValueKind.Object
            ? element.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone(), StringComparer.OrdinalIgnoreCase)
            : null;
    }

    private static void CopySafeAgent37Option(Dictionary<string, object?>? source, Dictionary<string, object?> target,
        string destination, string? alias = null)
    {
        if (source is null) return;
        if (source.TryGetValue(destination, out var value) || (alias is not null && source.TryGetValue(alias, out value)))
            if (value is not null) target[destination] = value;
    }

    private Dictionary<string, object?> CreateAgent37ResponseMetadata(JsonElement raw, Agent37Route route)
        => new()
        {
            ["agent37.session_id"] = GetString(raw, "session_id"),
            ["agent37.response_id"] = GetString(raw, "id"),
            ["agent37.instance_id"] = route.InstanceId,
            ["agent37.agent"] = GetString(raw, "agent") ?? route.Agent,
            ["agent37.model"] = GetString(raw, "model") ?? route.Model,
            ["agent37.provider"] = GetString(raw, "provider"),
            ["agent37.status"] = GetString(raw, "status"),
            ["agent37.context"] = GetObject(raw, "context"),
            ["agent37.error"] = GetObject(raw, "error"),
            ["agent37.metadata"] = GetObject(raw, "metadata"),
            ["agent37.raw"] = raw.Clone()
        };

    private Dictionary<string, object?> CreateAgent37EventMetadata(string type, JsonElement raw, Agent37Route route,
        string? sessionId, string? responseId) => new()
        {
            ["agent37.event_type"] = type,
            ["agent37.instance_id"] = route.InstanceId,
            ["agent37.session_id"] = sessionId,
            ["agent37.response_id"] = responseId,
            ["agent37.raw"] = raw.Clone()
        };

    private static Dictionary<string, object?> Agent37RawMetadata(JsonElement raw) => new() { ["agent37.raw"] = raw.Clone() };
    private static Dictionary<string, Dictionary<string, object>> ScopedAgent37Metadata(string type, JsonElement raw)
        => new(StringComparer.OrdinalIgnoreCase) { ["agent37"] = new Dictionary<string, object> { ["event_type"] = type, ["raw"] = raw.Clone() } };
    private static Dictionary<string, object> FlattenAgent37Metadata(string type, JsonElement raw)
        => new(StringComparer.OrdinalIgnoreCase) { ["agent37"] = new Dictionary<string, object> { ["event_type"] = type, ["raw"] = raw.Clone() } };

    private static AIUsage? CreateAgent37Usage(JsonElement? usage)
    {
        if (!usage.HasValue || usage.Value.ValueKind != JsonValueKind.Object) return null;
        var input = GetInt(usage, "input_tokens"); var output = GetInt(usage, "output_tokens");
        return new AIUsage
        {
            InputTokens = input,
            OutputTokens = output,
            TotalTokens = AddTokens(input, output),
            AdditionalProperties = new Dictionary<string, JsonElement> { ["agent37"] = usage.Value.Clone() }
        };
    }

    private static AIFinishGatewayMetadata? CreateAgent37GatewayMetadata(JsonElement? usage)
    {
        if (!usage.HasValue || !TryGetProperty(usage.Value, "cost_usd", out var cost) || cost.ValueKind != JsonValueKind.Number) return null;
        return new AIFinishGatewayMetadata { Cost = cost.TryGetDecimal(out var value) ? value : null };
    }

    private static string ExtractAgent37Error(JsonElement data)
    {
        var error = GetObject(data, "error");
        return GetString(error, "message") ?? GetString(data, "message") ?? "Agent37 response failed.";
    }

    private static string FormatAgent37ModelId(Agent37Route route)
        => route.Agent is null ? $"agent37/{route.InstanceId}" : $"agent37/{route.InstanceId}/{route.Agent}/{route.Model}";
    private static string BuildAgent37SessionToolCallId(string sessionId) => $"agent37-create-session-{sessionId}";
    private static string NormalizeAgent37Name(string value)
        => new string(value.Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_').ToArray()).Trim('_');
    private static string SanitizeAgent37Filename(string value)
    {
        var name = Path.GetFileName(value);
        var invalid = Path.GetInvalidFileNameChars();
        name = new string(name.Select(ch => invalid.Contains(ch) || ch is '/' or '\\' ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(name) ? "attachment.bin" : name;
    }
    private static int? AddTokens(int? input, int? output) => input.HasValue || output.HasValue ? (input ?? 0) + (output ?? 0) : null;
    private static string? GetDictionaryString(Dictionary<string, object?>? values, string name)
        => values is not null && values.TryGetValue(name, out var value) ? value?.ToString() : null;
    private static string? GetString(Dictionary<string, object?>? values, string name)
        => values is not null && values.TryGetValue(name, out var value) ? value switch
        { JsonElement json when json.ValueKind == JsonValueKind.String => json.GetString(), _ => value?.ToString() } : null;
    private static string? GetString(JsonElement? element, string name)
        => element.HasValue ? GetString(element.Value, name) : null;
    private static string? GetString(JsonElement element, string name)
        => TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static JsonElement? GetObject(JsonElement element, string name)
        => TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.Object ? value.Clone() : null;
    private static JsonElement? GetObject(JsonElement? element, string name)
        => element.HasValue ? GetObject(element.Value, name) : null;
    private static int? GetInt(JsonElement? element, string name)
        => element.HasValue && TryGetProperty(element.Value, name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var result) ? result : null;
    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        return false;
    }
}
