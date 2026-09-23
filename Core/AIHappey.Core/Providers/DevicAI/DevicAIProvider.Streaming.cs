using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.DevicAI;

public partial class DevicAIProvider
{
    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var route = ParseDevicAIRoute(request.Model);
        if (route.Kind == DevicAIRouteKind.Whisper)
            throw new NotSupportedException("The DevicAI Whisper model is available through transcription endpoints.");

        if (route.Kind == DevicAIRouteKind.Agent)
        {
            var execution = await RunAgentAsync(request, route, cancellationToken);
            var response = CreateAgentResponse(execution);
            await foreach (var streamEvent in MimicResponseStream(response, execution.Thread, cancellationToken))
                yield return streamEvent;
            yield break;
        }

        await foreach (var streamEvent in StreamAssistantAsync(request, route, cancellationToken))
            yield return streamEvent;
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamAssistantAsync(
        AIRequest request,
        DevicAIRoute route,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var hasExisting = TryFindChatUid(request, out var chatUid);
        var baselineIds = new HashSet<string>(StringComparer.Ordinal);
        if (hasExisting)
        {
            var baseline = await GetChatHistoryAsync(route.Target, chatUid, cancellationToken);
            ValidateChatAssistant(baseline, route.Target);
            foreach (var message in GetArray(baseline, "chatContent"))
            {
                var uid = GetString(message, "uid");
                if (!string.IsNullOrWhiteSpace(uid)) baselineIds.Add(uid);
            }
        }

        JsonElement acknowledgement;
        var toolResponses = ExtractClientToolResponses(request);
        IReadOnlyList<JsonElement> uploads = [];
        if (toolResponses.Count > 0)
        {
            if (!hasExisting)
                throw new InvalidOperationException("DevicAI client tool responses require a recovered chatUid.");
            acknowledgement = await SendJsonAsync(
                HttpMethod.Post,
                $"assistants/{Uri.EscapeDataString(route.Target)}/chats/{Uri.EscapeDataString(chatUid)}/tool-response",
                new { responses = toolResponses },
                "submit tool responses",
                cancellationToken);
        }
        else
        {
            var attachments = await ResolveAssistantAttachmentsAsync(request, cancellationToken);
            uploads = attachments.Uploads;
            var payload = BuildAssistantMessagePayload(request, hasExisting ? chatUid : null, attachments);
            acknowledgement = await SendJsonAsync(
                HttpMethod.Post,
                BuildAsyncAssistantMessagePath(route.Target, request),
                payload,
                "start async assistant message",
                cancellationToken);
            chatUid = GetString(acknowledgement, "chatUid")
                      ?? throw new InvalidOperationException("DevicAI async assistant acknowledgement did not include chatUid.");
        }

        if (!hasExisting)
        {
            var identityExecution = new DevicAIAssistantExecution(
                route, chatUid, true, [], acknowledgement, null, "processing", uploads);
            foreach (var identityEvent in CreateIdentityStreamEvents(CreateChatIdentityPart(identityExecution), acknowledgement))
                yield return identityEvent;
        }

        var streamedMessageIds = new HashSet<string>(StringComparer.Ordinal);
        var emittedToolCallIds = new HashSet<string>(StringComparer.Ordinal);
        var activeTextId = $"devicai-text-{chatUid}";
        var activeText = new StringBuilder();
        var textStarted = false;
        JsonElement lastSnapshot = default;
        string? lastStatus = null;

        while (lastStatus is null || (!TerminalChatStatuses.Contains(lastStatus)
                                      && !string.Equals(lastStatus, "waiting_for_tool_response", StringComparison.OrdinalIgnoreCase)))
        {
            await foreach (var frame in ReadAssistantSseConnectionAsync(route.Target, chatUid, cancellationToken))
            {
                var now = DateTimeOffset.UtcNow;
                if (string.Equals(frame.Event, "snapshot", StringComparison.OrdinalIgnoreCase))
                {
                    lastSnapshot = frame.Data.Clone();
                    lastStatus = GetString(frame.Data, "status") ?? lastStatus;
                    if (TryGetProperty(frame.Data, "streamingMessage", out var streamingMessage))
                    {
                        var messageId = GetString(streamingMessage, "uid") ?? activeTextId;
                        if (!string.IsNullOrWhiteSpace(messageId)) streamedMessageIds.Add(messageId);
                        var fullText = ExtractChatMessageText(streamingMessage) ?? string.Empty;
                        foreach (var textEvent in CreateTextGrowthEvents(
                                     fullText, ref textStarted, activeText, activeTextId, streamingMessage, now))
                            yield return textEvent;
                    }

                    if (string.Equals(lastStatus, "waiting_for_tool_response", StringComparison.OrdinalIgnoreCase))
                        foreach (var toolEvent in CreatePendingToolStreamEvents(frame.Data, emittedToolCallIds, now))
                            yield return toolEvent;
                }
                else if (string.Equals(frame.Event, "partial", StringComparison.OrdinalIgnoreCase))
                {
                    var streamingMessage = TryGetProperty(frame.Data, "streamingMessage", out var nested)
                        ? nested
                        : frame.Data;
                    var messageId = GetString(streamingMessage, "uid") ?? activeTextId;
                    if (!string.IsNullOrWhiteSpace(messageId)) streamedMessageIds.Add(messageId);
                    var fullText = ExtractChatMessageText(streamingMessage) ?? string.Empty;
                    foreach (var textEvent in CreateTextGrowthEvents(
                                 fullText, ref textStarted, activeText, activeTextId, streamingMessage, now))
                        yield return textEvent;
                }
                else if (string.Equals(frame.Event, "delta", StringComparison.OrdinalIgnoreCase))
                {
                    var append = GetString(frame.Data, "append") ?? string.Empty;
                    if (!textStarted)
                    {
                        textStarted = true;
                        yield return StreamEvent("text-start", activeTextId,
                            new AITextStartEventData { ProviderMetadata = LooseMetadata(frame.Data) }, now,
                            FrameMetadata(frame.Event, frame.Data, chatUid));
                    }
                    if (append.Length > 0)
                    {
                        activeText.Append(append);
                        yield return StreamEvent("text-delta", activeTextId,
                            new AITextDeltaEventData { Delta = append, ProviderMetadata = LooseMetadata(frame.Data) }, now,
                            FrameMetadata(frame.Event, frame.Data, chatUid));
                    }
                }

                if (lastStatus is not null
                    && (TerminalChatStatuses.Contains(lastStatus)
                        || string.Equals(lastStatus, "waiting_for_tool_response", StringComparison.OrdinalIgnoreCase)))
                    break;
            }
        }

        var completedAt = DateTimeOffset.UtcNow;
        if (textStarted)
            yield return StreamEvent("text-end", activeTextId,
                new AITextEndEventData
                {
                    ProviderMetadata = lastSnapshot.ValueKind == JsonValueKind.Object
                        ? LooseMetadata(lastSnapshot)
                        : null
                }, completedAt, FrameMetadata("terminal", lastSnapshot, chatUid));

        var history = await GetChatHistoryAsync(route.Target, chatUid, cancellationToken);
        var newMessages = FilterNewMessages(GetArray(history, "chatContent"), baselineIds)
            .Where(message =>
            {
                var uid = GetString(message, "uid");
                return string.IsNullOrWhiteSpace(uid) || !streamedMessageIds.Contains(uid);
            }).ToList();
        foreach (var part in MapAssistantMessages(newMessages))
        {
            if (part is AIToolCallContentPart tool && !emittedToolCallIds.Add(tool.ToolCallId))
                continue;
            foreach (var mapped in ContentPartToStreamEvents(part, completedAt, FrameMetadata("history", history, chatUid)))
                yield return mapped;
        }

        if (lastStatus is "error" or "limit_exceeded")
        {
            yield return StreamEvent("error", chatUid,
                new AIErrorEventData
                {
                    ErrorText = GetString(lastSnapshot, "error", "message")
                                ?? $"DevicAI chat ended with status '{lastStatus}'."
                }, completedAt, FrameMetadata("terminal", lastSnapshot, chatUid));
            yield break;
        }

        var usage = CreateUsage(history);
        yield return StreamEvent("finish", chatUid,
            new AIFinishEventData
            {
                FinishReason = string.Equals(lastStatus, "waiting_for_tool_response", StringComparison.OrdinalIgnoreCase)
                    ? "tool-calls"
                    : "stop",
                Model = route.ModelId,
                CompletedAt = completedAt.ToUnixTimeSeconds(),
                InputTokens = usage?.InputTokens,
                OutputTokens = usage?.OutputTokens,
                TotalTokens = usage?.TotalTokens,
                Response = history.Clone(),
                MessageMetadata = AIFinishMessageMetadata.Create(
                    route.ModelId,
                    completedAt,
                    usage,
                    inputTokens: usage?.InputTokens,
                    outputTokens: usage?.OutputTokens,
                    totalTokens: usage?.TotalTokens,
                    additionalProperties: new Dictionary<string, object?>
                    {
                        ["devicai"] = new { chatUid, chat_uid = chatUid, raw = history.Clone() }
                    })
            }, completedAt, FrameMetadata("terminal", history, chatUid));
    }

