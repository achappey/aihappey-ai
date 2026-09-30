using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Abstractions.Http;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;
using Microsoft.AspNetCore.StaticFiles;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.OpenAI;

public partial class OpenAIProvider
{
    private const string AgentsBetaHeaderName = "OpenAI-Beta";
    private const string AgentsBetaHeaderValue = "agents=v1";
    private const string AgentModelPrefix = "agent/";
    private const string AgentSessionToolName = "create_openai_agent_session";
    private const string AgentsEndpoint = "v1/agents";
    private const string AgentSessionsEndpoint = "v1/agents/sessions";
    private const int AgentPageSize = 100;

    private sealed record OpenAiAgentTarget(string LocalModelId, string? AgentId = null);

    private sealed record OpenAiAgentSessionResolution(
        string? SessionId,
        string? EnvironmentId,
        bool Created);

    private sealed record OpenAiAgentArtifact(
        string Id,
        string Path,
        string TurnId,
        string EnvironmentId,
        long SizeBytes,
        long CreatedAt,
        byte[] Content,
        string MediaType);

    private sealed class OpenAiAgentStreamState
    {
        public HashSet<string> SeenEventIds { get; } = new(StringComparer.Ordinal);
        public HashSet<string> StartedTextItems { get; } = new(StringComparer.Ordinal);
        public HashSet<string> CompletedTextItems { get; } = new(StringComparer.Ordinal);
        public HashSet<string> StartedReasoningItems { get; } = new(StringComparer.Ordinal);
        public HashSet<string> CompletedReasoningItems { get; } = new(StringComparer.Ordinal);
        public HashSet<string> EmittedToolInputs { get; } = new(StringComparer.Ordinal);
        public HashSet<string> EmittedToolOutputs { get; } = new(StringComparer.Ordinal);
        public HashSet<string> EmittedArtifactIds { get; } = new(StringComparer.Ordinal);
        public HashSet<string> EmittedApprovalIds { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> FunctionTurnIds { get; } = new(StringComparer.Ordinal);
        public string? SessionId { get; set; }
        public string? EnvironmentId { get; set; }
        public string? EnvironmentType { get; set; }
        public string? TurnId { get; set; }
        public JsonElement? Session { get; set; }
        public JsonElement? Usage { get; set; }
        public JsonElement? TerminalEvent { get; set; }
        public JsonElement? Error { get; set; }
        public string Status { get; set; } = "in_progress";
        public bool RequiresAction { get; set; }
    }

