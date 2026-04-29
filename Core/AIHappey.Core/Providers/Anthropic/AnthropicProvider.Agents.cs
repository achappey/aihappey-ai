using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Anthropic;

public partial class AnthropicProvider
{
    private const string ManagedAgentsBeta = "managed-agents-2026-04-01";
    private const string ManagedAgentModelPrefix = "agent/";
    private const string ManagedAgentSessionToolName = "create_managed_agent_session";
    private const string ManagedAgentsEndpoint = "v1/agents";
    private const string ManagedAgentEnvironmentsEndpoint = "v1/environments";
    private const string ManagedAgentSessionsEndpoint = "v1/sessions";
    private const int ManagedAgentListPageSize = 100;
    private const int ManagedAgentEventsPageSize = 200;

    private static readonly TimeSpan ManagedAgentPollInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan ManagedAgentPollTimeout = TimeSpan.FromSeconds(30);

    private sealed record AnthropicManagedAgentTarget(string AgentId, string EnvironmentId)
    {
        public string LocalModelId => $"{ManagedAgentModelPrefix}{AgentId}/{EnvironmentId}";
    }

    private sealed record AnthropicManagedAgentDefinition(
        string Id,
        string? Name,
        string? Description,
        string? ModelId,
        DateTimeOffset? CreatedAt);

    private sealed record AnthropicManagedAgentEnvironmentDefinition(
        string Id,
        string? Name,
        string? Description,
        DateTimeOffset? CreatedAt);

    private sealed record AnthropicManagedAgentSessionResolution(
        string Id,
        bool Created,
        JsonElement? RawSession);

    private sealed class AnthropicManagedAgentTextEntry
    {
        public required string Text { get; init; }

        public JsonElement? RawEvent { get; init; }
    }

    private sealed class AnthropicManagedAgentToolEntry
    {
        public required string ToolCallId { get; init; }

        public string? ToolName { get; set; }

        public string? Title { get; set; }

        public object? Input { get; set; }

        public object? Output { get; set; }

        public string State { get; set; } = "input-available";

        public bool ProviderExecuted { get; init; } = true;

        public Dictionary<string, object?>? Metadata { get; set; }
    }

    private sealed class AnthropicManagedAgentTurnSnapshot
    {
        public List<object> Entries { get; } = [];

        public Dictionary<string, AnthropicManagedAgentToolEntry> ToolEntries { get; } = new(StringComparer.Ordinal);

        public string Status { get; set; } = "completed";

        public JsonElement? TerminalEvent { get; set; }

        public JsonElement? ErrorEvent { get; set; }
    }

    private sealed class AnthropicManagedAgentStreamState
    {
        public Dictionary<string, AnthropicManagedAgentToolEntry> ToolEntries { get; } = new(StringComparer.Ordinal);

        public HashSet<string> SeenEventIds { get; } = new(StringComparer.Ordinal);
    }