    private async IAsyncEnumerable<AIStreamEvent> MimicResponseStream(
        AIResponse response,
        JsonElement raw,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        foreach (var part in response.Output?.Items?.SelectMany(item => item.Content ?? []) ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var streamEvent in ContentPartToStreamEvents(part, now, response.Metadata))
                yield return streamEvent;
        }

        if (string.Equals(response.Status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            yield return StreamEvent("error", GetString(raw, "id"),
                new AIErrorEventData
                {
                    ErrorText = GetString(raw, "error", "message") ?? "DevicAI agent thread failed."
                }, now, response.Metadata);
            yield break;
        }

        var usage = response.NormalizedUsage;
        yield return StreamEvent("finish", GetString(raw, "id"),
            new AIFinishEventData
            {
                FinishReason = string.Equals(response.Status, "incomplete", StringComparison.OrdinalIgnoreCase)
                    ? "stop"
                    : "stop",
                Model = response.Model,
                CompletedAt = now.ToUnixTimeSeconds(),
                InputTokens = usage?.InputTokens,
                OutputTokens = usage?.OutputTokens,
                TotalTokens = usage?.TotalTokens,
                Response = raw.Clone(),
                MessageMetadata = AIFinishMessageMetadata.Create(
                    response.Model,
                    now,
                    usage,
                    inputTokens: usage?.InputTokens,
                    outputTokens: usage?.OutputTokens,
                    totalTokens: usage?.TotalTokens,
                    additionalProperties: response.Metadata)
            }, now, response.Metadata);
    }

