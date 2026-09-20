using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Softref;

public partial class SoftrefProvider
{
    private const string CreateSoftrefSessionToolName = "create_softref_session";
    private static readonly JsonSerializerOptions SoftrefJson = JsonSerializerOptions.Web;
    private static readonly HashSet<string> SoftrefLocalOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "session_id", "sessionId", "id"
    };

    private sealed record SoftrefExecution(string SessionId, bool Created, JsonElement Session);
    private sealed record SoftrefSseFrame(string Event, JsonElement Data);

    public async Task<AIResponse> ExecuteUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var content = new List<AIContentPart>();
        var text = new StringBuilder();
        var reasoning = new StringBuilder();
        var tools = new Dictionary<string, AIToolCallContentPart>(StringComparer.Ordinal);
        Dictionary<string, object?>? metadata = null;
        object? usage = null;
        var status = "completed";

        await foreach (var streamEvent in StreamUnifiedAsync(request, cancellationToken))
        {
            metadata = streamEvent.Metadata ?? metadata;
            switch (streamEvent.Event.Type)
            {
                case "text-delta" when streamEvent.Event.Data is AITextDeltaEventData delta:
                    text.Append(delta.Delta);
                    break;
                case "reasoning-delta" when streamEvent.Event.Data is AIReasoningDeltaEventData delta:
                    reasoning.Append(delta.Delta);
                    break;
                case "tool-input-available" when streamEvent.Event.Data is AIToolInputAvailableEventData input:
                    tools[streamEvent.Event.Id ?? Guid.NewGuid().ToString("N")] = new AIToolCallContentPart
                    {
                        ToolCallId = streamEvent.Event.Id ?? Guid.NewGuid().ToString("N"),
                        ToolName = input.ToolName,
                        Type = "tool-call",
                        Title = input.Title,
                        Input = input.Input,
                        ProviderExecuted = input.ProviderExecuted,
                        State = "input-available"
                    };
                    break;
                case "tool-output-available" when streamEvent.Event.Data is AIToolOutputAvailableEventData output:
                    {
                        var id = streamEvent.Event.Id ?? Guid.NewGuid().ToString("N");
                        tools.TryGetValue(id, out var existing);
                        tools[id] = new AIToolCallContentPart
                        {
                            ToolCallId = id,
                            Type = "tool-call",
                            ToolName = output.ToolName ?? existing?.ToolName,
                            Title = existing?.Title,
                            Input = existing?.Input,
                            Output = output.Output,
                            ProviderExecuted = output.ProviderExecuted ?? existing?.ProviderExecuted,
                            State = "output-available"
                        };
                        break;
                    }
                case "tool-output-error" when streamEvent.Event.Data is AIToolOutputErrorEventData error:
                    {
                        var id = error.ToolCallId ?? streamEvent.Event.Id ?? Guid.NewGuid().ToString("N");
                        tools.TryGetValue(id, out var existing);
                        tools[id] = new AIToolCallContentPart
                        {
                            ToolCallId = id,
                            Type = "tool-call",
                            ToolName = existing?.ToolName,
                            Title = existing?.Title,
                            Input = existing?.Input,
                            Output = new CallToolResult
                            {
                                IsError = true,
                                StructuredContent = JsonSerializer.SerializeToElement(new { error = error.ErrorText }, SoftrefJson)
                            },
                            ProviderExecuted = error.ProviderExecuted ?? existing?.ProviderExecuted,
                            State = "output-error"
                        };
                        break;
                    }
                case "finish" when streamEvent.Event.Data is AIFinishEventData finish:
                    usage = finish.MessageMetadata?.Usage.Clone();
                    break;
                case "error":
                    status = "failed";
                    break;
            }
        }

        content.AddRange(tools.Values);
        if (reasoning.Length > 0)
            content.Add(new AIReasoningContentPart
            {
                Type = "reasoning",
                Text = reasoning.ToString()
            });
        if (text.Length > 0 || content.Count == 0)
            content.Add(new AITextContentPart { Type = "text", Text = text.ToString() });

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = NormalizeSoftrefModel(request.Model),
            Status = status,
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
                        Content = content,
                        Metadata = metadata
                    }
                ],
                Metadata = metadata
            }
        };
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ApplyAuthHeader();

        var execution = await ResolveSoftrefSessionAsync(request, cancellationToken);
        var metadata = CreateSoftrefMetadata(execution.SessionId, execution.Created, execution.Session);
        var now = DateTimeOffset.UtcNow;

        if (execution.Created)
        {
            foreach (var evt in CreateSoftrefSessionToolEvents(execution, now, metadata))
                yield return evt;
        }

        await PostSoftrefJsonAsync(
            $"v1/sessions/{Uri.EscapeDataString(execution.SessionId)}/messages",
            BuildSoftrefMessagePayload(request),
            "send message",
            cancellationToken);

        await PostSoftrefJsonAsync(
            $"v1/sessions/{Uri.EscapeDataString(execution.SessionId)}/run",
            new { },
            "run session",
            cancellationToken,
            allowEmptyResponse: true);

        using var streamRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"v1/sessions/{Uri.EscapeDataString(execution.SessionId)}/stream");
        streamRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var streamResponse = await _client.SendAsync(
            streamRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSoftrefSuccessAsync(streamResponse, "stream session", cancellationToken);

        await using var stream = await streamResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var textId = $"softref-text-{execution.SessionId}";
        var reasoningId = $"softref-reasoning-{execution.SessionId}";
        var textStarted = false;
        var reasoningStarted = false;
        var activeTools = new Dictionary<string, Queue<string>>(StringComparer.OrdinalIgnoreCase);

        await foreach (var frame in ReadSoftrefSseAsync(reader, cancellationToken))
        {
            now = DateTimeOffset.UtcNow;
            var eventMetadata = CreateSoftrefEventMetadata(execution.SessionId, frame.Event, frame.Data);
            var scopedMetadata = CreateSoftrefScopedMetadata(frame.Event, frame.Data, execution.SessionId);

            switch (frame.Event)
            {
                case "content_delta":
                    if (!textStarted)
                    {
                        textStarted = true;
                        yield return CreateSoftrefEvent("text-start", textId,
                            new AITextStartEventData { ProviderMetadata = FlattenSoftrefMetadata(frame.Event, frame.Data, execution.SessionId) },
                            now, eventMetadata);
                    }
                    yield return CreateSoftrefEvent("text-delta", textId,
                        new AITextDeltaEventData
                        {
                            Delta = GetSoftrefString(frame.Data, "text", "delta", "content") ?? string.Empty,
                            ProviderMetadata = FlattenSoftrefMetadata(frame.Event, frame.Data, execution.SessionId)
                        }, now, eventMetadata);
                    break;

                case "thinking_delta":
                case "reasoning_delta":
                    if (!reasoningStarted)
                    {
                        reasoningStarted = true;
                        yield return CreateSoftrefEvent("reasoning-start", reasoningId,
                            new AIReasoningStartEventData { ProviderMetadata = scopedMetadata }, now, eventMetadata);
                    }
                    yield return CreateSoftrefEvent("reasoning-delta", reasoningId,
                        new AIReasoningDeltaEventData
                        {
                            Delta = GetSoftrefString(frame.Data, "text", "delta", "thinking") ?? string.Empty,
                            ProviderMetadata = scopedMetadata
                        }, now, eventMetadata);
                    break;

                case "tool_started":
                    {
                        var name = GetSoftrefString(frame.Data, "name", "tool", "tool_name") ?? "softref_tool";
                        var callId = GetSoftrefString(frame.Data, "id", "tool_call_id", "toolCallId")
                                     ?? $"softref-{NormalizeSoftrefName(name)}-{Guid.NewGuid():N}";
                        if (!activeTools.TryGetValue(name, out var queue))
                            activeTools[name] = queue = new Queue<string>();
                        queue.Enqueue(callId);
                        yield return CreateSoftrefEvent("tool-input-available", callId,
                            new AIToolInputAvailableEventData
                            {
                                ToolName = NormalizeSoftrefName(name),
                                Title = name,
                                Input = GetSoftrefValue(frame.Data, "input", "arguments") ?? frame.Data.Clone(),
                                ProviderExecuted = true,
                                ProviderMetadata = scopedMetadata
                            }, now, eventMetadata);
                        break;
                    }

                case "tool_completed":
                case "tool_failed":
                    {
                        var name = GetSoftrefString(frame.Data, "name", "tool", "tool_name") ?? "softref_tool";
                        var callId = GetSoftrefString(frame.Data, "id", "tool_call_id", "toolCallId");
                        if (string.IsNullOrWhiteSpace(callId)
                            && activeTools.TryGetValue(name, out var queue)
                            && queue.Count > 0)
                            callId = queue.Dequeue();
                        callId ??= $"softref-{NormalizeSoftrefName(name)}-{Guid.NewGuid():N}";

                        if (frame.Event == "tool_failed")
                        {
                            yield return CreateSoftrefEvent("tool-output-error", callId,
                                new AIToolOutputErrorEventData
                                {
                                    ToolCallId = callId,
                                    ErrorText = GetSoftrefString(frame.Data, "error", "message") ?? $"Softref tool '{name}' failed.",
                                    ProviderExecuted = true,
                                    Dynamic = true,
                                    ProviderMetadata = scopedMetadata
                                }, now, eventMetadata);
                        }
                        else
                        {
                            yield return CreateSoftrefEvent("tool-output-available", callId,
                                new AIToolOutputAvailableEventData
                                {
                                    ToolName = NormalizeSoftrefName(name),
                                    Output = new CallToolResult
                                    {
                                        Content = [],
                                        StructuredContent = (GetSoftrefValue(frame.Data, "output", "result") as JsonElement?)?.Clone()
                                                            ?? frame.Data.Clone()
                                    },
                                    ProviderExecuted = true,
                                    Dynamic = true,
                                    ProviderMetadata = scopedMetadata
                                }, now, eventMetadata);
                        }
                        break;
                    }

                case "session_completed":
                case "session_idle":
                    if (reasoningStarted)
                        yield return CreateSoftrefEvent("reasoning-end", reasoningId,
                            new AIReasoningEndEventData { ProviderMetadata = scopedMetadata }, now, eventMetadata);
                    if (textStarted)
                        yield return CreateSoftrefEvent("text-end", textId,
                            new AITextEndEventData { ProviderMetadata = FlattenSoftrefMetadata(frame.Event, frame.Data, execution.SessionId) },
                            now, eventMetadata);
                    yield return CreateSoftrefFinishEvent(request, execution.SessionId, frame.Data, now, metadata);
                    yield break;

                case "error":
                case "session_error":
                case "session_failed":
                    if (reasoningStarted)
                        yield return CreateSoftrefEvent("reasoning-end", reasoningId,
                            new AIReasoningEndEventData { ProviderMetadata = scopedMetadata }, now, eventMetadata);
                    if (textStarted)
                        yield return CreateSoftrefEvent("text-end", textId,
                            new AITextEndEventData { ProviderMetadata = FlattenSoftrefMetadata(frame.Event, frame.Data, execution.SessionId) },
                            now, eventMetadata);
                    yield return CreateSoftrefEvent("error", execution.SessionId,
                        new AIErrorEventData
                        {
                            ErrorText = GetSoftrefString(frame.Data, "error", "message", "detail")
                                        ?? $"Softref session failed: {frame.Data.GetRawText()}"
                        }, now, eventMetadata);
                    yield break;

                default:
                    yield return CreateSoftrefEvent($"data-softref.{NormalizeSoftrefName(frame.Event)}", execution.SessionId,
                        new AIDataEventData
                        {
                            Id = execution.SessionId,
                            Data = frame.Data.Clone(),
                            Transient = true
                        }, now, eventMetadata);
                    break;
            }
        }

        if (reasoningStarted)
            yield return CreateSoftrefEvent("reasoning-end", reasoningId, new AIReasoningEndEventData(), now, metadata);
        if (textStarted)
            yield return CreateSoftrefEvent("text-end", textId, new AITextEndEventData(), now, metadata);
        yield return CreateSoftrefFinishEvent(request, execution.SessionId,
            JsonSerializer.SerializeToElement(new { status = "stream_closed" }, SoftrefJson), now, metadata);
    }

    private async Task<SoftrefExecution> ResolveSoftrefSessionAsync(AIRequest request, CancellationToken cancellationToken)
    {
        if (TryFindSoftrefSessionId(request, out var sessionId))
            return new SoftrefExecution(sessionId, false,
                JsonSerializer.SerializeToElement(new { id = sessionId, recovered = true }, SoftrefJson));

        var session = await PostSoftrefJsonAsync(
            "v1/sessions",
            BuildSoftrefSessionPayload(request),
            "create session",
            cancellationToken);
        sessionId = GetSoftrefString(session, "id", "session_id", "sessionId")
                    ?? throw new InvalidOperationException("Softref create session response did not include an id.");
        return new SoftrefExecution(sessionId, true, session);
    }

    private Dictionary<string, object?> BuildSoftrefSessionPayload(AIRequest request)
    {
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["model"] = NormalizeSoftrefModel(request.Model)
        };
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            payload["system"] = request.Instructions;

        var options = GetSoftrefOptions(request);
        if (options.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in options.EnumerateObject())
            {
                if (SoftrefLocalOptions.Contains(property.Name)
                    || property.NameEquals("model")
                    || property.NameEquals("system"))
                    continue;
                payload[property.Name] = property.Value.Clone();
            }
        }
        return payload;
    }

    private static Dictionary<string, object?> BuildSoftrefMessagePayload(AIRequest request)
        => new()
        {
            ["role"] = "user",
            ["content"] = ExtractLatestSoftrefUserText(request)
        };

    private static string ExtractLatestSoftrefUserText(AIRequest request)
    {
        foreach (var item in (request.Input?.Items ?? []).AsEnumerable().Reverse())
        {
            if (!string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))
                continue;
            if (item.Content?.Any(part => part is AIFileContentPart) == true)
                throw new NotSupportedException("Softref unified sessions currently support text input only.");
            var text = string.Join("\n", item.Content?.OfType<AITextContentPart>()
                .Select(part => part.Text).Where(value => !string.IsNullOrWhiteSpace(value)) ?? []);
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        if (!string.IsNullOrWhiteSpace(request.Input?.Text))
            return request.Input.Text;
        throw new InvalidOperationException("Softref requires a non-empty latest user message.");
    }

    private static string NormalizeSoftrefModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Softref requires a model.", nameof(model));
        var normalized = model.Trim().Trim('/');
        return normalized.StartsWith("softref/", StringComparison.OrdinalIgnoreCase)
            ? normalized[8..]
            : normalized;
    }

    private bool TryFindSoftrefSessionId(AIRequest request, out string sessionId)
    {
        var options = GetSoftrefOptions(request);
        sessionId = GetSoftrefString(options, "session_id", "sessionId", "id")
                    ?? GetDictionaryString(request.Input?.Metadata, "session_id", "sessionId")
                    ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(sessionId))
            return true;

        foreach (var item in request.Input?.Items ?? [])
        {
            if (TryExtractSoftrefSessionId(item.Metadata, out sessionId))
                return true;
            foreach (var tool in item.Content?.OfType<AIToolCallContentPart>() ?? [])
            {
                if (tool.ProviderExecuted != true)
                    continue;
                if (TryExtractSoftrefSessionId(tool.Output, out sessionId)
                    || TryExtractSoftrefSessionId(tool.Metadata, out sessionId)
                    || (string.Equals(tool.ToolName, CreateSoftrefSessionToolName, StringComparison.OrdinalIgnoreCase)
                        && TryExtractSoftrefSessionId(tool.Input, out sessionId)))
                    return true;
            }
        }
        sessionId = string.Empty;
        return false;
    }

    private static bool TryExtractSoftrefSessionId(object? value, out string sessionId)
    {
        sessionId = string.Empty;
        if (value is null)
            return false;
        JsonElement element;
        try
        {
            element = value is JsonElement json ? json : JsonSerializer.SerializeToElement(value, SoftrefJson);
        }
        catch
        {
            return false;
        }
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var nestedName in new[] { "structuredContent", "output", "softref", "session", "metadata" })
        {
            if (TryGetSoftrefProperty(element, nestedName, out var nested)
                && TryExtractSoftrefSessionId(nested, out sessionId))
                return true;
        }
        sessionId = GetSoftrefString(element, "session_id", "sessionId") ?? string.Empty;
        return !string.IsNullOrWhiteSpace(sessionId);
    }

    private static string? GetDictionaryString(Dictionary<string, object?>? values, params string[] names)
    {
        if (values is null)
            return null;
        foreach (var name in names)
            if (values.TryGetValue(name, out var value) && value is not null)
                return value.ToString();
        return null;
    }

    private static JsonElement GetSoftrefOptions(AIRequest request)
    {
        if (request.Metadata is null || !request.Metadata.TryGetValue("softref", out var value) || value is null)
            return default;
        try
        {
            return value is JsonElement json ? json : JsonSerializer.SerializeToElement(value, SoftrefJson);
        }
        catch
        {
            return default;
        }
    }

    private async Task<JsonElement> PostSoftrefJsonAsync(
        string path,
        object payload,
        string operation,
        CancellationToken cancellationToken,
        bool allowEmptyResponse = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, SoftrefJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSoftrefSuccessAsync(response, operation, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(raw))
        {
            if (allowEmptyResponse)
                return JsonSerializer.SerializeToElement(new { }, SoftrefJson);
            throw new InvalidOperationException($"Softref {operation} returned an empty response.");
        }
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(raw, SoftrefJson).Clone();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Softref {operation} returned invalid JSON: {raw}", ex);
        }
    }

    private static async Task EnsureSoftrefSuccessAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(
            $"Softref {operation} failed with status {(int)response.StatusCode}: {raw}",
            null,
            response.StatusCode);
    }

    private static async IAsyncEnumerable<SoftrefSseFrame> ReadSoftrefSseAsync(
        StreamReader reader,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? eventName = null;
        var data = new StringBuilder();
        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                break;
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    var raw = data.ToString();
                    JsonElement json;
                    try
                    {
                        json = JsonSerializer.Deserialize<JsonElement>(raw, SoftrefJson).Clone();
                    }
                    catch (JsonException)
                    {
                        json = JsonSerializer.SerializeToElement(new { text = raw }, SoftrefJson);
                    }
                    yield return new SoftrefSseFrame(eventName ?? "message", json);
                }
                eventName = null;
                data.Clear();
                continue;
            }
            if (line.StartsWith(':'))
                continue;
            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
                eventName = line[6..].Trim();
            else if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                if (data.Length > 0)
                    data.Append('\n');
                data.Append(line[5..].TrimStart());
            }
        }

        if (data.Length > 0)
        {
            var raw = data.ToString();
            JsonElement json;
            try
            {
                json = JsonSerializer.Deserialize<JsonElement>(raw, SoftrefJson).Clone();
            }
            catch (JsonException)
            {
                json = JsonSerializer.SerializeToElement(new { text = raw }, SoftrefJson);
            }
            yield return new SoftrefSseFrame(eventName ?? "message", json);
        }
    }

    private IEnumerable<AIStreamEvent> CreateSoftrefSessionToolEvents(
        SoftrefExecution execution,
        DateTimeOffset timestamp,
        Dictionary<string, object?> metadata)
    {
        var callId = $"softref-session-{execution.SessionId}";
        var providerMetadata = CreateSoftrefScopedMetadata(CreateSoftrefSessionToolName, execution.Session, execution.SessionId);
        yield return CreateSoftrefEvent("tool-input-available", callId,
            new AIToolInputAvailableEventData
            {
                ToolName = CreateSoftrefSessionToolName,
                Title = "Create Softref session",
                Input = new { },
                ProviderExecuted = true,
                ProviderMetadata = providerMetadata
            }, timestamp, metadata);
        yield return CreateSoftrefEvent("tool-output-available", callId,
            new AIToolOutputAvailableEventData
            {
                ToolName = CreateSoftrefSessionToolName,
                Output = CreateSoftrefSessionResult(execution),
                ProviderExecuted = true,
                ProviderMetadata = providerMetadata
            }, timestamp, metadata);
    }

    private static CallToolResult CreateSoftrefSessionResult(SoftrefExecution execution)
        => new()
        {
            Content = [],
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                sessionId = execution.SessionId,
                session_id = execution.SessionId,
                session = execution.Session.Clone()
            }, SoftrefJson)
        };

    private AIStreamEvent CreateSoftrefFinishEvent(
        AIRequest request,
        string sessionId,
        JsonElement data,
        DateTimeOffset timestamp,
        Dictionary<string, object?> metadata)
    {
        var usage = TryGetSoftrefProperty(data, "usage", out var usageValue)
            ? usageValue.Clone()
            : JsonSerializer.SerializeToElement(new { }, SoftrefJson);
        var inputTokens = GetSoftrefInt(usage, "input_tokens", "inputTokens");
        var outputTokens = GetSoftrefInt(usage, "output_tokens", "outputTokens");
        var totalTokens = GetSoftrefInt(usage, "total_tokens", "totalTokens")
                          ?? (inputTokens.HasValue || outputTokens.HasValue ? (inputTokens ?? 0) + (outputTokens ?? 0) : null);
        var model = NormalizeSoftrefModel(request.Model);
        return CreateSoftrefEvent("finish", sessionId,
            new AIFinishEventData
            {
                FinishReason = "stop",
                Model = model,
                CompletedAt = timestamp.ToUnixTimeSeconds(),
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                TotalTokens = totalTokens,
                Response = data.Clone(),
                MessageMetadata = AIFinishMessageMetadata.Create(
                    model,
                    timestamp,
                    usage,
                    inputTokens: inputTokens,
                    outputTokens: outputTokens,
                    totalTokens: totalTokens,
                    temperature: request.Temperature,
                    additionalProperties: new Dictionary<string, object?>
                    {
                        [GetIdentifier()] = new { session_id = sessionId, raw = data.Clone() }
                    })
            }, timestamp, metadata);
    }

    private AIStreamEvent CreateSoftrefEvent(
        string type,
        string? id,
        object data,
        DateTimeOffset timestamp,
        Dictionary<string, object?>? metadata)
        => new()
        {
            ProviderId = GetIdentifier(),
            Event = new AIEventEnvelope
            {
                Type = type,
                Id = id,
                Timestamp = timestamp,
                Data = data,
                Metadata = metadata
            },
            Metadata = metadata
        };

    private static Dictionary<string, object?> CreateSoftrefMetadata(
        string sessionId,
        bool created,
        JsonElement raw)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["softref"] = new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId,
                ["session_id"] = sessionId,
                ["created"] = created,
                ["raw"] = raw.Clone()
            }
        };

    private static Dictionary<string, object?> CreateSoftrefEventMetadata(
        string sessionId,
        string eventName,
        JsonElement raw)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["softref"] = new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId,
                ["session_id"] = sessionId,
                ["event"] = eventName,
                ["raw"] = raw.Clone()
            }
        };

    private static Dictionary<string, Dictionary<string, object>> CreateSoftrefScopedMetadata(
        string eventName,
        JsonElement raw,
        string sessionId)
        => new()
        {
            ["softref"] = new Dictionary<string, object>
            {
                ["sessionId"] = sessionId,
                ["session_id"] = sessionId,
                ["event"] = eventName,
                ["raw"] = raw.Clone()
            }
        };

    private static Dictionary<string, object> FlattenSoftrefMetadata(
        string eventName,
        JsonElement raw,
        string sessionId)
        => new()
        {
            ["softref.sessionId"] = sessionId,
            ["softref.session_id"] = sessionId,
            ["softref.event"] = eventName,
            ["softref.raw"] = raw.Clone()
        };

    private static string NormalizeSoftrefName(string value)
    {
        var chars = value.Select(character => char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '_').ToArray();
        return new string(chars).Trim('_');
    }

    private static object? GetSoftrefValue(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (TryGetSoftrefProperty(element, name, out var value))
                return value.Clone();
        return null;
    }

    private static string? GetSoftrefString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetSoftrefProperty(element, name, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.String)
                return value.GetString();
            if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                return value.ToString();
        }
        return null;
    }

    private static int? GetSoftrefInt(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetSoftrefProperty(element, name, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                return number;
            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number))
                return number;
        }
        return null;
    }

    private static bool TryGetSoftrefProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }
}