    private bool TryResolveOpenAiAgentTarget(string? model, out OpenAiAgentTarget target)
    {
        target = default!;
        if (string.IsNullOrWhiteSpace(model))
            return false;

        var normalized = model.Trim();
        var providerPrefix = GetIdentifier() + "/";
        if (normalized.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            normalized = normalized.SplitModelId().Model;

        if (!normalized.StartsWith(AgentModelPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var agentId = normalized[AgentModelPrefix.Length..].Trim('/').Trim();
        if (string.IsNullOrWhiteSpace(agentId) || agentId.Contains('/'))
            return false;

        target = new OpenAiAgentTarget($"{AgentModelPrefix}{agentId}", agentId);
        return true;
    }

    private bool TryResolveOpenAiAgentTarget(AIRequest request, out OpenAiAgentTarget target)
    {
        if (TryResolveOpenAiAgentTarget(request.Model, out target))
            return true;

        target = default!;
        if (string.IsNullOrWhiteSpace(request.Model))
            return false;

        var model = request.Model.Trim();
        var providerPrefix = GetIdentifier() + "/";
        if (model.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            model = model.SplitModelId().Model;

        if (string.IsNullOrWhiteSpace(model) || model.GuessModelType() != "language"
            || GetOpenAiProviderOption<JsonElement?>(request.Metadata, "agent") is not { ValueKind: JsonValueKind.Object })
            return false;

        target = new OpenAiAgentTarget(model);
        return true;
    }

    private async Task<AIResponse> ExecuteOpenAiAgentUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken)
    {
        var output = new List<AIOutputItem>();
        var text = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        var reasoning = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        var tools = new Dictionary<string, AIToolCallContentPart>(StringComparer.Ordinal);
        Dictionary<string, object?>? responseMetadata = null;
        object? usage = null;
        string status = "completed";

        await foreach (var streamEvent in StreamOpenAiAgentUnifiedAsync(request, cancellationToken)
                           .WithCancellation(cancellationToken))
        {
            var id = streamEvent.Event.Id ?? Guid.NewGuid().ToString("N");
            switch (streamEvent.Event.Type)
            {
                case "text-start":
                    text.TryAdd(id, new StringBuilder());
                    break;
                case "text-delta" when streamEvent.Event.Data is AITextDeltaEventData delta:
                    if (!text.TryGetValue(id, out var textBuffer))
                        text[id] = textBuffer = new StringBuilder();
                    textBuffer.Append(delta.Delta);
                    break;
                case "reasoning-start":
                    reasoning.TryAdd(id, new StringBuilder());
                    break;
                case "reasoning-delta" when streamEvent.Event.Data is AIReasoningDeltaEventData delta:
                    if (!reasoning.TryGetValue(id, out var reasoningBuffer))
                        reasoning[id] = reasoningBuffer = new StringBuilder();
                    reasoningBuffer.Append(delta.Delta);
                    break;
                case "tool-input-available" when streamEvent.Event.Data is AIToolInputAvailableEventData toolInput:
                    tools[id] = new AIToolCallContentPart
                    {
                        Type = "tool-call",
                        ToolCallId = id,
                        ToolName = toolInput.ToolName,
                        Title = toolInput.Title,
                        Input = toolInput.Input,
                        ProviderExecuted = toolInput.ProviderExecuted,
                        State = "input-available",
                        Metadata = FlattenProviderMetadata(toolInput.ProviderMetadata)
                    };
                    break;
                case "tool-output-available" when streamEvent.Event.Data is AIToolOutputAvailableEventData toolOutput:
                    if (!tools.TryGetValue(id, out var tool))
                    {
                        tool = new AIToolCallContentPart
                        {
                            Type = "tool-call",
                            ToolCallId = id,
                            ToolName = toolOutput.ToolName,
                            ProviderExecuted = toolOutput.ProviderExecuted,
                            State = "output-available",
                            Output = toolOutput.Output,
                            Metadata = FlattenProviderMetadata(toolOutput.ProviderMetadata)
                        };
                    }
                    else
                    {
                        tool = new AIToolCallContentPart
                        {
                            Type = "tool-call",
                            ToolCallId = tool.ToolCallId,
                            ToolName = tool.ToolName ?? toolOutput.ToolName,
                            Title = tool.Title,
                            Input = tool.Input,
                            Output = toolOutput.Output,
                            ProviderExecuted = tool.ProviderExecuted ?? toolOutput.ProviderExecuted,
                            State = "output-available",
                            Metadata = tool.Metadata
                        };
                    }
                    tools[id] = tool;
                    break;
                case "tool-approval-request" when streamEvent.Event.Data is AIToolApprovalRequestEventData approval
                    && tools.TryGetValue(id, out var approvalTool):
                    tools[id] = new AIToolCallContentPart
                    {
                        Type = "tool-call", ToolCallId = id, ToolName = approvalTool.ToolName,
                        Title = approvalTool.Title, Input = approvalTool.Input,
                        ProviderExecuted = true, State = "approval-requested",
                        Approval = new AIToolCallApproval { Id = approval.ApprovalId },
                        Metadata = approvalTool.Metadata
                    };
                    break;
                case "file" when streamEvent.Event.Data is AIFileEventData file:
                    output.Add(new AIOutputItem
                    {
                        Type = "file",
                        Role = "assistant",
                        Content =
                        [
                            new AIFileContentPart
                            {
                                Type = "file",
                                Filename = file.Filename,
                                MediaType = file.MediaType,
                                Data = file.Url,
                                Metadata = FlattenProviderMetadata(file.ProviderMetadata)
                            }
                        ]
                    });
                    break;
                case "finish" when streamEvent.Event.Data is AIFinishEventData finish:
                    usage = finish.MessageMetadata?.Usage;
                    status = finish.FinishReason switch
                    {
                        "error" => "failed",
                        "cancelled" => "cancelled",
                        "tool-calls" => "requires_action",
                        _ => "completed"
                    };
                    responseMetadata = streamEvent.Metadata;
                    break;
            }
        }

        foreach (var value in text.Values.Where(static value => value.Length > 0))
        {
            output.Add(new AIOutputItem
            {
                Type = "message",
                Role = "assistant",
                Content = [new AITextContentPart { Type = "text", Text = value.ToString() }]
            });
        }

        foreach (var value in reasoning.Values.Where(static value => value.Length > 0))
        {
            output.Add(new AIOutputItem
            {
                Type = "reasoning",
                Role = "assistant",
                Content = [new AIReasoningContentPart { Type = "reasoning", Text = value.ToString() }]
            });
        }

        output.AddRange(tools.Values.Select(tool => new AIOutputItem
        {
            Type = "message",
            Role = "assistant",
            Content = [tool]
        }));

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = request.Model?.ToModelId(GetIdentifier()),
            Status = status,
            Usage = usage,
            Output = output.Count == 0 ? null : new AIOutput { Items = output },
            Metadata = responseMetadata
        };
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamOpenAiAgentUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!TryResolveOpenAiAgentTarget(request, out var target))
            throw new InvalidOperationException("OpenAI agent target could not be resolved from the model id.");

        var state = new OpenAiAgentStreamState();
        var resolution = ResolveOpenAiAgentSession(request, target);
        state.SessionId = resolution.SessionId;
        state.EnvironmentId = resolution.EnvironmentId;
        var model = request.Model?.ToModelId(GetIdentifier()) ?? target.LocalModelId.ToModelId(GetIdentifier());
        HttpResponseMessage? streamResponse = null;

        if (resolution.Created)
        {
            var createBody = await BuildOpenAiAgentSessionBodyAsync(request, target, cancellationToken);
            state.EnvironmentType = TryGetOpenAiString(JsonSerializer.SerializeToElement(createBody["environment"], JsonSerializerOptions.Web), "type");
            createBody["stream"] = true;
            streamResponse = await SendOpenAiAgentStreamRequestAsync(
                HttpMethod.Post,
                AgentSessionsEndpoint,
                createBody,
                "OpenAI agent create session",
                cancellationToken);
        }
        else
        {
            var sessionId = resolution.SessionId!;
            var currentSession = await RetrieveOpenAiAgentSessionAsync(sessionId, cancellationToken);
            state.Session = currentSession;
            state.Status = TryGetOpenAiString(currentSession, "status") ?? state.Status;
            if (TryGetOpenAiProperty(currentSession, "environment", out var currentEnvironment))
            {
                state.EnvironmentId = TryGetOpenAiString(currentEnvironment, "id") ?? state.EnvironmentId;
                state.EnvironmentType = TryGetOpenAiString(currentEnvironment, "type") ?? state.EnvironmentType;
            }

            var computerEvents = BuildOpenAiComputerUseFollowUpEvents(request, currentSession);
            if (computerEvents.InternalForm is { } internalForm)
            {
                yield return internalForm;
                state.RequiresAction = true;
                state.Status = "requires_action";
                yield return CreateOpenAiAgentFinishEvent(model, target, state);
                yield break;
            }

            streamResponse = await SendOpenAiAgentStreamRequestAsync(
                HttpMethod.Get,
                $"{AgentSessionsEndpoint}/{Uri.EscapeDataString(sessionId)}/events",
                null,
                "OpenAI agent event stream",
                cancellationToken);

            var inputEvents = computerEvents.Events.Count > 0
                ? new List<object>() // Browser answers must never accompany an ordinary model message/tool result.
                : BuildOpenAiAgentFollowUpEvents(request, state);
            inputEvents.InsertRange(0, computerEvents.Events);
            if (inputEvents.Count > 0)
            {
                _ = await SendOpenAiAgentsJsonAsync(
                    HttpMethod.Post,
                    $"{AgentSessionsEndpoint}/{Uri.EscapeDataString(sessionId)}/events",
                    new Dictionary<string, object?> { ["events"] = inputEvents },
                    "OpenAI agent submit events",
                    cancellationToken,
                    sensitive: computerEvents.Events.Count > 0);
            }
        }

        await using var events = ReadOpenAiAgentSseEventsAsync(
                streamResponse,
                GetOpenAiAgentBackendCapture(request),
                cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        while (await events.MoveNextAsync())
        {
            foreach (var mapped in MapOpenAiAgentEvent(events.Current, target, model, state))
                yield return mapped;

            if (state.RequiresAction && !state.TerminalEvent.HasValue && !string.IsNullOrWhiteSpace(state.SessionId))
            {
                var current = await RetrieveOpenAiAgentSessionAsync(state.SessionId!, cancellationToken);
                state.Session = current;
                state.Status = TryGetOpenAiString(current, "status") ?? state.Status;
                foreach (var mapped in MapOpenAiRequiredActions(current, state, DateTimeOffset.UtcNow))
                    yield return mapped;
                state.TerminalEvent = events.Current.Clone();
            }

            if (state.TerminalEvent.HasValue)
                break;
        }

        streamResponse.Dispose();

        if (!state.TerminalEvent.HasValue && !string.IsNullOrWhiteSpace(state.SessionId))
        {
            var persistedItems = await ListOpenAiAgentSessionItemsAsync(state.SessionId!, cancellationToken);
            foreach (var item in persistedItems)
            {
                var synthetic = JsonSerializer.SerializeToElement(new
                {
                    type = "agent.session.turn.item.done",
                    event_id = $"recovered-{TryGetOpenAiString(item, "id") ?? Guid.NewGuid().ToString("N")}",
                    session_id = state.SessionId,
                    turn_id = TryGetOpenAiString(item, "turn_id"),
                    item
                }, JsonSerializerOptions.Web);

                foreach (var mapped in MapOpenAiAgentEvent(synthetic, target, model, state))
                    yield return mapped;
            }

            var session = await RetrieveOpenAiAgentSessionAsync(state.SessionId!, cancellationToken);
            state.Session = session;
            state.Status = TryGetOpenAiString(session, "status") ?? state.Status;
            if (TryGetOpenAiProperty(session, "environment", out var restoredEnvironment))
                state.EnvironmentType = TryGetOpenAiString(restoredEnvironment, "type") ?? state.EnvironmentType;
            if (state.Status == "requires_action")
            {
                state.RequiresAction = true;
                foreach (var mapped in MapOpenAiRequiredActions(session, state, DateTimeOffset.UtcNow))
                    yield return mapped;
            }
        }

        if (!string.IsNullOrWhiteSpace(state.SessionId)
            && string.Equals(state.EnvironmentType, "openai_hosted", StringComparison.OrdinalIgnoreCase)
            && string.Equals(state.Status, "completed", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var artifact in await ListAndDownloadOpenAiAgentArtifactsAsync(state.SessionId!, state.TurnId, cancellationToken))
            {
                if (!state.EmittedArtifactIds.Add(artifact.Id))
                    continue;

                yield return CreateOpenAiAgentEvent(
                    "file",
                    artifact.Id,
                    new AIFileEventData
                    {
                        Filename = Path.GetFileName(artifact.Path),
                        MediaType = artifact.MediaType,
                        Url = $"data:{artifact.MediaType};base64,{Convert.ToBase64String(artifact.Content)}",
                        ProviderMetadata = CreateOpenAiNestedMetadata(new Dictionary<string, object>
                        {
                            ["artifact"] = JsonSerializer.SerializeToElement(artifact, JsonSerializerOptions.Web)
                        })
                    },
                    DateTimeOffset.FromUnixTimeSeconds(artifact.CreatedAt),
                    CreateOpenAiAgentMetadata(state, target));
            }
        }

        yield return CreateOpenAiAgentFinishEvent(model, target, state);
    }

    private OpenAiAgentSessionResolution ResolveOpenAiAgentSession(AIRequest request, OpenAiAgentTarget target)
    {
        var sessionId = GetOpenAiProviderOption<string>(request.Metadata, "sessionId")
                        ?? GetOpenAiProviderOption<string>(request.Metadata, "session_id");
        var environmentId = GetOpenAiProviderOption<string>(request.Metadata, "environmentId")
                            ?? GetOpenAiProviderOption<string>(request.Metadata, "environment_id");

        if (!string.IsNullOrWhiteSpace(sessionId))
            return new OpenAiAgentSessionResolution(sessionId, environmentId, false);

        foreach (var item in request.Input?.Items ?? [])
        {
            foreach (var tool in item.Content?.OfType<AIToolCallContentPart>() ?? [])
            {
                if (tool.ProviderExecuted != true || !TryExtractOpenAiAgentSession(tool.Output, target, out sessionId, out environmentId))
                    continue;

                return new OpenAiAgentSessionResolution(sessionId, environmentId, false);
            }
        }

        return new OpenAiAgentSessionResolution(null, null, true);
    }

    private async Task<Dictionary<string, object?>> BuildOpenAiAgentSessionBodyAsync(
        AIRequest request,
        OpenAiAgentTarget target,
        CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object?>
        {
            ["environment"] = BuildOpenAiAgentEnvironment(request),
            ["input"] = BuildOpenAiAgentInput(request),
            ["metadata"] = GetOpenAiProviderOption<object>(request.Metadata, "session_metadata")
                            ?? GetOpenAiProviderOption<object>(request.Metadata, "metadata")
                            ?? new Dictionary<string, string>()
        };

        if (target.AgentId is not null)
            body["agent_id"] = target.AgentId;

        if (TryGetOpenAiString(JsonSerializer.SerializeToElement(body["environment"], JsonSerializerOptions.Web), "type") == "none")
        {
            var input = JsonSerializer.SerializeToElement(body["input"], JsonSerializerOptions.Web);
            if ((input.ValueKind == JsonValueKind.Array && input.GetArrayLength() == 0)
                || (input.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(input.GetString())))
                throw new InvalidOperationException("OpenAI agent sessions with environment.type 'none' require user input.");
        }

        var vaultIds = GetOpenAiProviderOption<object>(request.Metadata, "vault_ids");
        if (vaultIds is not null)
            body["vault_ids"] = vaultIds;

        var overrides = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (GetOpenAiProviderOption<JsonElement?>(request.Metadata, "agent") is { ValueKind: JsonValueKind.Object } configuredAgent)
        {
            foreach (var property in configuredAgent.EnumerateObject())
                if (property.Name.ToLowerInvariant() is "instructions" or "model" or "multi_agent" or "reasoning" or "service_tier" or "text" or "tools")
                    overrides[property.Name.ToLowerInvariant()] = property.Value.Clone();
        }

        if (target.AgentId is null)
        {
            // An inline session must always have a model; preserve an explicit agent.model override.
            if (!overrides.TryGetValue("model", out var explicitModel)
                || explicitModel is not JsonElement { ValueKind: JsonValueKind.String } modelElement
                || string.IsNullOrWhiteSpace(modelElement.GetString()))
                overrides["model"] = target.LocalModelId;
        }

        if (!string.IsNullOrWhiteSpace(request.Instructions) && !overrides.ContainsKey("instructions"))
            overrides["instructions"] = request.Instructions;

        var savedAgent = target.AgentId is null ? null : await FindOpenAiAgentAsync(target.AgentId, cancellationToken);
        var mergedTools = new List<JsonElement>();
        if (overrides.TryGetValue("tools", out var configuredTools)
            && configuredTools is JsonElement { ValueKind: JsonValueKind.Array } configuredArray)
            mergedTools.AddRange(configuredArray.EnumerateArray().Select(static tool => tool.Clone()));
        else if (!overrides.ContainsKey("tools") && savedAgent.HasValue
            && TryGetOpenAiProperty(savedAgent.Value, "tools", out var persistedTools)
            && persistedTools.ValueKind == JsonValueKind.Array)
        {
            mergedTools.AddRange(persistedTools.EnumerateArray().Select(static tool => tool.Clone()));
        }

        foreach (var tool in request.Tools ?? [])
        {
            var function = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["name"] = tool.Name,
                ["description"] = tool.Description ?? string.Empty,
                ["parameters"] = tool.InputSchema ?? new { type = "object", properties = new { } },
                ["defer_loading"] = tool.DeferLoading ?? false
            }, JsonSerializerOptions.Web);

            mergedTools.RemoveAll(existing => string.Equals(TryGetOpenAiString(existing, "type"), "function", StringComparison.OrdinalIgnoreCase)
                                               && string.Equals(TryGetOpenAiString(existing, "name"), tool.Name, StringComparison.Ordinal));
            mergedTools.Add(function);
        }

        if (mergedTools.Count > 0)
            overrides["tools"] = mergedTools;

        var reasoning = GetOpenAiProviderOption<object>(request.Metadata, "reasoning")
                        ?? GetOpenAiProviderOption<object>(request.Metadata, "agents.reasoning");
        if (reasoning is not null && !overrides.ContainsKey("reasoning"))
            overrides["reasoning"] = reasoning;

        var serviceTier = GetOpenAiProviderOption<string>(request.Metadata, "service_tier");
        if (!string.IsNullOrWhiteSpace(serviceTier) && !overrides.ContainsKey("service_tier"))
            overrides["service_tier"] = serviceTier;

        if (request.ResponseFormat is not null || !string.IsNullOrWhiteSpace(request.Verbosity))
        {
            var text = overrides.TryGetValue("text", out var configuredText)
                       && configuredText is JsonElement { ValueKind: JsonValueKind.Object } textElement
                ? textElement.EnumerateObject().ToDictionary(static property => property.Name, static property => (object?)property.Value.Clone(), StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (request.ResponseFormat is not null && !text.ContainsKey("format"))
                text["format"] = BuildOpenAiAgentTextFormat(request.ResponseFormat);
            if (!string.IsNullOrWhiteSpace(request.Verbosity) && !text.ContainsKey("verbosity"))
                text["verbosity"] = request.Verbosity;
            if (!overrides.ContainsKey("text") || configuredText is JsonElement { ValueKind: JsonValueKind.Object })
                overrides["text"] = text;
        }

        var multiAgent = GetOpenAiProviderOption<object>(request.Metadata, "multi_agent");
        if (multiAgent is not null && !overrides.ContainsKey("multi_agent"))
            overrides["multi_agent"] = multiAgent;

        if (overrides.Count > 0)
            body["agent"] = overrides;

        return body;
    }

    private Dictionary<string, object?> BuildOpenAiAgentEnvironment(AIRequest request)
    {
        var configured = GetOpenAiProviderOption<JsonElement?>(request.Metadata, "environment");
        var environment = configured is { ValueKind: JsonValueKind.Object }
            ? configured.Value.EnumerateObject().ToDictionary(
                static property => property.Name,
                static property => (object?)property.Value.Clone(),
                StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (!environment.TryGetValue("type", out var typeValue) || typeValue is null)
            environment["type"] = "openai_hosted";

        var type = environment["type"] is JsonElement { ValueKind: JsonValueKind.String } typeElement
            ? typeElement.GetString()
            : environment["type"]?.ToString();
        if (type is not ("none" or "openai_hosted" or "self_hosted"))
            throw new NotSupportedException($"OpenAI agent environment.type '{type}' is not supported.");

        if (type == "openai_hosted")
        {
            var files = BuildOpenAiHostedFiles(request).ToList();
            if (files.Count > 0)
            {
                var configuredFiles = environment.TryGetValue("files", out var existingFiles)
                    && existingFiles is JsonElement { ValueKind: JsonValueKind.Array } array
                    ? array.EnumerateArray().Select(static file => (object)file.Clone())
                    : [];
                environment["files"] = configuredFiles.Concat(files).ToList();
            }
        }

        return environment;
    }

    private static IEnumerable<object> BuildOpenAiHostedFiles(AIRequest request)
    {
        foreach (var file in request.Input?.Items?
                     .Where(static item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))
                     .SelectMany(static item => item.Content ?? [])
                     .OfType<AIFileContentPart>() ?? [])
        {
            if (file.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
                continue;

            var fileId = TryGetMetadataValue(file.Metadata, "responses.file_id");
            var path = TryGetMetadataValue(file.Metadata, "openai.agents.path")
                       ?? $"/workspace/{SanitizeOpenAiAgentFilename(file.Filename ?? "input.bin")}";
            if (!string.IsNullOrWhiteSpace(fileId))
            {
                yield return new { type = "file_id", file_id = fileId, path };
                continue;
            }

            var value = file.Data?.ToString();
            if (TryParseDataUrl(value, out _, out var base64))
                yield return new { type = "inline", data = base64, path };
        }
    }

    private static object BuildOpenAiAgentInput(AIRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Input?.Text))
            return request.Input.Text;

        var messages = new List<object>();
        foreach (var item in request.Input?.Items ?? [])
        {
            if (!string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))
                continue;

            var content = new List<object>();
            foreach (var part in item.Content ?? [])
            {
                switch (part)
                {
                    case AITextContentPart text when !string.IsNullOrWhiteSpace(text.Text):
                        content.Add(new { type = "input_text", text = text.Text });
                        break;
                    case AIFileContentPart file when file.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true:
                        var imageUrl = file.Data?.ToString();
                        if (!string.IsNullOrWhiteSpace(imageUrl))
                            content.Add(new { type = "input_image", image_url = imageUrl });
                        break;
                }
            }

            if (content.Count > 0)
                messages.Add(new { type = "message", role = "user", content });
        }