    private IEnumerable<AIStreamEvent> ContentPartToStreamEvents(
        AIContentPart part,
        DateTimeOffset timestamp,
        Dictionary<string, object?>? metadata)
    {
        if (part is AITextContentPart text)
        {
            var id = $"devicai-text-{Guid.NewGuid():N}";
            var providerMetadata = ToLooseProviderMetadata(text.Metadata);
            yield return StreamEvent("text-start", id,
                new AITextStartEventData { ProviderMetadata = providerMetadata }, timestamp, metadata);
            yield return StreamEvent("text-delta", id,
                new AITextDeltaEventData { Delta = text.Text, ProviderMetadata = providerMetadata }, timestamp, metadata);
            yield return StreamEvent("text-end", id,
                new AITextEndEventData { ProviderMetadata = providerMetadata }, timestamp, metadata);
            yield break;
        }

        if (part is not AIToolCallContentPart tool)
            yield break;

        var providerScoped = ToScopedProviderMetadata(tool.Metadata);
        yield return StreamEvent("tool-input-available", tool.ToolCallId,
            new AIToolInputAvailableEventData
            {
                ToolName = tool.ToolName ?? "devicai_tool",
                Title = tool.Title,
                Input = tool.Input ?? new { },
                ProviderExecuted = tool.ProviderExecuted,
                ProviderMetadata = providerScoped
            }, timestamp, metadata);

        if (tool.Approval is not null)
            yield return StreamEvent("tool-approval-request", tool.ToolCallId,
                new AIToolApprovalRequestEventData
                {
                    ApprovalId = tool.Approval.Id ?? tool.ToolCallId,
                    ToolCallId = tool.ToolCallId
                }, timestamp, metadata);

        if (tool.Output is not null)
            yield return StreamEvent("tool-output-available", tool.ToolCallId,
                new AIToolOutputAvailableEventData
                {
                    ToolName = tool.ToolName,
                    Output = tool.Output,
                    ProviderExecuted = tool.ProviderExecuted,
                    Dynamic = true,
                    ProviderMetadata = providerScoped
                }, timestamp, metadata);
    }

    private IEnumerable<AIStreamEvent> CreateIdentityStreamEvents(AIToolCallContentPart tool, JsonElement raw)
        => ContentPartToStreamEvents(tool, DateTimeOffset.UtcNow,
            new Dictionary<string, object?> { ["devicai.raw"] = raw.Clone() });

    private IEnumerable<AIStreamEvent> CreatePendingToolStreamEvents(
        JsonElement snapshot,
        HashSet<string> emitted,
        DateTimeOffset timestamp)
    {
        foreach (var message in GetArray(snapshot, "chatHistory"))
        foreach (var call in GetArray(message, "tool_calls"))
        {
            var callId = GetString(call, "id");
            if (string.IsNullOrWhiteSpace(callId) || !emitted.Add(callId))
                continue;
            var function = TryGetProperty(call, "function", out var value) ? value : default;
            var name = function.ValueKind == JsonValueKind.Object
                ? GetString(function, "name") ?? "devicai_tool"
                : "devicai_tool";
            yield return StreamEvent("tool-input-available", callId,
                new AIToolInputAvailableEventData
                {
                    ToolName = NormalizeToolName(name),
                    Title = name,
                    Input = ParseJsonValue(GetString(function, "arguments")),
                    ProviderExecuted = false,
                    ProviderMetadata = ScopedMetadata(call)
                }, timestamp, FrameMetadata("snapshot", snapshot, GetString(snapshot, "chatUID", "chatUid")));
        }
    }

