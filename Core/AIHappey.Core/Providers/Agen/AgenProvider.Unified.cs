using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Agen;

public partial class AgenProvider
{
    private const string AgenSessionsEndpoint = "api/v1/sessions";
    private const string AgenSessionToolName = "create_agen_session";
    private const int AgenEventsPageSize = 500;

    private static readonly JsonSerializerOptions AgenJson = JsonSerializerOptions.Web;
    private static readonly TimeSpan AgenPollInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan AgenPollTimeout = TimeSpan.FromMinutes(3);

    private sealed record AgenSessionResolution(string Id, bool Created, JsonElement? RawSession);

    private sealed record AgenTurnPollSnapshot(List<JsonElement> Events, JsonElement? Session);

    private sealed class AgenTurnSnapshot
    {
        public List<AIOutputItem> OutputItems { get; } = [];

        public string Status { get; set; } = "completed";

        public JsonElement? ErrorEvent { get; set; }
    }

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var session = await ResolveAgenSessionAsync(request, cancellationToken);
        var message = ExtractLatestAgenUserText(request)
                      ?? request.Input?.Text
                      ?? request.Instructions
                      ?? throw new InvalidOperationException("Agen requires a user message.");

        var sentEvent = await SendAgenUserMessageAsync(session.Id, message, BuildAgenClientId(request), cancellationToken);
        var sentEventId = TryGetString(sentEvent, "id")
                          ?? throw new InvalidOperationException("Agen send message response did not include an event id.");

        var turn = await WaitForAgenTurnAsync(session.Id, sentEventId, cancellationToken);
        var latestSession = turn.Session ?? await RetrieveAgenSessionAsync(session.Id, cancellationToken);
        var snapshot = BuildAgenTurnSnapshot(turn.Events, includeMessageContent: true);