        return messages;
    }

    private List<object> BuildOpenAiAgentFollowUpEvents(AIRequest request, OpenAiAgentStreamState state)
    {
        var events = new List<object>();
        var priorCalls = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tool in request.Input?.Items?.SelectMany(static item => item.Content ?? []).OfType<AIToolCallContentPart>() ?? [])
        {
            if (IsOpenAiComputerUseInput(tool))
                continue;
            if (tool.ProviderExecuted != true && tool.Output is null)
            {
                var turnId = TryGetMetadataValue(tool.Metadata, "openai.turn_id")
                             ?? TryGetMetadataValue(tool.Metadata, "turn_id");
                if (!string.IsNullOrWhiteSpace(turnId))
                    priorCalls[tool.ToolCallId] = turnId;
                continue;
            }

            if (tool.ProviderExecuted == true || tool.Output is null)
                continue;

            var outputTurnId = TryGetMetadataValue(tool.Metadata, "openai.turn_id")
                               ?? TryGetMetadataValue(tool.Metadata, "turn_id")
                               ?? priorCalls.GetValueOrDefault(tool.ToolCallId);
            if (string.IsNullOrWhiteSpace(outputTurnId))
                throw new InvalidOperationException($"OpenAI agent function result '{tool.ToolCallId}' is missing its turn id.");

            events.Add(new Dictionary<string, object?>
            {
                ["type"] = "agent.session.input.tool_result",
                ["turn_id"] = outputTurnId,
                ["call_id"] = tool.ToolCallId,
                ["success"] = !string.Equals(tool.State, "output-error", StringComparison.OrdinalIgnoreCase),
                ["output"] = tool.Output,
                ["error"] = string.Equals(tool.State, "output-error", StringComparison.OrdinalIgnoreCase)
                    ? tool.Output?.ToString()
                    : null
            });
        }