    private List<AIStreamEvent> CreateTextGrowthEvents(
        string fullText,
        ref bool started,
        StringBuilder buffer,
        string textId,
        JsonElement raw,
        DateTimeOffset timestamp)
    {
        var events = new List<AIStreamEvent>();
        if (!started && fullText.Length > 0)
        {
            started = true;
            events.Add(StreamEvent("text-start", textId,
                new AITextStartEventData { ProviderMetadata = LooseMetadata(raw) }, timestamp,
                FrameMetadata("partial", raw, GetString(raw, "chatUid", "chatUID"))));
        }
        if (fullText.Length <= buffer.Length)
            return events;
        var append = fullText[buffer.Length..];
        buffer.Append(append);
        events.Add(StreamEvent("text-delta", textId,
            new AITextDeltaEventData { Delta = append, ProviderMetadata = LooseMetadata(raw) }, timestamp,
            FrameMetadata("partial", raw, GetString(raw, "chatUid", "chatUID"))));
        return events;
    }

    private async IAsyncEnumerable<(string Event, JsonElement Data)> ReadAssistantSseConnectionAsync(
        string identifier,
        string chatUid,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var request = CreateRequest(
            HttpMethod.Get,
            $"assistants/{Uri.EscapeDataString(identifier)}/chats/{Uri.EscapeDataString(chatUid)}/stream?partial=1");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"DevicAI stream chat failed with status {(int)response.StatusCode} ({response.StatusCode}): {body}",
                null,
                response.StatusCode);
        }
        if (response.Content.Headers.ContentType?.MediaType is not "text/event-stream")
            throw new InvalidOperationException("DevicAI assistant stream did not return text/event-stream.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        string? eventName = null;
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                if (!string.IsNullOrWhiteSpace(eventName) && data.Length > 0)
                {
                    JsonElement parsed;
                    try { parsed = JsonSerializer.Deserialize<JsonElement>(data.ToString(), DevicAIJson).Clone(); }
                    catch (JsonException) { eventName = null; data.Clear(); continue; }
                    yield return (eventName, parsed);
                }
                eventName = null;
                data.Clear();
                continue;
            }
            if (line.StartsWith(':')) continue;
            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
                eventName = line[6..].Trim();
            else if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                if (data.Length > 0) data.AppendLine();
                data.Append(line[5..].TrimStart());
            }
        }
    }

    private AIStreamEvent StreamEvent(
        string type,
        string? id,
        object data,
        DateTimeOffset timestamp,
        Dictionary<string, object?>? metadata)
        => new()
        {
            ProviderId = GetIdentifier(),
            Event = new AIEventEnvelope { Type = type, Id = id, Timestamp = timestamp, Data = data },
            Metadata = metadata
        };

    private static string BuildAsyncAssistantMessagePath(string identifier, AIRequest request)
    {
        var query = new List<string> { "async=true" };
        var options = GetDevicAIOptions(request);
        if (options.HasValue)
        {
            var language = GetString(options.Value, "language");
            if (!string.IsNullOrWhiteSpace(language))
                query.Add($"language={Uri.EscapeDataString(language)}");
            var skip = GetBoolean(options.Value, "skipSummarization", "skip_summarization");
            if (skip.HasValue)
                query.Add($"skipSummarization={skip.Value.ToString().ToLowerInvariant()}");
        }
        return $"assistants/{Uri.EscapeDataString(identifier)}/messages?{string.Join('&', query)}";
    }

    private static string? ExtractChatMessageText(JsonElement message)
        => TryGetProperty(message, "content", out var content) && content.ValueKind == JsonValueKind.Object
            ? GetString(content, "message")
            : null;

    private static Dictionary<string, object?> FrameMetadata(string eventName, JsonElement raw, string? chatUid)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["devicai.event"] = eventName,
            ["devicai.chat_uid"] = chatUid,
            ["devicai.raw"] = raw.ValueKind == JsonValueKind.Undefined ? null : raw.Clone(),
            ["chatUid"] = chatUid,
            ["chat_uid"] = chatUid
        };

    private static Dictionary<string, object>? ToLooseProviderMetadata(Dictionary<string, object?>? metadata)
    {
        if (metadata is null) return null;
        return metadata.Where(entry => entry.Value is not null)
            .ToDictionary(entry => entry.Key, entry => entry.Value!, StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, Dictionary<string, object>>? ToScopedProviderMetadata(
        Dictionary<string, object?>? metadata)
    {
        if (metadata is null) return null;
        return new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase)
        {
            ["devicai"] = metadata.Where(entry => entry.Value is not null)
                .ToDictionary(entry => entry.Key, entry => entry.Value!, StringComparer.OrdinalIgnoreCase)
        };
    }
}