        if (session.Created && session.RawSession is JsonElement rawSession)
            snapshot.OutputItems.Insert(0, CreateAgenSessionToolOutputItem(session.Id, rawSession));

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = request.Model?.ToModelId(GetIdentifier()) ?? "agent".ToModelId(GetIdentifier()),
            Status = ResolveAgenResponseStatus(latestSession, snapshot),
            Output = snapshot.OutputItems.Count == 0 ? null : new AIOutput { Items = snapshot.OutputItems },
            Usage = TryGetObjectProperty(latestSession, "usage"),
            Metadata = CreateAgenResponseMetadata(session.Id, latestSession, snapshot.ErrorEvent)
        };
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var timestamp = DateTimeOffset.UtcNow;
        var session = await ResolveAgenSessionAsync(request, cancellationToken);
        var model = request.Model?.ToModelId(GetIdentifier()) ?? "agent".ToModelId(GetIdentifier());
        var message = ExtractLatestAgenUserText(request)
                      ?? request.Input?.Text
                      ?? request.Instructions
                      ?? throw new InvalidOperationException("Agen requires a user message.");

        if (session.Created && session.RawSession is JsonElement rawSession)
        {
            foreach (var streamEvent in CreateAgenSessionToolEvents(session.Id, rawSession, timestamp))
                yield return streamEvent;
        }

        var sentEvent = await SendAgenUserMessageAsync(session.Id, message, BuildAgenClientId(request), cancellationToken);
        var sentEventId = TryGetString(sentEvent, "id")
                          ?? throw new InvalidOperationException("Agen send message response did not include an event id.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        AgenTurnPollSnapshot? lastSnapshot = null;
        var started = DateTimeOffset.UtcNow;

        while (!cancellationToken.IsCancellationRequested)
        {
            lastSnapshot = await PollAgenTurnAsync(session.Id, sentEventId, cancellationToken);

            foreach (var timelineEvent in lastSnapshot.Events)
            {
                var eventId = TryGetString(timelineEvent, "id") ?? Guid.NewGuid().ToString("N");
                if (!seen.Add(eventId))
                    continue;

                foreach (var streamEvent in CreateAgenStreamEvents(timelineEvent, model))
                    yield return streamEvent;
            }

            if (IsAgenTurnTerminal(lastSnapshot))
                break;

            if (DateTimeOffset.UtcNow - started >= AgenPollTimeout)
                throw new TimeoutException($"Agen session '{session.Id}' did not complete within {AgenPollTimeout}.");

            await Task.Delay(AgenPollInterval, cancellationToken);
        }

        var latestSession = lastSnapshot?.Session ?? await RetrieveAgenSessionAsync(session.Id, cancellationToken);
        yield return CreateAgenFinishEvent(session.Id, model, latestSession, DateTimeOffset.UtcNow);
    }

    private async Task<AgenSessionResolution> ResolveAgenSessionAsync(
        AIRequest request,
        CancellationToken cancellationToken)
    {
        if (TryFindAgenSessionId(request, out var existingSessionId))
            return new AgenSessionResolution(existingSessionId, false, null);

        var rawSession = await CreateAgenSessionAsync(request, cancellationToken);
        var sessionId = TryGetString(rawSession, "id")
                        ?? throw new InvalidOperationException("Agen create session response did not include an id.");

        return new AgenSessionResolution(sessionId, true, rawSession);
    }

    private async Task<JsonElement> CreateAgenSessionAsync(AIRequest request, CancellationToken cancellationToken)
    {
        var branchName = ResolveAgenBranchName(request);
        var body = new Dictionary<string, object?>
        {
            ["branch_name"] = branchName,
            ["auto_commit"] = request.Metadata?.GetProviderOption<bool?>(GetIdentifier(), "auto_commit")
                              ?? request.Metadata?.GetProviderOption<bool?>(GetIdentifier(), "autoCommit")
                              ?? false
        };

        var sessionModel = request.Metadata?.GetProviderOption<string>(GetIdentifier(), "session_model")
                           ?? request.Metadata?.GetProviderOption<string>(GetIdentifier(), "model");
        var normalizedRequestModel = NormalizeAgenModelId(request.Model);
        if (string.IsNullOrWhiteSpace(sessionModel)
            && !string.IsNullOrWhiteSpace(normalizedRequestModel)
            && !string.Equals(normalizedRequestModel, "agent", StringComparison.OrdinalIgnoreCase))
        {
            sessionModel = normalizedRequestModel;
        }

        if (!string.IsNullOrWhiteSpace(sessionModel))
            body["model"] = sessionModel;

        return await SendAgenJsonAsync(
            HttpMethod.Post,
            AgenSessionsEndpoint,
            body,
            "Agen create session",
            cancellationToken);
    }

    private async Task<JsonElement> SendAgenUserMessageAsync(
        string sessionId,
        string message,
        string clientId,
        CancellationToken cancellationToken)
        => await SendAgenJsonAsync(
            HttpMethod.Post,
            $"{AgenSessionsEndpoint}/{Uri.EscapeDataString(sessionId)}/messages",
            new Dictionary<string, object?>
            {
                ["message"] = message,
                ["client_id"] = clientId
            },
            "Agen send message",
            cancellationToken);

    private async Task<AgenTurnPollSnapshot> WaitForAgenTurnAsync(
        string sessionId,
        string sentEventId,
        CancellationToken cancellationToken)
        => await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: ct => PollAgenTurnAsync(sessionId, sentEventId, ct),
            isTerminal: IsAgenTurnTerminal,
            interval: AgenPollInterval,
            timeout: AgenPollTimeout,
            maxAttempts: null,
            cancellationToken: cancellationToken);

    private async Task<AgenTurnPollSnapshot> PollAgenTurnAsync(
        string sessionId,
        string sentEventId,
        CancellationToken cancellationToken)
    {
        var events = await ListAgenEventsAfterAsync(sessionId, sentEventId, cancellationToken);
        JsonElement? session = null;

        try
        {
            session = await RetrieveAgenSessionAsync(sessionId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Session retrieval is useful for idle detection but timeline events are still authoritative enough to stream progress.
        }

        return new AgenTurnPollSnapshot(events, session);
    }

    private async Task<List<JsonElement>> ListAgenEventsAfterAsync(
        string sessionId,
        string sentEventId,
        CancellationToken cancellationToken)
    {
        var allEvents = await ListAllAgenSessionEventsAsync(sessionId, cancellationToken);
        var filtered = new List<JsonElement>();
        var markerSeen = false;

        foreach (var timelineEvent in allEvents)
        {
            var eventId = TryGetString(timelineEvent, "id");
            if (!markerSeen)
            {
                markerSeen = string.Equals(eventId, sentEventId, StringComparison.Ordinal);
                continue;
            }

            filtered.Add(timelineEvent);
        }

        return markerSeen ? filtered : [];
    }

    private async Task<List<JsonElement>> ListAllAgenSessionEventsAsync(string sessionId, CancellationToken cancellationToken)
    {
        var root = await SendAgenJsonAsync(
            HttpMethod.Get,
            $"{AgenSessionsEndpoint}/{Uri.EscapeDataString(sessionId)}/events",
            operation: "Agen list session events",
            cancellationToken: cancellationToken);

        if (root.ValueKind != JsonValueKind.Array)
            return [];

        return root.EnumerateArray().Select(static item => item.Clone()).ToList();
    }

    private async Task<JsonElement> RetrieveAgenSessionAsync(string sessionId, CancellationToken cancellationToken)
        => await SendAgenJsonAsync(
            HttpMethod.Get,
            $"{AgenSessionsEndpoint}/{Uri.EscapeDataString(sessionId)}",
            operation: "Agen retrieve session",
            cancellationToken: cancellationToken);

    private async Task<JsonElement> SendAgenJsonAsync(
        HttpMethod method,
        string uri,
        object? payload = null,
        string operation = "Agen request",
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));

        if (payload is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(payload, AgenJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json);
        }

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{operation} failed with status {(int)response.StatusCode}: {body}");

        if (string.IsNullOrWhiteSpace(body))
            return JsonSerializer.SerializeToElement(new { }, AgenJson);

        return JsonSerializer.Deserialize<JsonElement>(body, AgenJson).Clone();
    }

    private bool TryFindAgenSessionId(AIRequest request, out string sessionId)
    {
        sessionId = request.Metadata?.GetProviderOption<string>(GetIdentifier(), "sessionId")
                    ?? request.Metadata?.GetProviderOption<string>(GetIdentifier(), "session_id")
                    ?? TryGetStringFromObjectDictionary(request.Input?.Metadata, "sessionId")
                    ?? TryGetStringFromObjectDictionary(request.Input?.Metadata, "session_id")
                    ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(sessionId))
            return true;

        foreach (var item in request.Input?.Items ?? [])
        {
            if (TryExtractAgenSession(item.Metadata, out sessionId))
                return true;

            foreach (var toolPart in item.Content?.OfType<AIToolCallContentPart>() ?? [])
            {
                if (toolPart.ProviderExecuted != true)
                    continue;

                if (TryExtractAgenSession(toolPart.Output, out sessionId))
                    return true;

                if (TryExtractAgenSession(toolPart.Metadata, out sessionId))
                    return true;

                if (TryExtractAgenSession(toolPart.Input, out sessionId))
                    return true;
            }
        }

        sessionId = string.Empty;
        return false;
    }

    private bool TryExtractAgenSession(object? value, out string sessionId)
    {
        sessionId = string.Empty;
        if (value is null)
            return false;

        var element = value is JsonElement json
            ? json
            : JsonSerializer.SerializeToElement(value, AgenJson);

        if (element.ValueKind != JsonValueKind.Object)
            return false;

        if (TryGetProperty(element, "structuredContent", out var structuredContent)
            && TryExtractAgenSession(structuredContent, out sessionId))
        {
            return true;
        }

        if (TryGetProperty(element, GetIdentifier(), out var providerScoped)
            && TryExtractAgenSession(providerScoped, out sessionId))
        {
            return true;
        }

        if (TryGetProperty(element, "session", out var session)
            && TryExtractAgenSession(session, out sessionId))
        {
            return true;
        }

        var type = TryGetString(element, "type") ?? TryGetString(element, "tool_name") ?? TryGetString(element, "name");
        var candidate = TryGetString(element, "sessionId")
                        ?? TryGetString(element, "session_id")
                        ?? (string.Equals(type, AgenSessionToolName, StringComparison.OrdinalIgnoreCase)
                            ? TryGetString(element, "id")
                            : null);

        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        sessionId = candidate;
        return true;
    }

    private string ResolveAgenBranchName(AIRequest request)
    {
        var branchName = request.Metadata?.GetProviderOption<string>(GetIdentifier(), "branch_name")
                         ?? request.Metadata?.GetProviderOption<string>(GetIdentifier(), "branchName")
                         ?? request.Metadata?.GetProviderOption<string>(GetIdentifier(), "branch")
                         ?? TryGetStringFromObjectDictionary(request.Input?.Metadata, "branch_name")
                         ?? TryGetStringFromObjectDictionary(request.Input?.Metadata, "branchName")
                         ?? TryGetStringFromObjectDictionary(request.Input?.Metadata, "branch");

        return !string.IsNullOrWhiteSpace(branchName)
            ? branchName.Trim()
            : CreateGeneratedAgenBranchName(request);
    }

    private static string CreateGeneratedAgenBranchName(AIRequest request)
    {
        var seed = request.Id
                   ?? ExtractLatestAgenUserText(request)
                   ?? request.Input?.Text
                   ?? request.Instructions
                   ?? Guid.NewGuid().ToString("N");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed))).ToLowerInvariant()[..10];
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");

        return $"aihappey/agen-{stamp}-{hash}";
    }

    private static string BuildAgenClientId(AIRequest request)
    {
        var seed = request.Id
                   ?? ExtractLatestAgenUserText(request)
                   ?? request.Input?.Text
                   ?? request.Instructions
                   ?? Guid.NewGuid().ToString("N");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed))).ToLowerInvariant();
        return $"aihappey-{hash[..24]}";
    }

    private static string? ExtractLatestAgenUserText(AIRequest request)
    {
        foreach (var item in (request.Input?.Items ?? []).AsEnumerable().Reverse())
        {
            if (!string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))
                continue;

            var text = string.Join(
                "\n",
                item.Content?.OfType<AITextContentPart>()
                    .Select(part => part.Text)
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                ?? []);

            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return null;
    }

    private AgenTurnSnapshot BuildAgenTurnSnapshot(IEnumerable<JsonElement> events, bool includeMessageContent)
    {
        var snapshot = new AgenTurnSnapshot();

        foreach (var timelineEvent in events)
        {
            var type = TryGetString(timelineEvent, "type") ?? "unknown";
            if (!TryGetProperty(timelineEvent, "payload", out var payload))
                payload = JsonSerializer.SerializeToElement(new { type }, AgenJson);

            switch (type)
            {
                case "agent_message":
                    if (includeMessageContent && TryGetString(payload, "message") is { } agentMessage && !string.IsNullOrWhiteSpace(agentMessage))
                        snapshot.OutputItems.Add(CreateAgenTextOutputItem("assistant", agentMessage, timelineEvent));
                    break;

                case "system_message":
                    if (includeMessageContent && TryGetString(payload, "message") is { } systemMessage && !string.IsNullOrWhiteSpace(systemMessage))
                        snapshot.OutputItems.Add(CreateAgenTextOutputItem("system", systemMessage, timelineEvent));
                    break;

                case "session_closed":
                case "merge_request_closed":
                    snapshot.Status = "failed";
                    snapshot.OutputItems.Add(CreateAgenToolOutputItem(timelineEvent));
                    break;

                case "agent_alert_created":
                    snapshot.Status = "in_progress";
                    snapshot.ErrorEvent = timelineEvent.Clone();
                    snapshot.OutputItems.Add(CreateAgenToolOutputItem(timelineEvent));
                    break;

                case "user_message":
                    break;

                default:
                    snapshot.OutputItems.Add(CreateAgenToolOutputItem(timelineEvent));
                    foreach (var filePart in CreateAgenFileContentParts(timelineEvent))
                    {
                        snapshot.OutputItems.Add(new AIOutputItem
                        {
                            Type = "message",
                            Role = "assistant",
                            Content = [filePart]
                        });
                    }
                    break;
            }
        }

        return snapshot;
    }

    private AIOutputItem CreateAgenTextOutputItem(string role, string text, JsonElement rawEvent)
        => new()
        {
            Type = "message",
            Role = role,
            Content =
            [
                new AITextContentPart
                {
                    Type = "text",
                    Text = text,
                    Metadata = CreateAgenRawMetadata(rawEvent)
                }
            ]
        };

    private AIOutputItem CreateAgenToolOutputItem(JsonElement timelineEvent)
    {
        var type = TryGetString(timelineEvent, "type") ?? "unknown";
        var eventId = TryGetString(timelineEvent, "id") ?? Guid.NewGuid().ToString("N");
        var toolName = NormalizeAgenToolName(type);

        return new AIOutputItem
        {
            Type = "message",
            Role = "assistant",
            Content =
            [
                new AIToolCallContentPart
                {
                    Type = "tool-call",
                    ToolCallId = BuildAgenEventToolCallId(eventId),
                    ToolName = toolName,
                    Title = BuildAgenToolTitle(type, timelineEvent),
                    Input = CreateAgenToolInput(timelineEvent),
                    Output = CreateAgenToolResult(timelineEvent),
                    ProviderExecuted = true,
                    State = IsAgenEventError(timelineEvent) ? "output-error" : "output-available",
                    Metadata = CreateAgenToolMetadata(timelineEvent, toolName)
                }
            ]
        };
    }

    private AIOutputItem CreateAgenSessionToolOutputItem(string sessionId, JsonElement rawSession)
        => new()
        {
            Type = "message",
            Role = "assistant",
            Content =
            [
                new AIToolCallContentPart
                {
                    Type = "tool-call",
                    ToolCallId = BuildAgenSessionToolCallId(sessionId),
                    ToolName = AgenSessionToolName,
                    Title = "Create Agen session",
                    Input = CreateAgenSessionToolInput(rawSession),
                    Output = CreateAgenSessionToolResult(sessionId, rawSession),
                    ProviderExecuted = true,
                    State = "output-available",
                    Metadata = CreateAgenSessionToolMetadata(sessionId, rawSession)
                }
            ]
        };

    private IEnumerable<AIStreamEvent> CreateAgenSessionToolEvents(
        string sessionId,
        JsonElement rawSession,
        DateTimeOffset timestamp)
    {
        var toolCallId = BuildAgenSessionToolCallId(sessionId);
        var providerMetadata = CreateAgenProviderMetadata(new Dictionary<string, object>
        {
            ["type"] = AgenSessionToolName,
            ["sessionId"] = sessionId,
            ["session_id"] = sessionId,
            ["tool_name"] = AgenSessionToolName,
            ["title"] = "Create Agen session"
        });

        yield return CreateAgenStreamEvent(
            "tool-input-available",
            toolCallId,
            new AIToolInputAvailableEventData
            {
                ToolName = AgenSessionToolName,
                Title = "Create Agen session",
                Input = CreateAgenSessionToolInput(rawSession),
                ProviderExecuted = true,
                ProviderMetadata = providerMetadata
            },
            timestamp,
            null);

        yield return CreateAgenStreamEvent(
            "tool-output-available",
            toolCallId,
            new AIToolOutputAvailableEventData
            {
                ToolName = AgenSessionToolName,
                Output = CreateAgenSessionToolResult(sessionId, rawSession),
                ProviderExecuted = true,
                ProviderMetadata = providerMetadata
            },
            timestamp,
            null);
    }

    private IEnumerable<AIStreamEvent> CreateAgenStreamEvents(JsonElement timelineEvent, string model)
    {
        var type = TryGetString(timelineEvent, "type") ?? "unknown";
        var timestamp = ExtractAgenTimestamp(timelineEvent);

        if (string.Equals(type, "agent_message", StringComparison.OrdinalIgnoreCase)
            && TryGetProperty(timelineEvent, "payload", out var payload)
            && TryGetString(payload, "message") is { } message
            && !string.IsNullOrWhiteSpace(message))
        {
            var textId = TryGetString(timelineEvent, "id") ?? Guid.NewGuid().ToString("N");
            var providerMetadata = CreateAgenProviderMetadata(new Dictionary<string, object>
            {
                ["type"] = type,
                ["raw"] = timelineEvent.Clone()
            });

            yield return CreateAgenStreamEvent(
                "text-start",
                textId,
                new AITextStartEventData { ProviderMetadata = FlattenProviderMetadata(providerMetadata) },
                timestamp,
                null);

            yield return CreateAgenStreamEvent(
                "text-delta",
                textId,
                new AITextDeltaEventData
                {
                    Delta = message,
                    ProviderMetadata = FlattenProviderMetadata(providerMetadata)
                },
                timestamp,
                null);

            yield return CreateAgenStreamEvent(
                "text-end",
                textId,
                new AITextEndEventData { ProviderMetadata = FlattenProviderMetadata(providerMetadata) },
                timestamp,
                null);
        }
        else if (string.Equals(type, "system_message", StringComparison.OrdinalIgnoreCase)
                 && TryGetProperty(timelineEvent, "payload", out var systemPayload)
                 && TryGetString(systemPayload, "message") is { } systemMessage
                 && !string.IsNullOrWhiteSpace(systemMessage))
        {
            var dataId = TryGetString(timelineEvent, "id") ?? Guid.NewGuid().ToString("N");
            yield return CreateAgenStreamEvent(
                "data-agen-system-message",
                dataId,
                new AIDataEventData
                {
                    Id = dataId,
                    Data = new { message = systemMessage, raw = timelineEvent.Clone() },
                    Transient = false
                },
                timestamp,
                CreateAgenRawMetadata(timelineEvent));
        }
        else if (!string.Equals(type, "user_message", StringComparison.OrdinalIgnoreCase))
        {
            var toolName = NormalizeAgenToolName(type);
            var eventId = TryGetString(timelineEvent, "id") ?? Guid.NewGuid().ToString("N");
            var toolCallId = BuildAgenEventToolCallId(eventId);
            var providerMetadata = CreateAgenProviderMetadata(ToNonNullDictionary(CreateAgenToolMetadata(timelineEvent, toolName)));

            yield return CreateAgenStreamEvent(
                "tool-input-available",
                toolCallId,
                new AIToolInputAvailableEventData
                {
                    ToolName = toolName,
                    Title = BuildAgenToolTitle(type, timelineEvent),
                    Input = CreateAgenToolInput(timelineEvent),
                    ProviderExecuted = true,
                    ProviderMetadata = providerMetadata
                },
                timestamp,
                null);

            var output = CreateAgenToolResult(timelineEvent);
            if (IsAgenEventError(timelineEvent))
            {
                yield return CreateAgenStreamEvent(
                    "tool-output-error",
                    toolCallId,
                    new AIToolOutputErrorEventData
                    {
                        ToolCallId = toolCallId,
                        ErrorText = ExtractAgenErrorText(timelineEvent) ?? $"Agen event '{type}' reported an error.",
                        ProviderExecuted = true,
                        Dynamic = true,
                        ProviderMetadata = providerMetadata
                    },
                    timestamp,
                    null);
            }
            else
            {
                yield return CreateAgenStreamEvent(
                    "tool-output-available",
                    toolCallId,
                    new AIToolOutputAvailableEventData
                    {
                        ToolName = toolName,
                        Output = output,
                        ProviderExecuted = true,
                        Dynamic = true,
                        ProviderMetadata = providerMetadata
                    },
                    timestamp,
                    null);
            }
        }

        foreach (var sourceEvent in CreateAgenSourceEvents(timelineEvent, timestamp))
            yield return sourceEvent;

        foreach (var fileEvent in CreateAgenFileEvents(timelineEvent, timestamp))
            yield return fileEvent;

        if (IsAgenEventError(timelineEvent))
        {
            yield return CreateAgenStreamEvent(
                "error",
                TryGetString(timelineEvent, "id"),
                new AIErrorEventData { ErrorText = ExtractAgenErrorText(timelineEvent) ?? "Agen session error." },
                timestamp,
                CreateAgenRawMetadata(timelineEvent));
        }
    }

    private IEnumerable<AIStreamEvent> CreateAgenSourceEvents(JsonElement timelineEvent, DateTimeOffset timestamp)
    {
        foreach (var url in ExtractAgenUrls(timelineEvent).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return CreateAgenStreamEvent(
                "source-url",
                $"agen-source-{StableShortHash(url)}",
                new AISourceUrlEventData
                {
                    SourceId = $"agen-source-{StableShortHash(url)}",
                    Url = url,
                    Title = TryGetString(timelineEvent, "type") ?? url,
                    Type = "url",
                    ProviderMetadata = CreateAgenProviderMetadata(new Dictionary<string, object>
                    {
                        ["url"] = url,
                        ["raw"] = timelineEvent.Clone()
                    })
                },
                timestamp,
                null);
        }
    }

    private IEnumerable<AIStreamEvent> CreateAgenFileEvents(JsonElement timelineEvent, DateTimeOffset timestamp)
    {
        foreach (var filePart in CreateAgenFileContentParts(timelineEvent))
        {
            var fallbackFileId = TryGetString(timelineEvent, "id") ?? Guid.NewGuid().ToString("N");
            var filename = filePart.Filename ?? $"agen-{fallbackFileId}.txt";
            var fileId = $"agen-file-{StableShortHash(filename + TryGetString(timelineEvent, "id"))}";

            yield return CreateAgenStreamEvent(
                "file",
                fileId,
                new AIFileEventData
                {
                    MediaType = filePart.MediaType ?? "text/plain",
                    Url = filePart.Data?.ToString() ?? string.Empty,
                    Filename = filename,
                    ProviderMetadata = CreateAgenProviderMetadata(new Dictionary<string, object>
                    {
                        ["filename"] = filename,
                        ["raw"] = timelineEvent.Clone()
                    })
                },
                timestamp,
                null);
        }
    }

    private static IEnumerable<AIFileContentPart> CreateAgenFileContentParts(JsonElement timelineEvent)
    {
        if (!TryGetProperty(timelineEvent, "payload", out var payload))
            yield break;

        var type = TryGetString(timelineEvent, "type") ?? "agen_event";

        if (TryGetString(payload, "diff") is { } diff && !string.IsNullOrWhiteSpace(diff))
        {
            var path = TryGetString(payload, "file_path")
                       ?? TryGetString(payload, "path")
                       ?? TryGetString(payload, "destination_path")
                       ?? $"{type}.diff";
            yield return CreateTextFilePart(path.EndsWith(".diff", StringComparison.OrdinalIgnoreCase) ? path : path + ".diff", diff, timelineEvent);
        }

        if (TryGetString(payload, "output") is { } commandOutput && !string.IsNullOrWhiteSpace(commandOutput))
        {
            var executable = TryGetString(payload, "executable") ?? "command";
            yield return CreateTextFilePart($"{executable}-output.txt", commandOutput, timelineEvent);
        }

        if (TryGetString(payload, "stdout") is { } stdout && !string.IsNullOrWhiteSpace(stdout))
            yield return CreateTextFilePart("stdout.txt", stdout, timelineEvent);

        if (TryGetString(payload, "stderr") is { } stderr && !string.IsNullOrWhiteSpace(stderr))
            yield return CreateTextFilePart("stderr.txt", stderr, timelineEvent);
    }

    private static AIFileContentPart CreateTextFilePart(string filename, string text, JsonElement rawEvent)
        => new()
        {
            Type = "file",
            MediaType = "text/plain",
            Filename = filename,
            Data = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)),
            Metadata = new Dictionary<string, object?> { ["agen.raw"] = rawEvent.Clone() }
        };

    private AIStreamEvent CreateAgenFinishEvent(
        string sessionId,
        string model,
        JsonElement session,
        DateTimeOffset timestamp)
    {
        var status = TryGetString(session, "status") ?? TryGetString(session, "agent_status") ?? "completed";
        var usage = TryGetObjectProperty(session, "usage");
        var providerAdditionalProperties = new Dictionary<string, object?>
        {
            [GetIdentifier()] = new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId,
                ["session_id"] = sessionId,
                ["status"] = status,
                ["session"] = session.Clone()
            }
        };

        return CreateAgenStreamEvent(
            "finish",
            sessionId,
            new AIFinishEventData
            {
                FinishReason = ResolveAgenFinishReason(status),
                Model = model,
                CompletedAt = timestamp.ToUnixTimeSeconds(),
                MessageMetadata = AIFinishMessageMetadata.Create(model, timestamp, usage, additionalProperties: providerAdditionalProperties)
            },
            timestamp,
            CreateAgenResponseMetadata(sessionId, session, null));
    }

    private AIStreamEvent CreateAgenStreamEvent(
        string type,
        string? eventId,
        object data,
        DateTimeOffset timestamp,
        Dictionary<string, object?>? metadata)
        => new()
        {
            ProviderId = GetIdentifier(),
            Event = new AIEventEnvelope
            {
                Type = type,
                Id = eventId,
                Timestamp = timestamp,
                Data = data
            },
            Metadata = metadata
        };

    private static object CreateAgenToolInput(JsonElement timelineEvent)
    {
        if (!TryGetProperty(timelineEvent, "payload", out var payload))
            return JsonSerializer.SerializeToElement(new { }, AgenJson);

        var input = new Dictionary<string, object?>
        {
            ["event_id"] = TryGetString(timelineEvent, "id"),
            ["event_type"] = TryGetString(timelineEvent, "type"),
            ["session_id"] = TryGetString(timelineEvent, "session_id"),
            ["client_id"] = TryGetString(timelineEvent, "client_id")
        };

        AddPayloadValue(input, payload, "repository_id");
        AddPayloadValue(input, payload, "file_path");
        AddPayloadValue(input, payload, "path");
        AddPayloadValue(input, payload, "source_path");
        AddPayloadValue(input, payload, "destination_path");
        AddPayloadValue(input, payload, "query");
        AddPayloadValue(input, payload, "url");
        AddPayloadValue(input, payload, "method");
        AddPayloadValue(input, payload, "executable");
        AddPayloadValue(input, payload, "args");
        AddPayloadValue(input, payload, "working_directory");
        AddPayloadValue(input, payload, "action");

        return JsonSerializer.SerializeToElement(
            input
                .Where(static item => item.Value is not null)
                .ToDictionary(static item => item.Key, static item => item.Value),
            AgenJson);
    }

    private static object CreateAgenToolResult(JsonElement timelineEvent)
    {
        var payload = TryGetProperty(timelineEvent, "payload", out var payloadElement)
            ? payloadElement.Clone()
            : JsonSerializer.SerializeToElement(new { }, AgenJson);

        return new CallToolResult
        {
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                event_id = TryGetString(timelineEvent, "id"),
                session_id = TryGetString(timelineEvent, "session_id"),
                type = TryGetString(timelineEvent, "type"),
                status = TryGetString(timelineEvent, "status"),
                payload
            }, AgenJson)
        };
    }

    private static object CreateAgenSessionToolInput(JsonElement rawSession)
        => JsonSerializer.SerializeToElement(new
        {
            branch_name = TryGetString(rawSession, "branch_name"),
            auto_commit = TryGetBool(rawSession, "auto_commit"),
            model = TryGetString(rawSession, "model")
        }, AgenJson);

    private static CallToolResult CreateAgenSessionToolResult(string sessionId, JsonElement rawSession)
        => new()
        {
            Content = [],
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                sessionId,
                session_id = sessionId,
                branch_name = TryGetString(rawSession, "branch_name"),
                model = TryGetString(rawSession, "model"),
                session = rawSession.Clone()
            }, AgenJson)
        };

    private Dictionary<string, object?> CreateAgenSessionToolMetadata(string sessionId, JsonElement rawSession)
    {
        var providerPayload = new Dictionary<string, object?>
        {
            ["type"] = AgenSessionToolName,
            ["sessionId"] = sessionId,
            ["session_id"] = sessionId,
            ["branch_name"] = TryGetString(rawSession, "branch_name"),
            ["model"] = TryGetString(rawSession, "model"),
            ["tool_name"] = AgenSessionToolName,
            ["raw"] = rawSession.Clone()
        };

        return new Dictionary<string, object?>
        {
            [GetIdentifier()] = JsonSerializer.SerializeToElement(providerPayload, AgenJson),
            ["type"] = AgenSessionToolName,
            ["sessionId"] = sessionId,
            ["session_id"] = sessionId,
            ["tool_name"] = AgenSessionToolName
        };
    }

    private Dictionary<string, object?> CreateAgenToolMetadata(JsonElement timelineEvent, string toolName)
    {
        var providerPayload = new Dictionary<string, object?>
        {
            ["type"] = TryGetString(timelineEvent, "type"),
            ["tool_name"] = toolName,
            ["event_id"] = TryGetString(timelineEvent, "id"),
            ["session_id"] = TryGetString(timelineEvent, "session_id"),
            ["status"] = TryGetString(timelineEvent, "status"),
            ["raw"] = timelineEvent.Clone()
        };

        return new Dictionary<string, object?>
        {
            [GetIdentifier()] = JsonSerializer.SerializeToElement(providerPayload, AgenJson),
            ["type"] = TryGetString(timelineEvent, "type"),
            ["tool_name"] = toolName,
            ["event_id"] = TryGetString(timelineEvent, "id"),
            ["session_id"] = TryGetString(timelineEvent, "session_id")
        };
    }

    private Dictionary<string, object?> CreateAgenResponseMetadata(
        string sessionId,
        JsonElement session,
        JsonElement? errorEvent)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["agen.sessionId"] = sessionId,
            ["agen.session_id"] = sessionId,
            ["agen.session"] = session.Clone(),
            ["agen.status"] = TryGetString(session, "status"),
            ["agen.agent_status"] = TryGetString(session, "agent_status")
        };

        if (errorEvent.HasValue)
            metadata["agen.error"] = errorEvent.Value.Clone();

        return metadata;
    }

    private static Dictionary<string, object?> CreateAgenRawMetadata(JsonElement raw)
        => new() { ["agen.raw"] = raw.Clone() };

    private Dictionary<string, Dictionary<string, object>> CreateAgenProviderMetadata(Dictionary<string, object> metadata)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            [GetIdentifier()] = metadata
        };

    private static Dictionary<string, object> FlattenProviderMetadata(Dictionary<string, Dictionary<string, object>> providerMetadata)
        => providerMetadata.ToDictionary(static item => item.Key, static item => (object)item.Value, StringComparer.OrdinalIgnoreCase);

    private static bool IsAgenTurnTerminal(AgenTurnPollSnapshot snapshot)
    {
        if (snapshot.Events.Any(IsAgenTerminalEvent))
            return true;

        if (snapshot.Events.Count == 0)
            return false;

        if (snapshot.Events.Any(static item =>
            string.Equals(TryGetString(item, "status"), "queued", StringComparison.OrdinalIgnoreCase)
            || string.Equals(TryGetString(item, "status"), "processing", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var hasAgentOutput = snapshot.Events.Any(static item =>
            !string.Equals(TryGetString(item, "type"), "user_message", StringComparison.OrdinalIgnoreCase));

        if (!hasAgentOutput)
            return false;

        if (snapshot.Session is not JsonElement session)
            return true;

        var agentStatus = TryGetString(session, "agent_status") ?? TryGetString(session, "status");
        return !IsAgenBusyStatus(agentStatus);
    }

    private static bool IsAgenTerminalEvent(JsonElement timelineEvent)
        => (TryGetString(timelineEvent, "type") ?? string.Empty) switch
        {
            "session_finished" => true,
            "session_closed" => true,
            "merge_request_merged" => true,
            "merge_request_closed" => true,
            _ => false
        };

    private static bool IsAgenBusyStatus(string? status)
        => (status ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "queued" => true,
            "processing" => true,
            "running" => true,
            "busy" => true,
            "working" => true,
            "in_progress" => true,
            "active" => true,
            _ => false
        };

    private static bool IsAgenEventError(JsonElement timelineEvent)
    {
        var type = TryGetString(timelineEvent, "type");
        var status = TryGetString(timelineEvent, "status");

        return string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase)
               || string.Equals(type, "agent_alert_created", StringComparison.OrdinalIgnoreCase)
               || string.Equals(type, "merge_request_closed", StringComparison.OrdinalIgnoreCase)
               || string.Equals(type, "session_closed", StringComparison.OrdinalIgnoreCase)
               || (TryGetProperty(timelineEvent, "payload", out var payload)
                   && TryGetBool(payload, "is_error") == true);
    }

    private static string? ExtractAgenErrorText(JsonElement timelineEvent)
    {
        if (TryGetProperty(timelineEvent, "payload", out var payload))
        {
            return TryGetString(payload, "message")
                   ?? TryGetString(payload, "description")
                   ?? TryGetString(payload, "title")
                   ?? TryGetString(payload, "error");
        }

        return null;
    }

    private static string ResolveAgenResponseStatus(JsonElement latestSession, AgenTurnSnapshot snapshot)
    {
        if (string.Equals(snapshot.Status, "failed", StringComparison.OrdinalIgnoreCase))
            return "failed";

        var status = TryGetString(latestSession, "status") ?? TryGetString(latestSession, "agent_status");
        if (string.IsNullOrWhiteSpace(status))
            return snapshot.Status;

        return IsAgenBusyStatus(status) ? "in_progress" : "completed";
    }

    private static string ResolveAgenFinishReason(string? status)
        => (status ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "failed" or "error" or "errored" => "error",
            "canceled" or "cancelled" => "cancelled",
            _ => "stop"
        };

    private static string NormalizeAgenToolName(string eventType)
    {
        var normalized = new string(eventType.Select(static ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray()).Trim('_');
        return string.IsNullOrWhiteSpace(normalized) ? "agen_event" : normalized;
    }

    private static string BuildAgenToolTitle(string eventType, JsonElement timelineEvent)
    {
        if (TryGetProperty(timelineEvent, "payload", out var payload))
        {
            var path = TryGetString(payload, "file_path") ?? TryGetString(payload, "path") ?? TryGetString(payload, "url");
            if (!string.IsNullOrWhiteSpace(path))
                return $"Agen {eventType}: {path}";

            var title = TryGetString(payload, "title") ?? TryGetString(payload, "goal_title") ?? TryGetString(payload, "query");
            if (!string.IsNullOrWhiteSpace(title))
                return $"Agen {eventType}: {title}";
        }

        return $"Agen {eventType}";
    }

    private static string BuildAgenSessionToolCallId(string sessionId)
        => $"agen-create-session-{sessionId}";

    private static string BuildAgenEventToolCallId(string eventId)
        => $"agen-event-{eventId}";

    private static DateTimeOffset ExtractAgenTimestamp(JsonElement timelineEvent)
    {
        if (TryGetDateTimeOffset(timelineEvent, "created_at") is { } createdAt)
            return createdAt;

        if (TryGetDateTimeOffset(timelineEvent, "updated_at") is { } updatedAt)
            return updatedAt;

        return DateTimeOffset.UtcNow;
    }

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement element, string propertyName)
    {
        var value = TryGetString(element, propertyName);
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }

    private string NormalizeAgenModelId(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return string.Empty;

        var trimmed = model.Trim();
        var providerPrefix = GetIdentifier() + "/";
        return trimmed.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed.SplitModelId().Model
            : trimmed;
    }

    private static IEnumerable<string> ExtractAgenUrls(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    var value = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(value)
                        && (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                            || value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
                    {
                        yield return value;
                    }
                }
                else
                {
                    foreach (var nested in ExtractAgenUrls(property.Value))
                        yield return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var nested in ExtractAgenUrls(item))
                    yield return nested;
            }
        }
    }

    private static void AddPayloadValue(Dictionary<string, object?> input, JsonElement payload, string propertyName)
    {
        if (TryGetProperty(payload, propertyName, out var value))
            input[propertyName] = value.Clone();
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value.Clone();
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static object? TryGetObjectProperty(JsonElement element, string propertyName)
        => TryGetProperty(element, propertyName, out var value) ? value.Clone() : null;

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static bool? TryGetBool(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static string? TryGetStringFromObjectDictionary(Dictionary<string, object?>? dictionary, string key)
    {
        if (dictionary is null)
            return null;

        foreach (var item in dictionary)
        {
            if (!string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase))
                continue;

            return item.Value switch
            {
                string value => value,
                JsonElement json when json.ValueKind == JsonValueKind.String => json.GetString(),
                JsonElement json when json.ValueKind == JsonValueKind.Number => json.ToString(),
                null => null,
                _ => item.Value.ToString()
            };
        }

        return null;
    }

    private static string StableShortHash(string? value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..12];
    }

    private static Dictionary<string, object> ToNonNullDictionary(Dictionary<string, object?>? metadata)
        => metadata?
            .Where(static item => item.Value is not null)
            .ToDictionary(static item => item.Key, static item => item.Value!, StringComparer.OrdinalIgnoreCase)
           ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
}