    private async Task<AIResponse> ExecuteManagedAgentUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryResolveManagedAgentTarget(request.Model, out var target))
            throw new InvalidOperationException("Anthropic managed-agent target could not be resolved from the model id.");

        var timestamp = DateTimeOffset.UtcNow;
        var session = await ResolveManagedAgentSessionAsync(request, target, cancellationToken);
        var text = ExtractLatestManagedAgentUserText(request)
                   ?? request.Input?.Text
                   ?? request.Instructions
                   ?? throw new InvalidOperationException("Anthropic managed agents require a user message.");

        var sentEventId = await SendManagedAgentUserMessageAsync(session.Id, text, cancellationToken);
        var events = await WaitForManagedAgentTurnEventsAsync(session.Id, sentEventId, cancellationToken);
        var latestSession = await RetrieveManagedAgentSessionAsync(session.Id, cancellationToken);
        var snapshot = BuildManagedAgentTurnSnapshot(events);

        var outputItems = new List<AIOutputItem>();

        if (session.Created && session.RawSession is JsonElement rawSession)
            outputItems.Add(CreateManagedAgentSessionToolOutputItem(session.Id, target, rawSession));

        outputItems.AddRange(snapshot.Entries.Select(CreateManagedAgentOutputItem));

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = request.Model?.ToModelId(GetIdentifier())
                ?? target.LocalModelId.ToModelId(GetIdentifier()),
            Status = snapshot.Status,
            Output = outputItems.Count == 0 ? null : new AIOutput { Items = outputItems },
            Usage = TryGetObjectProperty(latestSession, "usage"),
            Metadata = CreateManagedAgentResponseMetadata(session.Id, target, latestSession, snapshot.ErrorEvent)
        };
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamManagedAgentUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryResolveManagedAgentTarget(request.Model, out var target))
            throw new InvalidOperationException("Anthropic managed-agent target could not be resolved from the model id.");

        var timestamp = DateTimeOffset.UtcNow;
        var session = await ResolveManagedAgentSessionAsync(request, target, cancellationToken);
        var text = ExtractLatestManagedAgentUserText(request)
                   ?? request.Input?.Text
                   ?? request.Instructions
                   ?? throw new InvalidOperationException("Anthropic managed agents require a user message.");

        if (session.Created && session.RawSession is JsonElement rawSession)
        {
            foreach (var evt in CreateManagedAgentSessionToolEvents(session.Id, target, rawSession, timestamp))
                yield return evt;
        }

        var sentEventId = await SendManagedAgentUserMessageAsync(session.Id, text, cancellationToken);
        var streamState = new AnthropicManagedAgentStreamState();
        JsonElement? terminalEvent = null;
        JsonElement? errorEvent = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            var events = await ListManagedAgentEventsAfterAsync(session.Id, sentEventId, cancellationToken);

            foreach (var managedAgentEvent in events)
            {
                var eventId = TryGetString(managedAgentEvent, "id") ?? Guid.NewGuid().ToString("N");
                if (!streamState.SeenEventIds.Add(eventId))
                    continue;

                foreach (var streamEvent in CreateManagedAgentStreamEvents(
                             managedAgentEvent,
                             request.Model ?? target.LocalModelId.ToModelId(GetIdentifier()),
                             streamState))
                {
                    yield return streamEvent;
                }

                var eventType = TryGetString(managedAgentEvent, "type");
                if (string.Equals(eventType, "session.error", StringComparison.OrdinalIgnoreCase))
                    errorEvent = managedAgentEvent.Clone();

                if (IsManagedAgentTerminalEvent(managedAgentEvent))
                    terminalEvent = managedAgentEvent.Clone();
            }

            if (terminalEvent.HasValue)
                break;

            await Task.Delay(ManagedAgentPollInterval, cancellationToken);
        }

        var latestSession = await RetrieveManagedAgentSessionAsync(session.Id, cancellationToken);
        yield return CreateManagedAgentFinishEvent(
            session.Id,
            request.Model?.ToModelId(GetIdentifier()) ?? target.LocalModelId.ToModelId(GetIdentifier()),
            latestSession,
            terminalEvent,
            errorEvent,
            DateTimeOffset.UtcNow);
    }

    private async Task<IEnumerable<Model>> ListManagedAgentModelsSafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var agents = await ListManagedAgentsAsync(cancellationToken);
            var environments = await ListManagedAgentEnvironmentsAsync(cancellationToken);

            if (agents.Count == 0 || environments.Count == 0)
                return [];

            var models = new List<Model>();

            foreach (var agent in agents)
            {
                foreach (var environment in environments)
                {
                    var created = new[] { agent.CreatedAt, environment.CreatedAt }
                        .Where(static value => value.HasValue)
                        .Select(static value => value!.Value)
                        .DefaultIfEmpty(DateTimeOffset.UtcNow)
                        .Max();

                    AddManagedAgentModelIfMissing(models, new Model
                    {
                        Id = $"{ManagedAgentModelPrefix}{agent.Id}/{environment.Id}".ToModelId(GetIdentifier()),
                        Name = $"{GetManagedAgentDisplayName(agent)} @ {GetManagedAgentEnvironmentDisplayName(environment)}",
                        Description = BuildManagedAgentModelDescription(agent, environment),
                        OwnedBy = nameof(Anthropic),
                        Type = "language",
                        Created = created.ToUnixTimeSeconds(),
                        Tags = BuildManagedAgentModelTags(agent, environment)
                    });
                }
            }

            return models;
        }
        catch
        {
            return [];
        }
    }

    private async Task<IReadOnlyList<AnthropicManagedAgentDefinition>> ListManagedAgentsAsync(CancellationToken cancellationToken)
    {
        var results = new List<AnthropicManagedAgentDefinition>();
        string? page = null;

        while (true)
        {
            var uri = page is null
                ? $"{ManagedAgentsEndpoint}?limit={ManagedAgentListPageSize}"
                : $"{ManagedAgentsEndpoint}?limit={ManagedAgentListPageSize}&page={Uri.EscapeDataString(page)}";

            var root = await SendManagedAgentsJsonAsync(HttpMethod.Get, uri, operation: "Anthropic managed agents list", cancellationToken: cancellationToken);
            if (!TryGetProperty(root, "data", out var data) || data.ValueKind != JsonValueKind.Array)
                break;

            foreach (var item in data.EnumerateArray())
            {
                var id = TryGetString(item, "id");
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                string? modelId = null;
                if (TryGetProperty(item, "model", out var model) && model.ValueKind == JsonValueKind.Object)
                    modelId = TryGetString(model, "id");

                results.Add(new AnthropicManagedAgentDefinition(
                    id,
                    TryGetString(item, "name"),
                    TryGetString(item, "description"),
                    modelId,
                    TryGetDateTimeOffset(item, "created_at")));
            }

            page = TryGetString(root, "next_page");
            if (string.IsNullOrWhiteSpace(page))
                break;
        }

        return results;
    }

    private async Task<IReadOnlyList<AnthropicManagedAgentEnvironmentDefinition>> ListManagedAgentEnvironmentsAsync(CancellationToken cancellationToken)
    {
        var results = new List<AnthropicManagedAgentEnvironmentDefinition>();
        string? page = null;

        while (true)
        {
            var uri = page is null
                ? $"{ManagedAgentEnvironmentsEndpoint}?limit={ManagedAgentListPageSize}"
                : $"{ManagedAgentEnvironmentsEndpoint}?limit={ManagedAgentListPageSize}&page={Uri.EscapeDataString(page)}";

            var root = await SendManagedAgentsJsonAsync(HttpMethod.Get, uri, operation: "Anthropic managed agent environments list", cancellationToken: cancellationToken);
            if (!TryGetProperty(root, "data", out var data) || data.ValueKind != JsonValueKind.Array)
                break;

            foreach (var item in data.EnumerateArray())
            {
                var id = TryGetString(item, "id");
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                results.Add(new AnthropicManagedAgentEnvironmentDefinition(
                    id,
                    TryGetString(item, "name"),
                    TryGetString(item, "description"),
                    TryGetDateTimeOffset(item, "created_at")));
            }

            page = TryGetString(root, "next_page");
            if (string.IsNullOrWhiteSpace(page))
                break;
        }

        return results;
    }

    private async Task<AnthropicManagedAgentSessionResolution> ResolveManagedAgentSessionAsync(
        AIRequest request,
        AnthropicManagedAgentTarget target,
        CancellationToken cancellationToken)
    {
        if (TryFindManagedAgentSessionId(request, target, out var existingSessionId))
            return new AnthropicManagedAgentSessionResolution(existingSessionId, false, null);

        var rawSession = await CreateManagedAgentSessionAsync(request, target, cancellationToken);
        var sessionId = TryGetString(rawSession, "id")
                        ?? throw new InvalidOperationException("Anthropic managed-agent session create response did not include an id.");

        return new AnthropicManagedAgentSessionResolution(sessionId, true, rawSession);
    }

    private async Task<JsonElement> CreateManagedAgentSessionAsync(
        AIRequest request,
        AnthropicManagedAgentTarget target,
        CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object?>
        {
            ["agent"] = BuildManagedAgentReference(request, target),
            ["environment_id"] = target.EnvironmentId
        };

        var title = request.Metadata?.GetProviderOption<string>(GetIdentifier(), "title");
        if (!string.IsNullOrWhiteSpace(title))
            body["title"] = title;

        var vaultIds = request.Metadata?.GetProviderOption<List<string>>(GetIdentifier(), "vault_ids")
                       ?? request.Metadata?.GetProviderOption<string[]>(GetIdentifier(), "vault_ids")?.ToList();
        if (vaultIds?.Count > 0)
            body["vault_ids"] = vaultIds;

        var sessionMetadata = request.Metadata?.GetProviderOption<Dictionary<string, object?>>(GetIdentifier(), "session_metadata")
                              ?? request.Metadata?.GetProviderOption<Dictionary<string, object?>>(GetIdentifier(), "metadata");
        if (sessionMetadata?.Count > 0)
            body["metadata"] = sessionMetadata;

        return await SendManagedAgentsJsonAsync(
            HttpMethod.Post,
            ManagedAgentSessionsEndpoint,
            body,
            "Anthropic managed-agent create session",
            cancellationToken);
    }

    private object BuildManagedAgentReference(AIRequest request, AnthropicManagedAgentTarget target)
    {
        var version = request.Metadata?.GetProviderOption<int?>(GetIdentifier(), "agent_version");
        if (version is not > 0)
            return target.AgentId;

        return new Dictionary<string, object?>
        {
            ["type"] = "agent",
            ["id"] = target.AgentId,
            ["version"] = version.Value
        };
    }

    private async Task<string> SendManagedAgentUserMessageAsync(
        string sessionId,
        string text,
        CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object?>
        {
            ["events"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "user.message",
                    ["content"] = new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["type"] = "text",
                            ["text"] = text
                        }
                    }
                }
            }
        };

        var response = await SendManagedAgentsJsonAsync(
            HttpMethod.Post,
            $"{ManagedAgentSessionsEndpoint}/{Uri.EscapeDataString(sessionId)}/events",
            body,
            "Anthropic managed-agent send events",
            cancellationToken);

        if (!TryGetProperty(response, "data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Anthropic managed-agent send events response did not include sent events.");

        var sentEventId = data.EnumerateArray()
            .Select(item => TryGetString(item, "id"))
            .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));

        return sentEventId
               ?? throw new InvalidOperationException("Anthropic managed-agent send events response did not include a sent event id.");
    }

    private async Task<List<JsonElement>> WaitForManagedAgentTurnEventsAsync(
        string sessionId,
        string sentEventId,
        CancellationToken cancellationToken)
        => await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: ct => ListManagedAgentEventsAfterAsync(sessionId, sentEventId, ct),
            isTerminal: events => events.Any(IsManagedAgentTerminalEvent),
            interval: ManagedAgentPollInterval,
            timeout: ManagedAgentPollTimeout,
            maxAttempts: null,
            cancellationToken: cancellationToken);

    private async Task<List<JsonElement>> ListManagedAgentEventsAfterAsync(
        string sessionId,
        string sentEventId,
        CancellationToken cancellationToken)
    {
        var allEvents = await ListAllManagedAgentEventsAsync(sessionId, cancellationToken);
        var filtered = new List<JsonElement>();
        var markerSeen = false;

        foreach (var managedAgentEvent in allEvents)
        {
            var eventId = TryGetString(managedAgentEvent, "id");
            if (!markerSeen)
            {
                markerSeen = string.Equals(eventId, sentEventId, StringComparison.Ordinal);
                continue;
            }

            filtered.Add(managedAgentEvent);
        }

        return markerSeen ? filtered : [];
    }

    private async Task<List<JsonElement>> ListAllManagedAgentEventsAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        var results = new List<JsonElement>();
        string? page = null;

        while (true)
        {
            var uri = page is null
                ? $"{ManagedAgentSessionsEndpoint}/{Uri.EscapeDataString(sessionId)}/events?order=asc&limit={ManagedAgentEventsPageSize}"
                : $"{ManagedAgentSessionsEndpoint}/{Uri.EscapeDataString(sessionId)}/events?order=asc&limit={ManagedAgentEventsPageSize}&page={Uri.EscapeDataString(page)}";

            var root = await SendManagedAgentsJsonAsync(
                HttpMethod.Get,
                uri,
                operation: "Anthropic managed-agent list events",
                cancellationToken: cancellationToken);

            if (!TryGetProperty(root, "data", out var data) || data.ValueKind != JsonValueKind.Array)
                break;

            results.AddRange(data.EnumerateArray().Select(static item => item.Clone()));

            page = TryGetString(root, "next_page");
            if (string.IsNullOrWhiteSpace(page))
                break;
        }

        return results;
    }

    private async Task<JsonElement> RetrieveManagedAgentSessionAsync(string sessionId, CancellationToken cancellationToken)
        => await SendManagedAgentsJsonAsync(
            HttpMethod.Get,
            $"{ManagedAgentSessionsEndpoint}/{Uri.EscapeDataString(sessionId)}",
            operation: "Anthropic managed-agent retrieve session",
            cancellationToken: cancellationToken);

    private async Task<JsonElement> SendManagedAgentsJsonAsync(
        HttpMethod method,
        string uri,
        object? payload = null,
        string operation = "Anthropic managed-agent request",
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));
        request.Headers.TryAddWithoutValidation(betaKey, ManagedAgentsBeta);

        if (payload is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonSerializerOptions.Web),
                Encoding.UTF8,
                MediaTypeNames.Application.Json);
        }

        using var response = await _client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{operation} failed with status {(int)response.StatusCode}: {body}");

        if (string.IsNullOrWhiteSpace(body))
            return JsonSerializer.SerializeToElement(new { }, JsonSerializerOptions.Web);

        return JsonSerializer.Deserialize<JsonElement>(body, JsonSerializerOptions.Web).Clone();
    }

    private bool TryResolveManagedAgentTarget(string? model, out AnthropicManagedAgentTarget target)
    {
        target = default!;

        var normalized = NormalizeAnthropicManagedAgentModelId(model);
        if (!normalized.StartsWith(ManagedAgentModelPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3)
            return false;

        if (string.IsNullOrWhiteSpace(parts[1]) || string.IsNullOrWhiteSpace(parts[2]))
            return false;

        target = new AnthropicManagedAgentTarget(parts[1], parts[2]);
        return true;
    }

    private string NormalizeAnthropicManagedAgentModelId(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return string.Empty;

        var trimmed = model.Trim();
        var providerPrefix = GetIdentifier() + "/";

        return trimmed.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed.SplitModelId().Model
            : trimmed;
    }

    private bool TryFindManagedAgentSessionId(
        AIRequest request,
        AnthropicManagedAgentTarget target,
        out string sessionId)
    {
        sessionId = request.Metadata?.GetProviderOption<string>(GetIdentifier(), "sessionId")
                    ?? request.Metadata?.GetProviderOption<string>(GetIdentifier(), "session_id")
                    ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(sessionId))
            return true;

        foreach (var item in request.Input?.Items ?? [])
        {
            foreach (var toolPart in item.Content?.OfType<AIToolCallContentPart>() ?? [])
            {
                if (toolPart.ProviderExecuted != true)
                    continue;

                if (TryExtractManagedAgentSession(toolPart.Output, target, out sessionId))
                    return true;

                if (TryExtractManagedAgentSession(toolPart.Metadata, target, out sessionId))
                    return true;
            }
        }

        sessionId = string.Empty;
        return false;
    }

    private bool TryExtractManagedAgentSession(
        object? value,
        AnthropicManagedAgentTarget target,
        out string sessionId)
    {
        sessionId = string.Empty;
        if (value is null)
            return false;

        var element = value is JsonElement json
            ? json
            : JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);

        if (element.ValueKind != JsonValueKind.Object)
            return false;

        if (TryGetProperty(element, "structuredContent", out var structuredContent))
            element = structuredContent;

        if (TryGetProperty(element, GetIdentifier(), out var providerScoped) && providerScoped.ValueKind == JsonValueKind.Object)
        {
            if (TryExtractManagedAgentSession(providerScoped, target, out sessionId))
                return true;
        }

        if (TryGetProperty(element, "session", out var session) && session.ValueKind == JsonValueKind.Object)
        {
            if (TryExtractManagedAgentSession(session, target, out sessionId))
                return true;
        }

        var extractedSessionId = TryGetString(element, "sessionId") ?? TryGetString(element, "session_id");
        if (string.IsNullOrWhiteSpace(extractedSessionId))
            return false;

        var agentId = TryGetString(element, "agentId") ?? TryGetString(element, "agent_id");
        var environmentId = TryGetString(element, "environmentId") ?? TryGetString(element, "environment_id");

        if (!string.IsNullOrWhiteSpace(agentId)
            && !string.Equals(agentId, target.AgentId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(environmentId)
            && !string.Equals(environmentId, target.EnvironmentId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        sessionId = extractedSessionId;
        return true;
    }

    private static string? ExtractLatestManagedAgentUserText(AIRequest request)
    {
        foreach (var item in (request.Input?.Items ?? []).AsEnumerable().Reverse())
        {
            if (!string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))
                continue;

            var text = string.Join(
                "\n",
                item.Content?.OfType<AITextContentPart>().Select(part => part.Text).Where(static value => !string.IsNullOrWhiteSpace(value))
                ?? []);

            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return null;
    }

    private AnthropicManagedAgentTurnSnapshot BuildManagedAgentTurnSnapshot(IEnumerable<JsonElement> events)
    {
        var snapshot = new AnthropicManagedAgentTurnSnapshot();

        foreach (var managedAgentEvent in events)
        {
            var type = TryGetString(managedAgentEvent, "type");

            switch (type)
            {
                case "agent.message":
                    var text = ExtractManagedAgentMessageText(managedAgentEvent);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        snapshot.Entries.Add(new AnthropicManagedAgentTextEntry
                        {
                            Text = text,
                            RawEvent = managedAgentEvent.Clone()
                        });
                    }
                    break;

                case "agent.tool_use":
                case "agent.mcp_tool_use":
                    if (!TryCreateManagedAgentToolEntry(managedAgentEvent, snapshot.ToolEntries, out var toolEntry))
                        break;

                    if (!snapshot.Entries.Contains(toolEntry))
                        snapshot.Entries.Add(toolEntry);
                    break;

                case "agent.tool_result":
                case "agent.mcp_tool_result":
                    ApplyManagedAgentToolResult(managedAgentEvent, snapshot.ToolEntries, snapshot.Entries);
                    break;

                case "session.status_idle":
                    snapshot.Status = ResolveManagedAgentIdleStatus(managedAgentEvent);
                    snapshot.TerminalEvent = managedAgentEvent.Clone();
                    break;

                case "session.status_terminated":
                    snapshot.Status = "failed";
                    snapshot.TerminalEvent = managedAgentEvent.Clone();
                    break;

                case "session.error":
                    snapshot.ErrorEvent = managedAgentEvent.Clone();
                    break;
            }
        }

        return snapshot;
    }

    private AIOutputItem CreateManagedAgentOutputItem(object entry)
        => entry switch
        {
            AnthropicManagedAgentTextEntry textEntry => new AIOutputItem
            {
                Type = "message",
                Role = "assistant",
                Content =
                [
                    new AITextContentPart
                    {
                        Type = "text",
                        Text = textEntry.Text,
                        Metadata = textEntry.RawEvent.HasValue
                            ? new Dictionary<string, object?> { ["anthropic.raw"] = textEntry.RawEvent.Value.Clone() }
                            : null
                    }
                ]
            },
            AnthropicManagedAgentToolEntry toolEntry => new AIOutputItem
            {
                Type = "message",
                Role = "assistant",
                Content =
                [
                    new AIToolCallContentPart
                    {
                        Type = "tool-call",
                        ToolCallId = toolEntry.ToolCallId,
                        ToolName = toolEntry.ToolName,
                        Title = toolEntry.Title,
                        Input = toolEntry.Input,
                        Output = toolEntry.Output,
                        ProviderExecuted = toolEntry.ProviderExecuted,
                        State = toolEntry.State,
                        Metadata = toolEntry.Metadata
                    }
                ]
            },
            _ => new AIOutputItem { Type = "message", Role = "assistant" }
        };

    private AIOutputItem CreateManagedAgentSessionToolOutputItem(
        string sessionId,
        AnthropicManagedAgentTarget target,
        JsonElement rawSession)
        => new()
        {
            Type = "message",
            Role = "assistant",
            Content =
            [
                new AIToolCallContentPart
                {
                    Type = "tool-call",
                    ToolCallId = BuildManagedAgentSessionToolCallId(sessionId),
                    ToolName = ManagedAgentSessionToolName,
                    Title = "Create Anthropic managed-agent session",
                    Input = JsonSerializer.SerializeToElement(new
                    {
                        agent = target.AgentId,
                        environment_id = target.EnvironmentId
                    }, JsonSerializerOptions.Web),
                    Output = CreateManagedAgentSessionToolResult(sessionId, target, rawSession),
                    ProviderExecuted = true,
                    State = "output-available",
                    Metadata = CreateManagedAgentSessionToolMetadata(sessionId, target, rawSession)
                }
            ]
        };

    private IEnumerable<AIStreamEvent> CreateManagedAgentSessionToolEvents(
        string sessionId,
        AnthropicManagedAgentTarget target,
        JsonElement rawSession,
        DateTimeOffset timestamp)
    {
        var toolCallId = BuildManagedAgentSessionToolCallId(sessionId);
        var providerMetadata = CreateManagedAgentProviderMetadata(new Dictionary<string, object>
        {
            ["type"] = ManagedAgentSessionToolName,
            ["sessionId"] = sessionId,
            ["agentId"] = target.AgentId,
            ["environmentId"] = target.EnvironmentId,
            ["tool_name"] = ManagedAgentSessionToolName,
            ["title"] = "Create Anthropic managed-agent session"
        });

        yield return CreateManagedAgentStreamEvent(
            "tool-input-available",
            toolCallId,
            new AIToolInputAvailableEventData
            {
                ToolName = ManagedAgentSessionToolName,
                Title = "Create Anthropic managed-agent session",
                Input = JsonSerializer.SerializeToElement(new
                {
                    agent = target.AgentId,
                    environment_id = target.EnvironmentId
                }, JsonSerializerOptions.Web),
                ProviderExecuted = true,
                ProviderMetadata = providerMetadata
            },
            timestamp,
            null);

        yield return CreateManagedAgentStreamEvent(
            "tool-output-available",
            toolCallId,
            new AIToolOutputAvailableEventData
            {
                ToolName = ManagedAgentSessionToolName,
                ProviderExecuted = true,
                Output = CreateManagedAgentSessionToolResult(sessionId, target, rawSession),
                ProviderMetadata = providerMetadata
            },
            timestamp,
            null);
    }

    private IEnumerable<AIStreamEvent> CreateManagedAgentStreamEvents(
        JsonElement managedAgentEvent,
        string model,
        AnthropicManagedAgentStreamState state)
    {
        var type = TryGetString(managedAgentEvent, "type");
        var timestamp = ExtractManagedAgentTimestamp(managedAgentEvent);

        switch (type)
        {
            case "agent.message":
                var text = ExtractManagedAgentMessageText(managedAgentEvent);
                if (string.IsNullOrWhiteSpace(text))
                    yield break;

                var textEventId = TryGetString(managedAgentEvent, "id") ?? Guid.NewGuid().ToString("N");
                var providerMetadata = new Dictionary<string, object>
                {
                    ["raw"] = managedAgentEvent.Clone()
                };

                yield return CreateManagedAgentStreamEvent(
                    "text-start",
                    textEventId,
                    new AITextStartEventData { ProviderMetadata = providerMetadata },
                    timestamp,
                    null);

                yield return CreateManagedAgentStreamEvent(
                    "text-delta",
                    textEventId,
                    new AITextDeltaEventData
                    {
                        Delta = text,
                        ProviderMetadata = providerMetadata
                    },
                    timestamp,
                    null);

                yield return CreateManagedAgentStreamEvent(
                    "text-end",
                    textEventId,
                    new AITextEndEventData { ProviderMetadata = providerMetadata },
                    timestamp,
                    null);
                yield break;

            case "agent.tool_use":
            case "agent.mcp_tool_use":
                if (!TryCreateManagedAgentToolEntry(managedAgentEvent, state.ToolEntries, out var toolEntry))
                    yield break;

                yield return CreateManagedAgentStreamEvent(
                    "tool-input-available",
                    toolEntry.ToolCallId,
                    new AIToolInputAvailableEventData
                    {
                        ToolName = toolEntry.ToolName ?? "unknown",
                        Title = toolEntry.Title,
                        Input = toolEntry.Input ?? JsonSerializer.SerializeToElement(new { }, JsonSerializerOptions.Web),
                        ProviderExecuted = true,
                        ProviderMetadata = CreateManagedAgentProviderMetadata(ToNonNullDictionary(toolEntry.Metadata))
                    },
                    timestamp,
                    null);
                yield break;

            case "agent.tool_result":
            case "agent.mcp_tool_result":
                ApplyManagedAgentToolResult(managedAgentEvent, state.ToolEntries, null);

                var toolUseId = TryGetString(managedAgentEvent, "tool_use_id")
                                ?? TryGetString(managedAgentEvent, "mcp_tool_use_id");
                if (string.IsNullOrWhiteSpace(toolUseId)
                    || !state.ToolEntries.TryGetValue(toolUseId, out var updatedToolEntry))
                {
                    yield break;
                }

                yield return CreateManagedAgentStreamEvent(
                    "tool-output-available",
                    updatedToolEntry.ToolCallId,
                    new AIToolOutputAvailableEventData
                    {
                        ToolName = updatedToolEntry.ToolName,
                        Output = updatedToolEntry.Output ?? JsonSerializer.SerializeToElement(new { }, JsonSerializerOptions.Web),
                        ProviderExecuted = true,
                        ProviderMetadata = CreateManagedAgentProviderMetadata(ToNonNullDictionary(updatedToolEntry.Metadata))
                    },
                    timestamp,
                    null);
                yield break;

            case "session.error":
                yield return CreateManagedAgentStreamEvent(
                    "error",
                    TryGetString(managedAgentEvent, "id"),
                    new AIErrorEventData
                    {
                        ErrorText = ExtractManagedAgentErrorText(managedAgentEvent) ?? "Anthropic managed-agent session error."
                    },
                    timestamp,
                    new Dictionary<string, object?>
                    {
                        ["anthropic.raw"] = managedAgentEvent.Clone(),
                        ["anthropic.managed_agent.event_type"] = type
                    });
                yield break;
        }
    }

    private static bool TryCreateManagedAgentToolEntry(
        JsonElement managedAgentEvent,
        Dictionary<string, AnthropicManagedAgentToolEntry> toolEntries,
        out AnthropicManagedAgentToolEntry toolEntry)
    {
        var toolCallId = TryGetString(managedAgentEvent, "id");
        if (string.IsNullOrWhiteSpace(toolCallId))
        {
            toolEntry = null!;
            return false;
        }

        if (toolEntries.TryGetValue(toolCallId, out toolEntry!))
            return true;

        var type = TryGetString(managedAgentEvent, "type") ?? string.Empty;
        var toolName = TryGetString(managedAgentEvent, "name");
        var metadata = new Dictionary<string, object?>
        {
            ["type"] = type,
            ["tool_use_id"] = toolCallId,
            ["tool_name"] = toolName,
            ["title"] = toolName,
            ["raw"] = managedAgentEvent.Clone()
        };

        var serverName = TryGetString(managedAgentEvent, "mcp_server_name");
        if (!string.IsNullOrWhiteSpace(serverName))
            metadata["server_name"] = serverName;

        toolEntry = new AnthropicManagedAgentToolEntry
        {
            ToolCallId = toolCallId,
            ToolName = toolName,
            Title = toolName,
            Input = TryGetProperty(managedAgentEvent, "input", out var input)
                ? input.Clone()
                : JsonSerializer.SerializeToElement(new { }, JsonSerializerOptions.Web),
            Metadata = metadata
        };

        toolEntries[toolCallId] = toolEntry;
        return true;
    }

    private static void ApplyManagedAgentToolResult(
        JsonElement managedAgentEvent,
        Dictionary<string, AnthropicManagedAgentToolEntry> toolEntries,
        List<object>? orderedEntries)
    {
        var toolUseId = TryGetString(managedAgentEvent, "tool_use_id")
                        ?? TryGetString(managedAgentEvent, "mcp_tool_use_id");
        if (string.IsNullOrWhiteSpace(toolUseId))
            return;

        if (!toolEntries.TryGetValue(toolUseId, out var toolEntry))
        {
            var type = TryGetString(managedAgentEvent, "type") ?? string.Empty;
            toolEntry = new AnthropicManagedAgentToolEntry
            {
                ToolCallId = toolUseId,
                ToolName = null,
                Title = null,
                Input = JsonSerializer.SerializeToElement(new { }, JsonSerializerOptions.Web),
                Metadata = new Dictionary<string, object?> { ["type"] = type }
            };
            toolEntries[toolUseId] = toolEntry;
            orderedEntries?.Add(toolEntry);
        }

        var metadata = toolEntry.Metadata ??= new Dictionary<string, object?>();
        metadata["raw"] = managedAgentEvent.Clone();
        metadata["tool_use_id"] = toolUseId;

        if (TryGetBool(managedAgentEvent, "is_error") is bool isError)
            metadata["is_error"] = isError;

        toolEntry.Output = BuildManagedAgentToolResult(managedAgentEvent);
        toolEntry.State = TryGetBool(managedAgentEvent, "is_error") == true ? "output-error" : "output-available";
    }

    private static object BuildManagedAgentToolResult(JsonElement managedAgentEvent)
    {
        var payload = new Dictionary<string, object?>
        {
            ["type"] = TryGetString(managedAgentEvent, "type")
        };

        if (TryGetProperty(managedAgentEvent, "content", out var content))
            payload["content"] = content.Clone();

        if (TryGetBool(managedAgentEvent, "is_error") is bool isError)
            payload["is_error"] = isError;

        return JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web);
    }

    private static string? ExtractManagedAgentMessageText(JsonElement managedAgentEvent)
    {
        if (!TryGetProperty(managedAgentEvent, "content", out var content) || content.ValueKind != JsonValueKind.Array)
            return null;

        var texts = content.EnumerateArray()
            .Where(static block => string.Equals(TryGetString(block, "type"), "text", StringComparison.OrdinalIgnoreCase))
            .Select(block => TryGetString(block, "text"))
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .ToList();

        return texts.Count == 0 ? null : string.Join("\n", texts);
    }

    private static string ResolveManagedAgentIdleStatus(JsonElement idleEvent)
    {
        if (!TryGetProperty(idleEvent, "stop_reason", out var stopReason) || stopReason.ValueKind != JsonValueKind.Object)
            return "completed";

        return TryGetString(stopReason, "type")?.Trim().ToLowerInvariant() switch
        {
            "requires_action" => "in_progress",
            "retries_exhausted" => "failed",
            _ => "completed"
        };
    }

    private static bool IsManagedAgentTerminalEvent(JsonElement managedAgentEvent)
        => (TryGetString(managedAgentEvent, "type") ?? string.Empty) switch
        {
            "session.status_idle" => true,
            "session.status_terminated" => true,
            _ => false
        };

    private static string ResolveManagedAgentFinishReason(string status)
        => status.Trim().ToLowerInvariant() switch
        {
            "failed" => "error",
            "in_progress" => "other",
            _ => "stop"
        };

    private AIStreamEvent CreateManagedAgentFinishEvent(
        string sessionId,
        string model,
        JsonElement session,
        JsonElement? terminalEvent,
        JsonElement? errorEvent,
        DateTimeOffset timestamp)
    {
        var status = terminalEvent.HasValue
            ? ResolveManagedAgentTerminalStatus(terminalEvent.Value)
            : TryGetString(session, "status") ?? "completed";

        var usage = TryGetObjectProperty(session, "usage");
        var providerAdditionalProperties = new Dictionary<string, object?>
        {
            [GetIdentifier()] = new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId,
                ["session"] = session.Clone(),
                ["status"] = status,
                ["terminalEvent"] = terminalEvent?.Clone(),
                ["errorEvent"] = errorEvent?.Clone()
            }
        };

        return CreateManagedAgentStreamEvent(
            "finish",
            sessionId,
            new AIFinishEventData
            {
                FinishReason = ResolveManagedAgentFinishReason(status),
                Model = model,
                CompletedAt = timestamp.ToUnixTimeSeconds(),
                MessageMetadata = AIFinishMessageMetadata.Create(model, timestamp, usage, additionalProperties: providerAdditionalProperties)
            },
            timestamp,
            CreateManagedAgentResponseMetadata(sessionId, null, session, errorEvent));
    }

    private string ResolveManagedAgentTerminalStatus(JsonElement terminalEvent)
    {
        var type = TryGetString(terminalEvent, "type");
        if (string.Equals(type, "session.status_terminated", StringComparison.OrdinalIgnoreCase))
            return "failed";

        return ResolveManagedAgentIdleStatus(terminalEvent);
    }

    private AIStreamEvent CreateManagedAgentStreamEvent(
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

    private Dictionary<string, object?> CreateManagedAgentSessionToolMetadata(
        string sessionId,
        AnthropicManagedAgentTarget target,
        JsonElement rawSession)
    {
        var providerPayload = new Dictionary<string, object?>
        {
            ["type"] = ManagedAgentSessionToolName,
            ["sessionId"] = sessionId,
            ["session_id"] = sessionId,
            ["agentId"] = target.AgentId,
            ["agent_id"] = target.AgentId,
            ["environmentId"] = target.EnvironmentId,
            ["environment_id"] = target.EnvironmentId,
            ["tool_name"] = ManagedAgentSessionToolName,
            ["title"] = "Create Anthropic managed-agent session",
            ["raw"] = rawSession.Clone()
        };

        return new Dictionary<string, object?>
        {
            [GetIdentifier()] = JsonSerializer.SerializeToElement(providerPayload, JsonSerializerOptions.Web),
            ["type"] = ManagedAgentSessionToolName,
            ["sessionId"] = sessionId,
            ["session_id"] = sessionId,
            ["agentId"] = target.AgentId,
            ["environmentId"] = target.EnvironmentId,
            ["tool_name"] = ManagedAgentSessionToolName
        };
    }

    private static CallToolResult CreateManagedAgentSessionToolResult(
        string sessionId,
        AnthropicManagedAgentTarget target,
        JsonElement rawSession)
        => new()
        {
            Content = [],
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                sessionId,
                session_id = sessionId,
                agentId = target.AgentId,
                agent_id = target.AgentId,
                environmentId = target.EnvironmentId,
                environment_id = target.EnvironmentId,
                session = rawSession.Clone()
            }, JsonSerializerOptions.Web)
        };

    private static string BuildManagedAgentSessionToolCallId(string sessionId)
        => $"anthropic-create-session-{sessionId}";

    private static string GetManagedAgentDisplayName(AnthropicManagedAgentDefinition agent)
        => string.IsNullOrWhiteSpace(agent.Name) ? agent.Id : agent.Name!;

    private static string GetManagedAgentEnvironmentDisplayName(AnthropicManagedAgentEnvironmentDefinition environment)
        => string.IsNullOrWhiteSpace(environment.Name) ? environment.Id : environment.Name!;

    private static string BuildManagedAgentModelDescription(
        AnthropicManagedAgentDefinition agent,
        AnthropicManagedAgentEnvironmentDefinition environment)
    {
        var agentLabel = GetManagedAgentDisplayName(agent);
        var environmentLabel = GetManagedAgentEnvironmentDisplayName(environment);
        var details = new List<string>
        {
            $"Anthropic managed agent '{agentLabel}' in environment '{environmentLabel}'."
        };

        if (!string.IsNullOrWhiteSpace(agent.Description))
            details.Add(agent.Description!);

        if (!string.IsNullOrWhiteSpace(environment.Description))
            details.Add($"Environment: {environment.Description}");

        if (!string.IsNullOrWhiteSpace(agent.ModelId))
            details.Add($"Backed by {agent.ModelId}.");

        return string.Join(" ", details);
    }

    private static IEnumerable<string> BuildManagedAgentModelTags(
        AnthropicManagedAgentDefinition agent,
        AnthropicManagedAgentEnvironmentDefinition environment)
    {
        var tags = new List<string>
        {
            "agent",
            "managed-agent",
            "shortcut",
            $"agent:{agent.Id}",
            $"environment:{environment.Id}"
        };

        if (!string.IsNullOrWhiteSpace(agent.ModelId))
            tags.Add(agent.ModelId!);

        return tags;
    }

    private static void AddManagedAgentModelIfMissing(List<Model> models, Model model)
    {
        if (models.Any(existing => string.Equals(existing.Id, model.Id, StringComparison.OrdinalIgnoreCase)))
            return;

        models.Add(model);
    }

    private static DateTimeOffset ExtractManagedAgentTimestamp(JsonElement managedAgentEvent)
    {
        if (TryGetDateTimeOffset(managedAgentEvent, "processed_at") is { } processedAt)
            return processedAt;

        if (TryGetDateTimeOffset(managedAgentEvent, "created_at") is { } createdAt)
            return createdAt;

        return DateTimeOffset.UtcNow;
    }

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement element, string propertyName)
    {
        var value = TryGetString(element, propertyName);
        return DateTimeOffset.TryParse(value, out var parsed)
            ? parsed
            : null;
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

    private static string? ExtractManagedAgentErrorText(JsonElement managedAgentEvent)
    {
        if (!TryGetProperty(managedAgentEvent, "error", out var error) || error.ValueKind != JsonValueKind.Object)
            return null;

        return TryGetString(error, "message");
    }

    private Dictionary<string, object?> CreateManagedAgentResponseMetadata(
        string sessionId,
        AnthropicManagedAgentTarget? target,
        JsonElement session,
        JsonElement? errorEvent)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["anthropic.sessionId"] = sessionId,
            ["anthropic.session"] = session.Clone(),
            ["anthropic.managed_agent.beta"] = ManagedAgentsBeta,
            ["anthropic.managed_agent.status"] = TryGetString(session, "status")
        };

        if (target is not null)
        {
            metadata["anthropic.managed_agent.agent_id"] = target.AgentId;
            metadata["anthropic.managed_agent.environment_id"] = target.EnvironmentId;
        }

        if (errorEvent.HasValue)
            metadata["anthropic.managed_agent.error"] = errorEvent.Value.Clone();

        return metadata;
    }

    private Dictionary<string, Dictionary<string, object>> CreateManagedAgentProviderMetadata(Dictionary<string, object> metadata)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            [GetIdentifier()] = metadata
        };

    private static Dictionary<string, object> ToNonNullDictionary(Dictionary<string, object?>? metadata)
        => metadata?
            .Where(static item => item.Value is not null)
            .ToDictionary(static item => item.Key, static item => item.Value!, StringComparer.OrdinalIgnoreCase)
           ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
}