        var input = BuildOpenAiAgentInput(request);
        var inputElement = JsonSerializer.SerializeToElement(input, JsonSerializerOptions.Web);
        if ((inputElement.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(inputElement.GetString()))
            || (inputElement.ValueKind == JsonValueKind.Array && inputElement.GetArrayLength() > 0))
        {
            var messages = inputElement.ValueKind == JsonValueKind.String
                ? new object[] { new { role = "user", content = new[] { new { type = "input_text", text = inputElement.GetString() } } } }
                : inputElement.EnumerateArray().Select(static value => (object)value.Clone()).ToArray();
            events.Add(new { type = "agent.session.input.message", input = messages });
        }

        if (GetOpenAiProviderOption<bool?>(request.Metadata, "cancel") == true)
            events.Add(new { type = "agent.session.input.cancel" });

        return events;
    }

    private IEnumerable<AIStreamEvent> MapOpenAiAgentEvent(
        JsonElement agentEvent,
        OpenAiAgentTarget target,
        string model,
        OpenAiAgentStreamState state)
    {
        var type = TryGetOpenAiString(agentEvent, "type") ?? string.Empty;
        var eventId = TryGetOpenAiString(agentEvent, "event_id") ?? Guid.NewGuid().ToString("N");
        if (!state.SeenEventIds.Add(eventId))
            yield break;

        var timestamp = DateTimeOffset.UtcNow;
        if (TryGetOpenAiString(agentEvent, "session_id") is { Length: > 0 } sessionId)
            state.SessionId = sessionId;
        if (TryGetOpenAiString(agentEvent, "turn_id") is { Length: > 0 } turnId)
            state.TurnId = turnId;

        switch (type)
        {
            case "agent.session.created":
                if (!TryGetOpenAiProperty(agentEvent, "session", out var createdSession))
                    yield break;
                state.Session = createdSession;
                state.SessionId = TryGetOpenAiString(createdSession, "id") ?? state.SessionId;
                if (TryGetOpenAiProperty(createdSession, "environment", out var environment))
                {
                    state.EnvironmentId = TryGetOpenAiString(environment, "id") ?? state.EnvironmentId;
                    state.EnvironmentType = TryGetOpenAiString(environment, "type") ?? state.EnvironmentType;
                }
                if (!string.IsNullOrWhiteSpace(state.SessionId))
                {
                    foreach (var sessionEvent in CreateOpenAiSessionToolEvents(state.SessionId!, state.EnvironmentId, target, createdSession, timestamp))
                        yield return sessionEvent;
                }
                yield break;

            case "agent.session.turn.output_text.delta":
                var itemId = TryGetOpenAiString(agentEvent, "item_id") ?? eventId;
                if (state.StartedTextItems.Add(itemId))
                    yield return CreateOpenAiAgentEvent("text-start", itemId, new AITextStartEventData { ProviderMetadata = CreateOpenAiMetadata(agentEvent) }, timestamp, null);
                yield return CreateOpenAiAgentEvent("text-delta", itemId, new AITextDeltaEventData
                {
                    Delta = TryGetOpenAiString(agentEvent, "delta") ?? string.Empty,
                    ProviderMetadata = CreateOpenAiMetadata(agentEvent)
                }, timestamp, null);
                yield break;

            case "agent.session.turn.output_text.done":
                itemId = TryGetOpenAiString(agentEvent, "item_id") ?? eventId;
                if (state.StartedTextItems.Remove(itemId))
                {
                    state.CompletedTextItems.Add(itemId);
                    yield return CreateOpenAiAgentEvent("text-end", itemId, new AITextEndEventData { ProviderMetadata = CreateOpenAiMetadata(agentEvent) }, timestamp, null);
                }
                yield break;

            case "agent.session.turn.reasoning_summary_text.delta":
                itemId = TryGetOpenAiString(agentEvent, "item_id") ?? eventId;
                if (state.StartedReasoningItems.Add(itemId))
                    yield return CreateOpenAiAgentEvent("reasoning-start", itemId, new AIReasoningStartEventData { ProviderMetadata = CreateOpenAiNestedMetadata(agentEvent) }, timestamp, null);
                yield return CreateOpenAiAgentEvent("reasoning-delta", itemId, new AIReasoningDeltaEventData
                {
                    Delta = TryGetOpenAiString(agentEvent, "delta") ?? string.Empty,
                    ProviderMetadata = CreateOpenAiNestedMetadata(agentEvent)
                }, timestamp, null);
                yield break;

            case "agent.session.turn.reasoning_summary_text.done":
                itemId = TryGetOpenAiString(agentEvent, "item_id") ?? eventId;
                if (state.StartedReasoningItems.Remove(itemId))
                {
                    state.CompletedReasoningItems.Add(itemId);
                    yield return CreateOpenAiAgentEvent("reasoning-end", itemId, new AIReasoningEndEventData { ProviderMetadata = CreateOpenAiNestedMetadata(agentEvent) }, timestamp, null);
                }
                yield break;

            case "agent.session.turn.item.done":
                if (!TryGetOpenAiProperty(agentEvent, "item", out var item))
                    yield break;
                foreach (var mapped in MapOpenAiAgentItem(item, agentEvent, state, timestamp))
                    yield return mapped;
                yield break;

            case "agent.session.turn.item.added":
                if (TryGetOpenAiProperty(agentEvent, "item", out var addedItem)
                    && TryGetOpenAiString(addedItem, "type") == "computer_use_call")
                    foreach (var mapped in MapOpenAiAgentItem(addedItem, agentEvent, state, timestamp))
                        yield return mapped;
                yield break;

            case "agent.session.requires_action":
                state.RequiresAction = true;
                state.Status = "requires_action";
                if (TryGetOpenAiProperty(agentEvent, "session", out var actionSession))
                    state.Session = actionSession;
                // Refresh required_actions after the event; its embedded snapshot can already be stale.
                yield break;

            case "agent.session.turn.completed":
                if (TryGetOpenAiProperty(agentEvent, "turn", out var completedTurn)
                    && TryGetOpenAiString(completedTurn, "subagent_id") is not null)
                    yield break;
                state.Status = "completed";
                state.TerminalEvent = agentEvent.Clone();
                if (TryGetOpenAiProperty(agentEvent, "usage", out var completedUsage))
                    state.Usage = completedUsage;
                yield break;
            case "agent.session.turn.failed":
            case "agent.session.failed":
            case "error":
                if (type == "agent.session.turn.failed"
                    && TryGetOpenAiProperty(agentEvent, "turn", out var failedTurn)
                    && TryGetOpenAiString(failedTurn, "subagent_id") is not null)
                    yield break;
                state.Status = "failed";
                state.TerminalEvent = agentEvent.Clone();
                state.Error = TryGetOpenAiProperty(agentEvent, "error", out var failure) ? failure : agentEvent.Clone();
                yield return CreateOpenAiAgentEvent("error", eventId, new AIErrorEventData
                {
                    ErrorText = ExtractOpenAiAgentError(agentEvent) ?? "OpenAI agent session failed."
                }, timestamp, CreateOpenAiAgentMetadata(state, target));
                yield break;
            case "agent.session.turn.cancelled":
                if (TryGetOpenAiProperty(agentEvent, "turn", out var cancelledTurn)
                    && TryGetOpenAiString(cancelledTurn, "subagent_id") is not null)
                    yield break;
                state.Status = "cancelled";
                state.TerminalEvent = agentEvent.Clone();
                yield break;
            case "agent.session.idle":
                if (TryGetOpenAiProperty(agentEvent, "session", out var idleSession))
                    state.Session = idleSession;
                yield break;
            default:
                if (type.StartsWith("agent.session.environment.", StringComparison.Ordinal)
                    || type.StartsWith("agent.session.subagent.", StringComparison.Ordinal)
                    || type == "agent.output.command_execution_output.delta")
                {
                    foreach (var mapped in CreateProviderLifecycleToolEvents(agentEvent, state, timestamp))
                        yield return mapped;
                }
                yield break;
        }
    }

    private IEnumerable<AIStreamEvent> MapOpenAiAgentItem(
        JsonElement item,
        JsonElement sourceEvent,
        OpenAiAgentStreamState state,
        DateTimeOffset timestamp)
    {
        var type = TryGetOpenAiString(item, "type") ?? "item";
        var id = TryGetOpenAiString(item, "id") ?? Guid.NewGuid().ToString("N");
        var turnId = TryGetOpenAiString(item, "turn_id") ?? state.TurnId;
        if (!string.IsNullOrWhiteSpace(turnId))
            state.TurnId = turnId;

        if (type == "message")
        {
            if (state.StartedTextItems.Contains(id)
                || state.CompletedTextItems.Contains(id)
                || !string.Equals(TryGetOpenAiString(item, "role"), "assistant", StringComparison.OrdinalIgnoreCase))
                yield break;

            var text = ExtractOpenAiAgentText(item, "output_text");
            if (string.IsNullOrEmpty(text))
                yield break;
            yield return CreateOpenAiAgentEvent("text-start", id, new AITextStartEventData { ProviderMetadata = CreateOpenAiMetadata(sourceEvent) }, timestamp, null);
            yield return CreateOpenAiAgentEvent("text-delta", id, new AITextDeltaEventData { Delta = text, ProviderMetadata = CreateOpenAiMetadata(sourceEvent) }, timestamp, null);
            yield return CreateOpenAiAgentEvent("text-end", id, new AITextEndEventData { ProviderMetadata = CreateOpenAiMetadata(sourceEvent) }, timestamp, null);
            yield break;
        }

        if (type == "reasoning")
        {
            if (state.StartedReasoningItems.Contains(id)
                || state.CompletedReasoningItems.Contains(id))
                yield break;

            var summary = ExtractOpenAiAgentSummary(item);
            if (string.IsNullOrEmpty(summary))
                yield break;
            yield return CreateOpenAiAgentEvent("reasoning-start", id, new AIReasoningStartEventData { ProviderMetadata = CreateOpenAiNestedMetadata(sourceEvent) }, timestamp, null);
            yield return CreateOpenAiAgentEvent("reasoning-delta", id, new AIReasoningDeltaEventData { Delta = summary, ProviderMetadata = CreateOpenAiNestedMetadata(sourceEvent) }, timestamp, null);
            yield return CreateOpenAiAgentEvent("reasoning-end", id, new AIReasoningEndEventData { ProviderMetadata = CreateOpenAiNestedMetadata(sourceEvent) }, timestamp, null);
            yield break;
        }

        if (type == "computer_use_call")
        {
            var computerMetadata = CreateOpenAiNestedMetadata(new Dictionary<string, object>
            {
                ["item_type"] = type, ["turn_id"] = turnId ?? string.Empty
            });
            if (state.EmittedToolInputs.Add(id))
                yield return CreateOpenAiAgentEvent("tool-input-available", id, new AIToolInputAvailableEventData
                {
                    ToolName = "computer_use", Title = TryGetOpenAiString(item, "title") ?? "Browser activity",
                    Input = new { title = TryGetOpenAiString(item, "title") }, ProviderExecuted = true,
                    ProviderMetadata = computerMetadata
                }, timestamp, null);
            var computerStatus = TryGetOpenAiString(item, "status");
            if (computerStatus is "completed" or "failed" or "incomplete" && state.EmittedToolOutputs.Add(id))
                yield return CreateOpenAiAgentEvent("tool-output-available", id, new AIToolOutputAvailableEventData
                {
                    ToolName = "computer_use", ProviderExecuted = true,
                    Output = new { status = TryGetOpenAiString(item, "status"), screenshot = TryGetOpenAiProperty(item, "output", out var screenshot) && screenshot.ValueKind == JsonValueKind.Object ? screenshot : (JsonElement?)null },
                    ProviderMetadata = computerMetadata
                }, timestamp, null);
            yield break;
        }

        var isFunction = type == "function_call";
        var isFunctionOutput = type == "function_call_output";
        var providerExecuted = !isFunction && !isFunctionOutput;
        var toolCallId = TryGetOpenAiString(item, "call_id") ?? id;
        var toolName = ResolveOpenAiAgentToolName(type, item);
        var metadata = new Dictionary<string, object>
        {
            ["raw"] = item.Clone(),
            ["item_type"] = type,
            ["turn_id"] = turnId ?? string.Empty
        };

        if (isFunction && !string.IsNullOrWhiteSpace(turnId))
            state.FunctionTurnIds[toolCallId] = turnId;

        if (!isFunctionOutput && state.EmittedToolInputs.Add(toolCallId))
        {
            yield return CreateOpenAiAgentEvent("tool-input-available", toolCallId, new AIToolInputAvailableEventData
            {
                ToolName = toolName,
                Title = toolName,
                Input = TryGetOpenAiProperty(item, "arguments", out var arguments) ? arguments : item.Clone(),
                ProviderExecuted = providerExecuted,
                ProviderMetadata = CreateOpenAiNestedMetadata(metadata)
            }, timestamp, null);
        }

        var status = TryGetOpenAiString(item, "status");
        if ((isFunctionOutput || string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
            && state.EmittedToolOutputs.Add(toolCallId))
        {
            var output = TryGetOpenAiProperty(item, "output", out var itemOutput) ? itemOutput : item.Clone();
            yield return CreateOpenAiAgentEvent("tool-output-available", toolCallId, new AIToolOutputAvailableEventData
            {
                ToolName = toolName,
                Output = output,
                ProviderExecuted = providerExecuted,
                ProviderMetadata = CreateOpenAiNestedMetadata(metadata)
            }, timestamp, null);
        }
    }

    private IEnumerable<AIStreamEvent> MapOpenAiRequiredActions(
        JsonElement session,
        OpenAiAgentStreamState state,
        DateTimeOffset timestamp)
    {
        if (!TryGetOpenAiProperty(session, "required_actions", out var actions) || actions.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var action in actions.EnumerateArray())
        {
            if (TryGetOpenAiString(action, "type") == "computer_use_approval_request")
            {
                foreach (var mapped in MapOpenAiComputerUseAction(action, state, timestamp))
                    yield return mapped;
                continue;
            }
            if (!string.Equals(TryGetOpenAiString(action, "type"), "function_call", StringComparison.OrdinalIgnoreCase))
                continue;
            var callId = TryGetOpenAiString(action, "call_id") ?? Guid.NewGuid().ToString("N");
            var turnId = TryGetOpenAiString(action, "turn_id") ?? state.TurnId;
            if (!string.IsNullOrWhiteSpace(turnId))
                state.FunctionTurnIds[callId] = turnId;
            if (!state.EmittedToolInputs.Add(callId))
                continue;

            yield return CreateOpenAiAgentEvent("tool-input-available", callId, new AIToolInputAvailableEventData
            {
                ToolName = TryGetOpenAiString(action, "name") ?? "function",
                Title = TryGetOpenAiString(action, "name"),
                Input = TryGetOpenAiProperty(action, "arguments", out var arguments) ? arguments : new { },
                ProviderExecuted = false,
                ProviderMetadata = CreateOpenAiNestedMetadata(new Dictionary<string, object>
                {
                    ["raw"] = action.Clone(),
                    ["turn_id"] = turnId ?? string.Empty
                })
            }, timestamp, null);
        }
    }

    private IEnumerable<AIStreamEvent> CreateProviderLifecycleToolEvents(
        JsonElement agentEvent,
        OpenAiAgentStreamState state,
        DateTimeOffset timestamp)
    {
        var type = TryGetOpenAiString(agentEvent, "type") ?? "openai_agent_event";
        var id = ResolveProviderLifecycleToolCallId(agentEvent, type);
        var toolName = type.Replace("agent.session.", string.Empty, StringComparison.Ordinal)
            .Replace("agent.output.", string.Empty, StringComparison.Ordinal)
            .Replace('.', '_');
        var providerMetadata = CreateOpenAiNestedMetadata(agentEvent);

        if (state.EmittedToolInputs.Add(id))
        {
            yield return CreateOpenAiAgentEvent("tool-input-available", id, new AIToolInputAvailableEventData
            {
                ToolName = toolName,
                Title = toolName,
                Input = agentEvent.Clone(),
                ProviderExecuted = true,
                ProviderMetadata = providerMetadata
            }, timestamp, null);
        }

        yield return CreateOpenAiAgentEvent("tool-output-available", id, new AIToolOutputAvailableEventData
        {
            ToolName = toolName,
            Output = agentEvent.Clone(),
            ProviderExecuted = true,
            Dynamic = true,
            Preliminary = !IsTerminalProviderLifecycleEvent(type),
            ProviderMetadata = providerMetadata
        }, timestamp, null);
    }

    private static string ResolveProviderLifecycleToolCallId(JsonElement agentEvent, string type)
    {
        var resourceId = TryGetOpenAiString(agentEvent, "item_id")
                         ?? TryGetOpenAiNestedString(agentEvent, "environment", "id")
                         ?? TryGetOpenAiNestedString(agentEvent, "subagent", "id")
                         ?? TryGetOpenAiString(agentEvent, "execution_id")
                         ?? TryGetOpenAiString(agentEvent, "command_execution_id")
                         ?? TryGetOpenAiNestedString(agentEvent, "command_execution", "id");
        if (!string.IsNullOrWhiteSpace(resourceId))
            return $"openai-lifecycle-{resourceId}";

        return TryGetOpenAiString(agentEvent, "event_id")
               ?? $"openai-lifecycle-{type}-{Guid.NewGuid():N}";
    }

    private static string? TryGetOpenAiNestedString(JsonElement element, string propertyName, string nestedPropertyName)
        => TryGetOpenAiProperty(element, propertyName, out var nested)
            ? TryGetOpenAiString(nested, nestedPropertyName)
            : null;

    private static bool IsTerminalProviderLifecycleEvent(string type)
        => type.EndsWith(".failed", StringComparison.Ordinal)
           || type.EndsWith(".closed", StringComparison.Ordinal)
           || type.EndsWith(".completed", StringComparison.Ordinal)
           || type.EndsWith(".cancelled", StringComparison.Ordinal);

    private IEnumerable<AIStreamEvent> CreateOpenAiSessionToolEvents(
        string sessionId,
        string? environmentId,
        OpenAiAgentTarget target,
        JsonElement session,
        DateTimeOffset timestamp)
    {
        var id = $"openai-create-session-{sessionId}";
        var providerMetadata = CreateOpenAiNestedMetadata(new Dictionary<string, object>
        {
            ["sessionId"] = sessionId,
            ["environmentId"] = environmentId ?? string.Empty,
            ["agentId"] = target.AgentId ?? TryGetOpenAiNestedString(session, "agent", "id"),
            ["raw"] = session.Clone()
        });
        yield return CreateOpenAiAgentEvent("tool-input-available", id, new AIToolInputAvailableEventData
        {
            ToolName = AgentSessionToolName,
            Title = "Create OpenAI agent session",
            Input = new { agent_id = target.AgentId, environment = new { type = TryGetOpenAiNestedString(session, "environment", "type") ?? "openai_hosted" } },
            ProviderExecuted = true,
            ProviderMetadata = providerMetadata
        }, timestamp, null);
        yield return CreateOpenAiAgentEvent("tool-output-available", id, new AIToolOutputAvailableEventData
        {
            ToolName = AgentSessionToolName,
            Output = CreateOpenAiSessionToolResult(sessionId, environmentId, target, session),
            ProviderExecuted = true,
            ProviderMetadata = providerMetadata
        }, timestamp, null);
    }

    private static CallToolResult CreateOpenAiSessionToolResult(
        string sessionId,
        string? environmentId,
        OpenAiAgentTarget target,
        JsonElement session)
        => new()
        {
            Content = [],
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                sessionId,
                session_id = sessionId,
                agentId = target.AgentId ?? TryGetOpenAiNestedString(session, "agent", "id"),
                agent_id = target.AgentId ?? TryGetOpenAiNestedString(session, "agent", "id"),
                model = target.LocalModelId,
                environmentId,
                environment_id = environmentId,
                session = session.Clone()
            }, JsonSerializerOptions.Web)
        };

    private AIStreamEvent CreateOpenAiAgentFinishEvent(string model, OpenAiAgentTarget target, OpenAiAgentStreamState state)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var finishReason = state.RequiresAction ? "tool-calls" : state.Status switch
        {
            "failed" => "error",
            "cancelled" => "cancelled",
            _ => "stop"
        };
        return CreateOpenAiAgentEvent("finish", state.SessionId, new AIFinishEventData
        {
            FinishReason = finishReason,
            Model = model,
            CompletedAt = timestamp.ToUnixTimeSeconds(),
            MessageMetadata = AIFinishMessageMetadata.Create(
                model,
                timestamp,
                state.Usage,
                additionalProperties: new Dictionary<string, object?>
                {
                    [GetIdentifier()] = CreateOpenAiAgentMetadata(state, target)
                })
        }, timestamp, CreateOpenAiAgentMetadata(state, target));
    }

    private async Task<HttpResponseMessage> SendOpenAiAgentStreamRequestAsync(
        HttpMethod method,
        string uri,
        object? body,
        string operation,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.TryAddWithoutValidation(AgentsBetaHeaderName, AgentsBetaHeaderValue);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body, JsonSerializerOptions.Web), Encoding.UTF8, MediaTypeNames.Application.Json);

        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.IsSuccessStatusCode)
            return response;

        var error = await response.Content.ReadAsStringAsync(cancellationToken);
        response.Dispose();
        request.Dispose();
        throw new InvalidOperationException($"{operation} failed with status {(int)response.StatusCode}: {error}");
    }

    private async Task<JsonElement> SendOpenAiAgentsJsonAsync(
        HttpMethod method,
        string uri,
        object? body,
        string operation,
        CancellationToken cancellationToken,
        bool sensitive = false)
    {
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation(AgentsBetaHeaderName, AgentsBetaHeaderValue);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body, JsonSerializerOptions.Web), Encoding.UTF8, MediaTypeNames.Application.Json);
        using var response = await _client.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(sensitive
                ? $"{operation} failed with status {(int)response.StatusCode}. Refresh the session before responding again."
                : $"{operation} failed with status {(int)response.StatusCode}: {json}");
        return string.IsNullOrWhiteSpace(json)
            ? JsonSerializer.SerializeToElement(new { }, JsonSerializerOptions.Web)
            : JsonSerializer.Deserialize<JsonElement>(json, JsonSerializerOptions.Web).Clone();
    }

    private static async IAsyncEnumerable<JsonElement> ReadOpenAiAgentSseEventsAsync(
        HttpResponseMessage response,
        ProviderBackendCaptureRequest? capture,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        await using var captureSink = ProviderBackendCapture.BeginStreamCapture("openai-agents", response, capture);
        var data = new List<string>();
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                break;
            if (captureSink is not null)
                await captureSink.WriteLineAsync(line, cancellationToken);
            if (line.Length == 0)
            {
                if (TryParseOpenAiAgentSseData(data, out var parsed))
                    yield return parsed;
                data.Clear();
                continue;
            }
            if (line.StartsWith("data:", StringComparison.Ordinal))
                data.Add(line.Length > 5 && line[5] == ' ' ? line[6..] : line[5..]);
        }
        if (TryParseOpenAiAgentSseData(data, out var final))
            yield return final;
    }

    private ProviderBackendCaptureRequest? GetOpenAiAgentBackendCapture(AIRequest request)
    {
        try
        {
            return GetOpenAiProviderOption<ProviderBackendCaptureRequest>(request.Metadata, "capture")
                   ?? GetOpenAiProviderOption<ProviderBackendCaptureRequest>(request.Metadata, "backend_capture");
        }
        catch
        {
            return null;
        }
    }

    private static bool TryParseOpenAiAgentSseData(List<string> data, out JsonElement value)
    {
        value = default;
        if (data.Count == 0 || data.Count == 1 && data[0] == "[DONE]")
            return false;
        try
        {
            value = JsonSerializer.Deserialize<JsonElement>(string.Join('\n', data), JsonSerializerOptions.Web).Clone();
            return value.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<List<JsonElement>> ListOpenAiAgentSessionItemsAsync(string sessionId, CancellationToken cancellationToken)
    {
        var result = new List<JsonElement>();
        string? after = null;
        do
        {
            var uri = $"{AgentSessionsEndpoint}/{Uri.EscapeDataString(sessionId)}/items?order=asc&limit={AgentPageSize}"
                      + (after is null ? string.Empty : $"&after={Uri.EscapeDataString(after)}");
            var page = await SendOpenAiAgentsJsonAsync(HttpMethod.Get, uri, null, "OpenAI agent list session items", cancellationToken);
            if (TryGetOpenAiProperty(page, "data", out var data) && data.ValueKind == JsonValueKind.Array)
                result.AddRange(data.EnumerateArray().Select(static item => item.Clone()));
            after = TryGetOpenAiBool(page, "has_more") == true ? TryGetOpenAiString(page, "last_id") : null;
        } while (!string.IsNullOrWhiteSpace(after));
        return result;
    }

    private Task<JsonElement> RetrieveOpenAiAgentSessionAsync(string sessionId, CancellationToken cancellationToken)
        => SendOpenAiAgentsJsonAsync(
            HttpMethod.Get,
            $"{AgentSessionsEndpoint}/{Uri.EscapeDataString(sessionId)}",
            null,
            "OpenAI agent retrieve session",
            cancellationToken);

    private async Task<List<OpenAiAgentArtifact>> ListAndDownloadOpenAiAgentArtifactsAsync(
        string sessionId,
        string? turnId,
        CancellationToken cancellationToken)
    {
        var artifacts = new List<OpenAiAgentArtifact>();
        string? after = null;
        do
        {
            var uri = $"{AgentSessionsEndpoint}/{Uri.EscapeDataString(sessionId)}/artifacts?order=asc&limit={AgentPageSize}"
                      + (after is null ? string.Empty : $"&after={Uri.EscapeDataString(after)}");
            var page = await SendOpenAiAgentsJsonAsync(HttpMethod.Get, uri, null, "OpenAI agent list artifacts", cancellationToken);
            if (TryGetOpenAiProperty(page, "data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var artifact in data.EnumerateArray())
                {
                    var artifactTurnId = TryGetOpenAiString(artifact, "turn_id") ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(turnId) && !string.Equals(turnId, artifactTurnId, StringComparison.Ordinal))
                        continue;
                    var id = TryGetOpenAiString(artifact, "id");
                    if (string.IsNullOrWhiteSpace(id))
                        continue;
                    var bytes = await DownloadOpenAiAgentArtifactAsync(sessionId, id, cancellationToken);
                    var path = TryGetOpenAiString(artifact, "path") ?? id;
                    artifacts.Add(new OpenAiAgentArtifact(
                        id,
                        path,
                        artifactTurnId,
                        TryGetOpenAiString(artifact, "environment_id") ?? string.Empty,
                        TryGetOpenAiInt64(artifact, "size_bytes") ?? bytes.LongLength,
                        TryGetOpenAiInt64(artifact, "created_at") ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        bytes,
                        ResolveOpenAiAgentMediaType(path)));
                }
            }
            after = TryGetOpenAiBool(page, "has_more") == true ? TryGetOpenAiString(page, "last_id") : null;
        } while (!string.IsNullOrWhiteSpace(after));
        return artifacts;
    }

    private async Task<byte[]> DownloadOpenAiAgentArtifactAsync(string sessionId, string artifactId, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{AgentSessionsEndpoint}/{Uri.EscapeDataString(sessionId)}/artifacts/{Uri.EscapeDataString(artifactId)}/content");
        request.Headers.TryAddWithoutValidation(AgentsBetaHeaderName, AgentsBetaHeaderValue);
        using var response = await _client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenAI agent artifact download failed with status {(int)response.StatusCode}.");
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private async Task<JsonElement?> FindOpenAiAgentAsync(string agentId, CancellationToken cancellationToken)
    {
        string? after = null;
        do
        {
            var uri = $"{AgentsEndpoint}?order=asc&limit={AgentPageSize}"
                      + (after is null ? string.Empty : $"&after={Uri.EscapeDataString(after)}");
            var page = await SendOpenAiAgentsJsonAsync(HttpMethod.Get, uri, null, "OpenAI agents list", cancellationToken);
            if (TryGetOpenAiProperty(page, "data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var agent in data.EnumerateArray())
                {
                    if (string.Equals(TryGetOpenAiString(agent, "id"), agentId, StringComparison.Ordinal))
                        return agent.Clone();
                }
            }
            after = TryGetOpenAiBool(page, "has_more") == true ? TryGetOpenAiString(page, "last_id") : null;
        } while (!string.IsNullOrWhiteSpace(after));
        return null;
    }

    private static bool TryExtractOpenAiAgentSession(
        object? value,
        OpenAiAgentTarget target,
        out string sessionId,
        out string? environmentId)
    {
        sessionId = string.Empty;
        environmentId = null;
        if (value is null)
            return false;
        var element = value is JsonElement json ? json : JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
        if (TryGetOpenAiProperty(element, "structuredContent", out var structured))
            element = structured;
        if (TryGetOpenAiProperty(element, "session", out var nested) && nested.ValueKind == JsonValueKind.Object
            && TryExtractOpenAiAgentSession(nested, target, out sessionId, out environmentId))
            return true;
        var agentId = TryGetOpenAiString(element, "agentId") ?? TryGetOpenAiString(element, "agent_id");
        if (target.AgentId is not null && !string.IsNullOrWhiteSpace(agentId) && !string.Equals(agentId, target.AgentId, StringComparison.Ordinal))
            return false;
        if (target.AgentId is null && TryGetOpenAiString(element, "model") is { } model
            && !string.Equals(model, target.LocalModelId, StringComparison.Ordinal))
            return false;
        sessionId = TryGetOpenAiString(element, "sessionId") ?? TryGetOpenAiString(element, "session_id") ?? string.Empty;
        environmentId = TryGetOpenAiString(element, "environmentId") ?? TryGetOpenAiString(element, "environment_id");
        return !string.IsNullOrWhiteSpace(sessionId);
    }

    private AIStreamEvent CreateOpenAiAgentEvent(
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

    private Dictionary<string, object?> CreateOpenAiAgentMetadata(OpenAiAgentStreamState state, OpenAiAgentTarget target)
        => new()
        {
            ["openai.agent.beta"] = AgentsBetaHeaderValue,
            ["openai.agent.agent_id"] = target.AgentId ?? (state.Session.HasValue ? TryGetOpenAiNestedString(state.Session.Value, "agent", "id") : null),
            ["openai.agent.session_id"] = state.SessionId,
            ["openai.agent.environment_id"] = state.EnvironmentId,
            ["openai.agent.turn_id"] = state.TurnId,
            ["openai.agent.status"] = state.Status,
            ["openai.agent.session"] = state.Session?.Clone(),
            ["openai.agent.terminal_event"] = state.TerminalEvent?.Clone(),
            ["openai.agent.error"] = state.Error?.Clone()
        };

    private Dictionary<string, Dictionary<string, object>> CreateOpenAiNestedMetadata(JsonElement raw)
        => CreateOpenAiNestedMetadata(new Dictionary<string, object> { ["raw"] = raw.Clone() });

    private Dictionary<string, Dictionary<string, object>> CreateOpenAiNestedMetadata(Dictionary<string, object> values)
        => new(StringComparer.OrdinalIgnoreCase) { [GetIdentifier()] = values };

    private Dictionary<string, object> CreateOpenAiMetadata(JsonElement raw)
        => new(StringComparer.OrdinalIgnoreCase) { ["raw"] = raw.Clone() };

    private static Dictionary<string, object?>? FlattenProviderMetadata(Dictionary<string, Dictionary<string, object>>? nested)
        => nested?.ToDictionary(static item => item.Key, static item => (object?)item.Value, StringComparer.OrdinalIgnoreCase);

    private static string ResolveOpenAiAgentToolName(string itemType, JsonElement item)
        => itemType switch
        {
            "function_call" or "function_call_output" => TryGetOpenAiString(item, "name") ?? "function",
            "mcp_call" => TryGetOpenAiString(item, "name") ?? "mcp",
            "web_search_call" => "web_search",
            "command_execution" => "command_execution",
            "create_subagent_call" => "create_subagent",
            "send_subagent_input_call" => "send_subagent_input",
            "resume_subagent_call" => "resume_subagent",
            "wait_for_subagents_call" => "wait_for_subagents",
            "interrupt_subagent_call" => "interrupt_subagent",
            "close_subagent_call" => "close_subagent",
            "agent_message" => "agent_message",
            _ => itemType
        };

    private static object BuildOpenAiAgentTextFormat(object? responseFormat)
    {
        if (responseFormat is null)
            return new { type = "text" };
        var element = JsonSerializer.SerializeToElement(responseFormat, JsonSerializerOptions.Web);
        if (TryGetOpenAiString(element, "type") == "json_schema"
            && TryGetOpenAiProperty(element, "json_schema", out var schema)
            && TryGetOpenAiProperty(schema, "schema", out var actualSchema))
            return new { type = "json_schema", schema = actualSchema };
        return element.Clone();
    }

    private static string ExtractOpenAiAgentText(JsonElement item, string expectedType)
    {
        if (!TryGetOpenAiProperty(item, "content", out var content) || content.ValueKind != JsonValueKind.Array)
            return string.Empty;
        return string.Concat(content.EnumerateArray()
            .Where(part => string.Equals(TryGetOpenAiString(part, "type"), expectedType, StringComparison.OrdinalIgnoreCase))
            .Select(part => TryGetOpenAiString(part, "text"))
            .Where(static text => text is not null));
    }

    private static string ExtractOpenAiAgentSummary(JsonElement item)
    {
        if (!TryGetOpenAiProperty(item, "summary", out var summary) || summary.ValueKind != JsonValueKind.Array)
            return string.Empty;
        return string.Concat(summary.EnumerateArray().Select(part => TryGetOpenAiString(part, "text")).Where(static text => text is not null));
    }

    private static string? ExtractOpenAiAgentError(JsonElement value)
    {
        if (TryGetOpenAiProperty(value, "error", out var error))
            return TryGetOpenAiString(error, "message") ?? error.ToString();
        if (TryGetOpenAiProperty(value, "turn", out var turn)
            && TryGetOpenAiProperty(turn, "error", out error))
            return TryGetOpenAiString(error, "message") ?? error.ToString();
        return null;
    }

    private static string ResolveOpenAiAgentMediaType(string path)
    {
        var provider = new FileExtensionContentTypeProvider();
        return provider.TryGetContentType(path, out var mediaType) ? mediaType : "application/octet-stream";
    }

    private static string SanitizeOpenAiAgentFilename(string filename)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(filename.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "input.bin" : sanitized;
    }

    private static bool TryParseDataUrl(string? value, out string mediaType, out string base64)
    {
        mediaType = "application/octet-stream";
        base64 = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return false;
        var separator = value.IndexOf(',');
        if (separator < 0 || !value[..separator].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
            return false;
        var header = value[5..separator];
        mediaType = header[..^7];
        base64 = value[(separator + 1)..];
        return true;
    }

    private static string? TryGetMetadataValue(IReadOnlyDictionary<string, object?>? metadata, string key)
    {
        if (metadata is null || !metadata.TryGetValue(key, out var value) || value is null)
            return null;
        return value is JsonElement { ValueKind: JsonValueKind.String } element ? element.GetString() : value.ToString();
    }

    private T? GetOpenAiProviderOption<T>(IReadOnlyDictionary<string, object?>? metadata, string key)
    {
        if (metadata is null)
            return default;
        if (metadata.TryGetValue(key, out var direct) && TryDeserializeOpenAiValue(direct, out T? directValue))
            return directValue;
        if (!metadata.TryGetValue(GetIdentifier(), out var provider) || provider is null)
            return default;
        var providerElement = provider is JsonElement json ? json : JsonSerializer.SerializeToElement(provider, JsonSerializerOptions.Web);
        if (!TryGetOpenAiProperty(providerElement, key, out var value))
            return default;
        return value.Deserialize<T>(JsonSerializerOptions.Web);
    }

    private static bool TryDeserializeOpenAiValue<T>(object? value, out T? result)
    {
        try
        {
            result = value is T typed ? typed : JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web).Deserialize<T>(JsonSerializerOptions.Web);
            return true;
        }
        catch
        {
            result = default;
            return false;
        }
    }

    private static bool TryGetOpenAiProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value.Clone();
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static string? TryGetOpenAiString(JsonElement element, string name)
        => TryGetOpenAiProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool? TryGetOpenAiBool(JsonElement element, string name)
        => TryGetOpenAiProperty(element, name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static long? TryGetOpenAiInt64(JsonElement element, string name)
        => TryGetOpenAiProperty(element, name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
}
