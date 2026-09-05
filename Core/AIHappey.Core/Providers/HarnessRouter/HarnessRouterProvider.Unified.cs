using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.HarnessRouter;

public partial class HarnessRouterProvider
{
    private const string SessionToolName = "create_harnessrouter_session";
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    private sealed record HarnessRoute(string HarnessId, string? BackendModel, string UnifiedModel);
    private sealed record ContinuationState(string SessionId, string ResponseId);

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ApplyAuthHeader();

        var route = ResolveRoute(request.Model);
        var continuation = FindContinuation(request);
        var payload = BuildTaskPayload(request, route, continuation, stream: false);
        using var httpRequest = CreateTaskRequest(route, payload);
        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"HarnessRouter task error ({(int)response.StatusCode}): {raw}",
                null,
                response.StatusCode);

        using var document = JsonDocument.Parse(raw);
        var result = document.RootElement.Clone();
        var responseId = GetString(result, "id")
                         ?? throw new InvalidOperationException("HarnessRouter response did not include an id.");
        var sessionId = GetNestedString(result, "metadata", "session_id")
                        ?? GetNestedString(result, "metadata", "sessionId")
                        ?? throw new InvalidOperationException("HarnessRouter response did not include metadata.session_id.");

        var content = new List<AIContentPart>();
        if (continuation is null)
            content.Add(CreateSessionToolPart(route, responseId, sessionId, result));

        var text = ExtractResponseText(result);
        if (!string.IsNullOrWhiteSpace(text))
        {
            content.Add(new AITextContentPart
            {
                Type = "text",
                Text = text,
                Metadata = new Dictionary<string, object?> { ["harnessrouter.raw"] = result }
            });
        }

        content.AddRange(CreateActivityParts(result));

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = route.UnifiedModel,
            Status = GetString(result, "status") ?? "completed",
            Output = new AIOutput
            {
                Items = content.Count == 0
                    ? []
                    : [new AIOutputItem { Role = "assistant", Content = content }],
                Metadata = CreateResponseMetadata(route, responseId, sessionId, result)
            },
            Usage = TryGetProperty(result, "usage", out var usage) ? usage.Clone() : null,
            Metadata = CreateResponseMetadata(route, responseId, sessionId, result)
        };
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ApplyAuthHeader();

        var route = ResolveRoute(request.Model);
        var continuation = FindContinuation(request);
        var payload = BuildTaskPayload(request, route, continuation, stream: true);
        using var httpRequest = CreateTaskRequest(route, payload);
        using var response = await _client.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"HarnessRouter streaming task error ({(int)response.StatusCode}): {error}",
                null,
                response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        string? responseId = continuation?.ResponseId;
        string? sessionId = continuation?.SessionId;
        var textStarted = false;
        var terminalSeen = false;
        JsonElement? terminalResponse = null;

        await foreach (var data in ReadSseDataAsync(reader, cancellationToken))
        {
            if (!TryParseObject(data, out var frame))
                continue;

            var type = GetString(frame, "type");
            var frameResponse = TryGetProperty(frame, "response", out var nestedResponse)
                ? nestedResponse
                : frame;

            responseId = GetString(frameResponse, "id") ?? responseId;
            sessionId = GetNestedString(frameResponse, "metadata", "session_id")
                        ?? GetNestedString(frameResponse, "metadata", "sessionId")
                        ?? sessionId;

            var metadata = CreateResponseMetadata(route, responseId, sessionId, frame);
            switch (type)
            {
                case "response.created":
                    if (continuation is null
                        && !string.IsNullOrWhiteSpace(responseId)
                        && !string.IsNullOrWhiteSpace(sessionId))
                    {
                        foreach (var evt in CreateSessionToolEvents(route, responseId, sessionId, frame))
                            yield return evt;
                    }
                    break;

                case "response.output_text.delta":
                    var delta = GetString(frame, "delta") ?? GetString(frame, "text");
                    if (string.IsNullOrEmpty(delta))
                        break;
                    if (!textStarted)
                    {
                        textStarted = true;
                        yield return CreateStreamEvent(
                            "text-start",
                            responseId,
                            new AITextStartEventData { ProviderMetadata = CreateLooseMetadata(frame) },
                            metadata);
                    }
                    yield return CreateStreamEvent(
                        "text-delta",
                        responseId,
                        new AITextDeltaEventData { Delta = delta, ProviderMetadata = CreateLooseMetadata(frame) },
                        metadata);
                    break;

                case "response.output_item.added":
                    if (TryGetProperty(frame, "item", out var item))
                    {
                        foreach (var evt in CreateActivityEvents(item, metadata))
                            yield return evt;
                    }
                    break;

                case "response.completed":
                case "response.incomplete":
                case "response.failed":
                    terminalSeen = true;
                    terminalResponse = frameResponse.Clone();
                    if (textStarted)
                    {
                        yield return CreateStreamEvent(
                            "text-end",
                            responseId,
                            new AITextEndEventData { ProviderMetadata = CreateLooseMetadata(frame) },
                            metadata);
                        textStarted = false;
                    }

                    if (type == "response.failed")
                    {
                        yield return CreateStreamEvent(
                            "error",
                            responseId,
                            new AIErrorEventData { ErrorText = ExtractErrorText(frameResponse) },
                            metadata);
                    }

                    yield return CreateFinishEvent(
                        route,
                        responseId,
                        type == "response.completed" ? "stop" : type == "response.incomplete" ? "length" : "error",
                        frameResponse,
                        metadata);
                    yield break;
            }
        }

        if (!terminalSeen)
        {
            if (textStarted)
                yield return CreateStreamEvent("text-end", responseId, new AITextEndEventData(), null);

            yield return CreateFinishEvent(
                route,
                responseId,
                "error",
                terminalResponse,
                CreateResponseMetadata(route, responseId, sessionId, terminalResponse));
        }
    }

    private HttpRequestMessage CreateTaskRequest(HarnessRoute route, Dictionary<string, object?> payload)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{Uri.EscapeDataString(route.HarnessId)}/v1/responses")
        {
            Content = JsonContent.Create(payload, options: Json)
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString());
        return request;
    }

    private static Dictionary<string, object?> BuildTaskPayload(
        AIRequest request,
        HarnessRoute route,
        ContinuationState? continuation,
        bool stream)
    {
        var input = ExtractLatestUserText(request)
                    ?? request.Input?.Text
                    ?? request.Instructions
                    ?? throw new InvalidOperationException("HarnessRouter requires a user task.");

        var payload = new Dictionary<string, object?>
        {
            ["input"] = input,
            ["stream"] = stream
        };

        if (!string.IsNullOrWhiteSpace(route.BackendModel))
            payload["model"] = route.BackendModel;

        if (continuation is not null)
        {
            payload["previous_response_id"] = continuation.ResponseId;
            payload["metadata"] = new Dictionary<string, object?>
            {
                ["session_id"] = continuation.SessionId
            };
        }

        return payload;
    }

    private HarnessRoute ResolveRoute(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("HarnessRouter requires a model id containing a harness id.");

        var normalized = model.Trim();
        var providerPrefix = GetIdentifier() + "/";
        if (normalized.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            normalized = normalized[providerPrefix.Length..];

        var separator = normalized.IndexOf('/');
        var harness = separator < 0 ? normalized : normalized[..separator];
        var backendModel = separator < 0 ? null : normalized[(separator + 1)..];
        if (string.IsNullOrWhiteSpace(harness))
            throw new InvalidOperationException("HarnessRouter model id did not contain a harness id.");

        return new HarnessRoute(harness, string.IsNullOrWhiteSpace(backendModel) ? null : backendModel, $"{GetIdentifier()}/{normalized}");
    }

    private ContinuationState? FindContinuation(AIRequest request)
    {
        if (TryExtractContinuation(request.Metadata, out var direct))
            return direct;
        if (TryExtractContinuation(request.Input?.Metadata, out direct))
            return direct;

        foreach (var item in (request.Input?.Items ?? []).AsEnumerable().Reverse())
        {
            foreach (var tool in (item.Content ?? []).OfType<AIToolCallContentPart>().Reverse())
            {
                if (tool.ProviderExecuted != true)
                    continue;
                if (TryExtractContinuation(tool.Output, out var state)
                    || TryExtractContinuation(tool.Metadata, out state)
                    || TryExtractContinuation(tool.Input, out state))
                    return state;
            }
        }

        return null;
    }

    private bool TryExtractContinuation(object? value, out ContinuationState state)
    {
        state = default!;
        if (value is null)
            return false;

        JsonElement element;
        try
        {
            element = value is JsonElement json ? json : JsonSerializer.SerializeToElement(value, Json);
        }
        catch
        {
            return false;
        }

        if (element.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var property in new[] { "structuredContent", "output", GetIdentifier(), "session", "response", "metadata" })
        {
            if (TryGetProperty(element, property, out var nested)
                && TryExtractContinuation(nested, out state))
                return true;
        }

        var sessionId = GetString(element, "session_id") ?? GetString(element, "sessionId");
        var responseId = GetString(element, "response_id") ?? GetString(element, "responseId")
                         ?? GetString(element, "previous_response_id");
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(responseId))
            return false;

        state = new ContinuationState(sessionId, responseId);
        return true;
    }

    private AIToolCallContentPart CreateSessionToolPart(
        HarnessRoute route,
        string responseId,
        string sessionId,
        JsonElement raw)
        => new()
        {
            Type = "tool-call",
            ToolCallId = BuildSessionToolCallId(sessionId),
            ToolName = SessionToolName,
            Title = "Create HarnessRouter session",
            Input = new { harness_id = route.HarnessId, model = route.BackendModel },
            Output = CreateSessionToolOutput(route, responseId, sessionId, raw),
            ProviderExecuted = true,
            State = "output-available",
            Metadata = CreateSessionValues(route, responseId, sessionId, raw)
        };

    private IEnumerable<AIStreamEvent> CreateSessionToolEvents(
        HarnessRoute route,
        string responseId,
        string sessionId,
        JsonElement raw)
    {
        var id = BuildSessionToolCallId(sessionId);
        var providerMetadata = CreateScopedMetadata(CreateSessionValues(route, responseId, sessionId, raw));
        yield return CreateStreamEvent(
            "tool-input-available",
            id,
            new AIToolInputAvailableEventData
            {
                ToolName = SessionToolName,
                Title = "Create HarnessRouter session",
                Input = new { harness_id = route.HarnessId, model = route.BackendModel },
                ProviderExecuted = true,
                ProviderMetadata = providerMetadata
            },
            null);
        yield return CreateStreamEvent(
            "tool-output-available",
            id,
            new AIToolOutputAvailableEventData
            {
                ToolName = SessionToolName,
                Output = CreateSessionToolOutput(route, responseId, sessionId, raw),
                ProviderExecuted = true,
                ProviderMetadata = providerMetadata
            },
            null);
    }

    private static object CreateSessionToolOutput(
        HarnessRoute route,
        string responseId,
        string sessionId,
        JsonElement raw)
        => new
        {
            content = Array.Empty<object>(),
            structuredContent = new
            {
                type = SessionToolName,
                session_id = sessionId,
                sessionId,
                response_id = responseId,
                responseId,
                harness_id = route.HarnessId,
                model = route.BackendModel,
                response = raw
            }
        };

    private static Dictionary<string, object?> CreateSessionValues(
        HarnessRoute route,
        string responseId,
        string sessionId,
        JsonElement raw)
        => new()
        {
            ["type"] = SessionToolName,
            ["tool_name"] = SessionToolName,
            ["session_id"] = sessionId,
            ["sessionId"] = sessionId,
            ["response_id"] = responseId,
            ["responseId"] = responseId,
            ["harness_id"] = route.HarnessId,
            ["model"] = route.BackendModel,
            ["raw"] = raw
        };

    private IEnumerable<AIContentPart> CreateActivityParts(JsonElement response)
    {
        if (!TryGetProperty(response, "output", out var output) || output.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var item in output.EnumerateArray())
        {
            var type = GetString(item, "type");
            if (type is "message" or "output_text")
                continue;

            var id = GetString(item, "id") ?? $"harnessrouter-activity-{Guid.NewGuid():N}";
            yield return new AIToolCallContentPart
            {
                ToolCallId = id,
                Type = "tool-call",
                ToolName = GetString(item, "name") ?? type ?? "harness_activity",
                Title = GetString(item, "title") ?? type,
                Input = TryGetProperty(item, "arguments", out var arguments) ? arguments.Clone() : item.Clone(),
                Output = item.Clone(),
                ProviderExecuted = true,
                State = "output-available",
                Metadata = new Dictionary<string, object?> { ["harnessrouter.raw"] = item.Clone() }
            };
        }
    }

    private IEnumerable<AIStreamEvent> CreateActivityEvents(
        JsonElement item,
        Dictionary<string, object?>? responseMetadata)
    {
        var id = GetString(item, "id") ?? $"harnessrouter-activity-{Guid.NewGuid():N}";
        var name = GetString(item, "name") ?? GetString(item, "type") ?? "harness_activity";
        var providerMetadata = CreateScopedMetadata(new Dictionary<string, object?> { ["raw"] = item.Clone() });
        yield return CreateStreamEvent(
            "tool-input-available",
            id,
            new AIToolInputAvailableEventData
            {
                ToolName = name,
                Title = GetString(item, "title") ?? name,
                Input = TryGetProperty(item, "arguments", out var input) ? input.Clone() : item.Clone(),
                ProviderExecuted = true,
                ProviderMetadata = providerMetadata
            },
            responseMetadata);
    }

    private AIStreamEvent CreateFinishEvent(
        HarnessRoute route,
        string? responseId,
        string finishReason,
        JsonElement? response,
        Dictionary<string, object?>? metadata)
    {
        object? usage = response.HasValue && TryGetProperty(response.Value, "usage", out var rawUsage)
            ? rawUsage.Clone()
            : null;
        return CreateStreamEvent(
            "finish",
            responseId,
            new AIFinishEventData
            {
                FinishReason = finishReason,
                Model = route.UnifiedModel,
                CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Response = response,
                MessageMetadata = AIFinishMessageMetadata.Create(
                    route.UnifiedModel,
                    DateTimeOffset.UtcNow,
                    usage,
                    additionalProperties: new Dictionary<string, object?>
                    {
                        ["harnessrouter"] = response
                    })
            },
            metadata);
    }

    private AIStreamEvent CreateStreamEvent(
        string type,
        string? id,
        object data,
        Dictionary<string, object?>? metadata)
        => new()
        {
            ProviderId = GetIdentifier(),
            Event = new AIEventEnvelope
            {
                Type = type,
                Id = id,
                Timestamp = DateTimeOffset.UtcNow,
                Data = data
            },
            Metadata = metadata
        };

    private Dictionary<string, object?> CreateResponseMetadata(
        HarnessRoute route,
        string? responseId,
        string? sessionId,
        JsonElement? raw)
        => new()
        {
            [GetIdentifier()] = new Dictionary<string, object?>
            {
                ["harness_id"] = route.HarnessId,
                ["backend_model"] = route.BackendModel,
                ["response_id"] = responseId,
                ["session_id"] = sessionId,
                ["raw"] = raw
            }
        };

    private Dictionary<string, Dictionary<string, object>> CreateScopedMetadata(Dictionary<string, object?> values)
        => new()
        {
            [GetIdentifier()] = values
                .Where(pair => pair.Value is not null)
                .ToDictionary(pair => pair.Key, pair => pair.Value!)
        };

    private static Dictionary<string, object> CreateLooseMetadata(JsonElement raw)
        => new() { ["raw"] = raw.Clone() };

    private static string? ExtractLatestUserText(AIRequest request)
    {
        foreach (var item in (request.Input?.Items ?? []).AsEnumerable().Reverse())
        {
            if (!string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))
                continue;

            var text = string.Join(
                "\n",
                (item.Content ?? []).OfType<AITextContentPart>()
                    .Select(part => part.Text)
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }
        return null;
    }

    private static string? ExtractResponseText(JsonElement response)
    {
        var direct = GetString(response, "output_text");
        if (!string.IsNullOrWhiteSpace(direct))
            return direct;
        if (!TryGetProperty(response, "output", out var output) || output.ValueKind != JsonValueKind.Array)
            return null;

        var parts = new List<string>();
        foreach (var item in output.EnumerateArray())
        {
            if (!TryGetProperty(item, "content", out var content) || content.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var part in content.EnumerateArray())
            {
                var text = GetString(part, "text") ?? GetString(part, "output_text");
                if (!string.IsNullOrWhiteSpace(text))
                    parts.Add(text);
            }
        }
        return parts.Count == 0 ? null : string.Join("\n", parts);
    }

    private static string ExtractErrorText(JsonElement response)
    {
        if (TryGetProperty(response, "error", out var error))
            return GetString(error, "message") ?? error.ToString();
        return GetString(response, "message") ?? "HarnessRouter task failed.";
    }

    private static async IAsyncEnumerable<string> ReadSseDataAsync(
        StreamReader reader,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (line.Length == 0)
            {
                if (builder.Length > 0)
                {
                    yield return builder.ToString();
                    builder.Clear();
                }
                continue;
            }

            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;
            if (builder.Length > 0)
                builder.AppendLine();
            builder.Append(line[5..].TrimStart());
        }
        if (builder.Length > 0)
            yield return builder.ToString();
    }

    private static bool TryParseObject(string raw, out JsonElement element)
    {
        try
        {
            element = JsonSerializer.Deserialize<JsonElement>(raw, Json).Clone();
            return element.ValueKind == JsonValueKind.Object;
        }
        catch
        {
            element = default;
            return false;
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, string name)
        => TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? GetNestedString(JsonElement element, string parent, string name)
        => TryGetProperty(element, parent, out var nested) ? GetString(nested, name) : null;

    private static string BuildSessionToolCallId(string sessionId)
        => $"harnessrouter-create-session-{sessionId}";
}
