using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.OpenHands;

public partial class OpenHandsProvider
{
    private const string OpenHandsModelId = "openhands/cloud";
    private const string CreateConversationToolName = "create_conversation";
    private const string LookupConversationToolName = "lookup_conversation";
    private const string ConversationsEndpoint = "api/v1/app-conversations";
    private const string StartTasksEndpoint = "api/v1/app-conversations/start-tasks";
    private const string ConversationSearchEndpoint = "api/v1/app-conversations/search";
    private const int DefaultStartTaskMaxAttempts = 60;
    private const int DefaultExecutionMaxAttempts = 120;
    private const int DefaultStartTaskPollSeconds = 5;
    private const int DefaultExecutionPollSeconds = 30;

    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ApplyAuthHeader();

        var conversation = await ResolveConversationAsync(request, cancellationToken);
        var terminal = await WaitForConversationTerminalAsync(request, conversation.Id, cancellationToken);

        return CreateUnifiedResponse(request, conversation, terminal);
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ApplyAuthHeader();

        var timestamp = DateTimeOffset.UtcNow;
        var conversation = await ResolveConversationAsync(request, cancellationToken);

        foreach (var evt in CreateConversationToolEvents(conversation, timestamp))
            yield return evt;

        var textStarted = false;
        string? previousStatusText = null;
        OpenHandsConversationStatus? lastStatus = null;

        await foreach (var status in PollConversationStatusesAsync(request, conversation.Id, cancellationToken))
        {
            lastStatus = status;
            var statusText = CreateStatusText(conversation.Id, status);
            var delta = ComputeTextDelta(previousStatusText ?? string.Empty, statusText);
            previousStatusText = statusText;

            if (!textStarted)
            {
                textStarted = true;
                yield return CreateStreamEvent(
                    "text-start",
                    conversation.Id,
                    new AITextStartEventData { ProviderMetadata = CreateLooseProviderMetadata(status.Raw) },
                    timestamp,
                    CreateResponseMetadata(conversation, status));
            }

            if (!string.IsNullOrWhiteSpace(delta))
            {
                yield return CreateStreamEvent(
                    "text-delta",
                    conversation.Id,
                    new AITextDeltaEventData
                    {
                        Delta = delta,
                        ProviderMetadata = CreateLooseProviderMetadata(status.Raw)
                    },
                    DateTimeOffset.UtcNow,
                    CreateResponseMetadata(conversation, status));
            }
        }

        var finalStatus = lastStatus ?? await GetConversationStatusAsync(conversation.Id, cancellationToken);

        if (textStarted)
        {
            yield return CreateStreamEvent(
                "text-end",
                conversation.Id,
                new AITextEndEventData { ProviderMetadata = CreateLooseProviderMetadata(finalStatus.Raw) },
                DateTimeOffset.UtcNow,
                CreateResponseMetadata(conversation, finalStatus));
        }

        yield return CreateFinishEvent(conversation, finalStatus, DateTimeOffset.UtcNow);
    }

    private async Task<OpenHandsConversationResolution> ResolveConversationAsync(
        AIRequest request,
        CancellationToken cancellationToken)
    {
        if (TryFindConversationId(request, out var existingConversationId))
        {
            var existingStatus = await GetConversationStatusAsync(existingConversationId, cancellationToken);
            return new OpenHandsConversationResolution(
                existingConversationId,
                false,
                null,
                existingStatus.Raw,
                existingStatus.SelectedRepository,
                null);
        }

        var payload = BuildStartConversationPayload(request);
        using var response = await _client.PostAsJsonAsync(
            ConversationsEndpoint,
            payload,
            Json,
            cancellationToken);

        var startTask = await ReadJsonElementAsync(response, "OpenHands create conversation", cancellationToken);
        var startTaskId = ExtractString(startTask, "id")
                          ?? throw new InvalidOperationException("OpenHands create conversation response did not include a start-task id.");

        var immediateConversationId = ExtractString(startTask, "app_conversation_id")
                                      ?? ExtractString(startTask, "conversation_id");

        var readyTask = !string.IsNullOrWhiteSpace(immediateConversationId)
            ? startTask
            : await WaitForStartTaskReadyAsync(request, startTaskId, cancellationToken);

        var conversationId = ExtractString(readyTask, "app_conversation_id")
                             ?? ExtractString(readyTask, "conversation_id")
                             ?? immediateConversationId
                             ?? throw new InvalidOperationException("OpenHands start task reached ready state without an app_conversation_id.");

        var selectedRepository = request.Metadata?.GetProviderOption<string>(GetIdentifier(), "selected_repository");

        return new OpenHandsConversationResolution(
            conversationId,
            true,
            startTaskId,
            readyTask.Clone(),
            selectedRepository,
            ExtractString(readyTask, "sandbox_id"));
    }

    private Dictionary<string, object?> BuildStartConversationPayload(AIRequest request)
    {
        var taskText = ExtractTaskText(request)
                       ?? throw new InvalidOperationException("OpenHands requires a user task message.");

        var selectedRepository = request.Metadata?.GetProviderOption<string>(GetIdentifier(), "selected_repository")
                                 ?? request.Metadata?.GetProviderOption<string>(GetIdentifier(), "selectedRepository");

        if (string.IsNullOrWhiteSpace(selectedRepository))
            throw new InvalidOperationException("OpenHands requires metadata.openhands.selected_repository to start a conversation.");

        var payload = new Dictionary<string, object?>
        {
            ["initial_message"] = new Dictionary<string, object?>
            {
                ["content"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "text",
                        ["text"] = taskText
                    }
                }
            },
            ["selected_repository"] = selectedRepository
        };

        var providerMetadata = GetOpenHandsProviderMetadata(request.Metadata);
        if (providerMetadata is not null)
        {
            foreach (var prop in providerMetadata.Value.EnumerateObject())
            {
                if (IsLocalProviderOption(prop.Name) || prop.NameEquals("selectedRepository"))
                    continue;

                if (prop.NameEquals("selected_repository"))
                    continue;

                payload[prop.Name] = prop.Value.Clone();
            }
        }

        return payload;
    }

    private async Task<JsonElement> WaitForStartTaskReadyAsync(
        AIRequest request,
        string startTaskId,
        CancellationToken cancellationToken)
    {
        var maxAttempts = GetProviderOption(request, "start_task_max_attempts", DefaultStartTaskMaxAttempts);
        var delay = TimeSpan.FromSeconds(GetProviderOption(request, "start_task_poll_seconds", DefaultStartTaskPollSeconds));

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var task = await GetStartTaskAsync(startTaskId, cancellationToken);
            var status = ExtractString(task, "status");

            if (string.Equals(status, "READY", StringComparison.OrdinalIgnoreCase))
                return task;

            if (string.Equals(status, "ERROR", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"OpenHands start task failed: {task.GetRawText()}");

            await Task.Delay(delay, cancellationToken);
        }

        throw new TimeoutException($"Timed out waiting for OpenHands start task '{startTaskId}' to become READY.");
    }

    private async Task<JsonElement> GetStartTaskAsync(string startTaskId, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(
            $"{StartTasksEndpoint}?ids={Uri.EscapeDataString(startTaskId)}",
            cancellationToken);

        var json = await ReadJsonElementAsync(response, "OpenHands start task status", cancellationToken);
        return FirstObjectOrSelf(json).Clone();
    }

    private async Task<OpenHandsConversationStatus> WaitForConversationTerminalAsync(
        AIRequest request,
        string conversationId,
        CancellationToken cancellationToken)
    {
        OpenHandsConversationStatus? lastStatus = null;

        await foreach (var status in PollConversationStatusesAsync(request, conversationId, cancellationToken))
            lastStatus = status;

        return lastStatus ?? await GetConversationStatusAsync(conversationId, cancellationToken);
    }

    private async IAsyncEnumerable<OpenHandsConversationStatus> PollConversationStatusesAsync(
        AIRequest request,
        string conversationId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var maxAttempts = GetProviderOption(request, "execution_max_attempts", DefaultExecutionMaxAttempts);
        var delay = TimeSpan.FromSeconds(GetProviderOption(request, "execution_poll_seconds", DefaultExecutionPollSeconds));
        OpenHandsConversationStatus? previous = null;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var status = await GetConversationStatusAsync(conversationId, cancellationToken);

            if (!IsSameStatus(previous, status) || IsTerminalConversationStatus(status))
            {
                yield return status;
                previous = status;
            }

            if (IsTerminalConversationStatus(status))
                yield break;

            await Task.Delay(delay, cancellationToken);
        }

        throw new TimeoutException($"Timed out waiting for OpenHands conversation '{conversationId}' to reach a terminal state.");
    }

    private async Task<OpenHandsConversationStatus> GetConversationStatusAsync(string conversationId, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(
            $"{ConversationsEndpoint}?ids={Uri.EscapeDataString(conversationId)}",
            cancellationToken);

        var json = await ReadJsonElementAsync(response, "OpenHands conversation status", cancellationToken);
        var conversation = FirstObjectOrSelf(json);

        if (conversation.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"OpenHands conversation '{conversationId}' was not found.");

        var id = ExtractString(conversation, "id") ?? conversationId;
        var sandboxStatus = ExtractString(conversation, "sandbox_status");
        var executionStatus = ExtractString(conversation, "execution_status");

        return new OpenHandsConversationStatus(
            id,
            sandboxStatus,
            executionStatus,
            ExtractString(conversation, "selected_repository"),
            ExtractString(conversation, "title"),
            ExtractString(conversation, "sandbox_id"),
            conversation.Clone());
    }

    private AIResponse CreateUnifiedResponse(
        AIRequest request,
        OpenHandsConversationResolution conversation,
        OpenHandsConversationStatus status)
    {
        var content = new List<AIContentPart>
        {
            CreateConversationToolPart(conversation, status.Raw)
        };

        content.Add(new AITextContentPart
        {
            Type = "text",
            Text = CreateStatusText(conversation.Id, status),
            Metadata = new Dictionary<string, object?>
            {
                ["openhands.raw"] = status.Raw.Clone()
            }
        });

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = request.Model ?? OpenHandsModelId,
            Status = MapUnifiedStatus(status),
            Output = new AIOutput
            {
                Items =
                [
                    new AIOutputItem
                    {
                        Type = "message",
                        Role = "assistant",
                        Content = content,
                        Metadata = CreateResponseMetadata(conversation, status)
                    },
                    .. CreateConversationSourceOutputItems(conversation.Id)
                ],
                Metadata = CreateResponseMetadata(conversation, status)
            },
            Usage = CreateUsage(status),
            Metadata = CreateResponseMetadata(conversation, status)
        };
    }

    private AIToolCallContentPart CreateConversationToolPart(OpenHandsConversationResolution conversation, JsonElement rawConversation)
    {
        var toolName = conversation.Created ? CreateConversationToolName : LookupConversationToolName;

        return new AIToolCallContentPart
        {
            Type = "tool-call",
            ToolCallId = BuildConversationToolCallId(conversation.Id, conversation.Created),
            ToolName = toolName,
            Title = conversation.Created ? "Create OpenHands conversation" : "Lookup OpenHands conversation",
            Input = JsonSerializer.SerializeToElement(new
            {
                selected_repository = conversation.SelectedRepository,
                conversationId = conversation.Created ? null : conversation.Id
            }, Json),
            Output = CreateConversationToolResult(conversation, rawConversation),
            ProviderExecuted = true,
            State = "output-available",
            Metadata = CreateConversationToolMetadata(conversation, toolName)
        };
    }

    private IEnumerable<AIStreamEvent> CreateConversationToolEvents(
        OpenHandsConversationResolution conversation,
        DateTimeOffset timestamp)
    {
        var toolName = conversation.Created ? CreateConversationToolName : LookupConversationToolName;
        var toolCallId = BuildConversationToolCallId(conversation.Id, conversation.Created);
        var providerMetadata = CreateProviderScopedMetadata(CreateConversationToolMetadata(conversation, toolName));

        yield return CreateStreamEvent(
            "tool-input-available",
            toolCallId,
            new AIToolInputAvailableEventData
            {
                ToolName = toolName,
                Title = conversation.Created ? "Create OpenHands conversation" : "Lookup OpenHands conversation",
                Input = JsonSerializer.SerializeToElement(new
                {
                    selected_repository = conversation.SelectedRepository,
                    conversationId = conversation.Created ? null : conversation.Id
                }, Json),
                ProviderExecuted = true,
                ProviderMetadata = providerMetadata
            },
            timestamp,
            null);

        yield return CreateStreamEvent(
            "tool-output-available",
            toolCallId,
            new AIToolOutputAvailableEventData
            {
                ToolName = toolName,
                ProviderExecuted = true,
                Output = CreateConversationToolResult(conversation, conversation.RawConversation ?? JsonSerializer.SerializeToElement(new { id = conversation.Id }, Json)),
                ProviderMetadata = providerMetadata
            },
            timestamp,
            null);
    }

    private static CallToolResult CreateConversationToolResult(
        OpenHandsConversationResolution conversation,
        JsonElement rawConversation)
        => new()
        {
            Content = [],
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                conversationId = conversation.Id,
                conversation_id = conversation.Id,
                app_conversation_id = conversation.Id,
                startTaskId = conversation.StartTaskId,
                selected_repository = conversation.SelectedRepository,
                sandbox_id = conversation.SandboxId,
                conversationUrl = $"https://app.all-hands.dev/conversations/{conversation.Id}",
                conversation = rawConversation.Clone()
            }, Json)
        };

    private static bool TryFindConversationId(AIRequest request, out string conversationId)
    {
        conversationId = request.Metadata.GetProviderOption<string>("openhands", "conversationId")
                         ?? request.Metadata.GetProviderOption<string>("openhands", "conversation_id")
                         ?? request.Metadata.GetProviderOption<string>("openhands", "app_conversation_id")
                         ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(conversationId))
            return true;

        foreach (var item in request.Input?.Items ?? [])
        {
            foreach (var toolPart in item.Content?.OfType<AIToolCallContentPart>() ?? [])
            {
                if (toolPart.ProviderExecuted != true)
                    continue;

                if (TryExtractConversationId(toolPart.Output, out conversationId))
                    return true;

                if (TryExtractConversationId(toolPart.Metadata, out conversationId))
                    return true;

                if (IsConversationToolPart(toolPart) && TryExtractConversationId(toolPart.Input, out conversationId))
                    return true;
            }
        }

        conversationId = string.Empty;
        return false;
    }

    private static bool IsConversationToolPart(AIToolCallContentPart toolPart)
        => string.Equals(toolPart.ToolName, CreateConversationToolName, StringComparison.OrdinalIgnoreCase)
           || string.Equals(toolPart.ToolName, LookupConversationToolName, StringComparison.OrdinalIgnoreCase)
           || string.Equals(toolPart.ToolName, $"tool-{CreateConversationToolName}", StringComparison.OrdinalIgnoreCase)
           || string.Equals(toolPart.ToolName, $"tool-{LookupConversationToolName}", StringComparison.OrdinalIgnoreCase)
           || string.Equals(toolPart.Title, "Create OpenHands conversation", StringComparison.OrdinalIgnoreCase)
           || string.Equals(toolPart.Title, "Lookup OpenHands conversation", StringComparison.OrdinalIgnoreCase)
           || string.Equals(toolPart.Metadata?.GetValueOrDefault("messages.block.type")?.ToString(), CreateConversationToolName, StringComparison.OrdinalIgnoreCase)
           || string.Equals(toolPart.Metadata?.GetValueOrDefault("messages.block.type")?.ToString(), LookupConversationToolName, StringComparison.OrdinalIgnoreCase);

    private static bool TryExtractConversationId(object? value, out string conversationId)
    {
        conversationId = string.Empty;
        if (value is null)
            return false;

        var element = value is JsonElement json
            ? json
            : JsonSerializer.SerializeToElement(value, Json);

        if (element.ValueKind != JsonValueKind.Object)
            return false;

        if (element.TryGetProperty("structuredContent", out var structuredContent))
            element = structuredContent;

        if (element.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Object)
        {
            if (TryExtractConversationId(output, out conversationId))
                return true;
        }

        if (element.TryGetProperty("conversationId", out var id) && id.ValueKind == JsonValueKind.String)
            conversationId = id.GetString() ?? string.Empty;
        else if (element.TryGetProperty("conversation_id", out var snakeId) && snakeId.ValueKind == JsonValueKind.String)
            conversationId = snakeId.GetString() ?? string.Empty;
        else if (element.TryGetProperty("app_conversation_id", out var appId) && appId.ValueKind == JsonValueKind.String)
            conversationId = appId.GetString() ?? string.Empty;
        else if (element.TryGetProperty("conversation", out var conversation) && conversation.ValueKind == JsonValueKind.Object)
            conversationId = ExtractString(conversation, "id")
                             ?? ExtractString(conversation, "app_conversation_id")
                             ?? string.Empty;
        else if (element.TryGetProperty("openhands", out var openhands) && openhands.ValueKind == JsonValueKind.Object)
            conversationId = ExtractString(openhands, "conversationId")
                             ?? ExtractString(openhands, "conversation_id")
                             ?? ExtractString(openhands, "app_conversation_id")
                             ?? string.Empty;

        return !string.IsNullOrWhiteSpace(conversationId);
    }

    private static string? ExtractTaskText(AIRequest request)
    {
        var latestUserText = ExtractLatestUserText(request);

        if (!string.IsNullOrWhiteSpace(request.Instructions) && !string.IsNullOrWhiteSpace(latestUserText))
            return $"{request.Instructions}\n\n{latestUserText}";

        return latestUserText
               ?? request.Input?.Text
               ?? request.Instructions;
    }

    private static string? ExtractLatestUserText(AIRequest request)
    {
        foreach (var item in (request.Input?.Items ?? []).AsEnumerable().Reverse())
        {
            if (!string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))
                continue;

            var text = string.Join("\n", item.Content?.OfType<AITextContentPart>().Select(part => part.Text) ?? []);
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return null;
    }

    private static string CreateStatusText(string conversationId, OpenHandsConversationStatus status)
    {
        var executionStatus = string.IsNullOrWhiteSpace(status.ExecutionStatus) ? "unknown" : status.ExecutionStatus;
        var sandboxStatus = string.IsNullOrWhiteSpace(status.SandboxStatus) ? "unknown" : status.SandboxStatus;
        var title = string.IsNullOrWhiteSpace(status.Title) ? null : $" ({status.Title})";

        return $"OpenHands conversation {conversationId}{title} is {executionStatus}; sandbox is {sandboxStatus}. " +
               $"Conversation link: https://app.all-hands.dev/conversations/{conversationId}";
    }

    private static bool IsTerminalConversationStatus(OpenHandsConversationStatus status)
        => string.Equals(status.ExecutionStatus, "finished", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status.ExecutionStatus, "error", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status.ExecutionStatus, "stuck", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status.ExecutionStatus, "waiting_for_confirmation", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status.SandboxStatus, "ERROR", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status.SandboxStatus, "MISSING", StringComparison.OrdinalIgnoreCase);

    private static bool IsSameStatus(OpenHandsConversationStatus? left, OpenHandsConversationStatus right)
        => left is not null
           && string.Equals(left.SandboxStatus, right.SandboxStatus, StringComparison.OrdinalIgnoreCase)
           && string.Equals(left.ExecutionStatus, right.ExecutionStatus, StringComparison.OrdinalIgnoreCase);

    private static string MapUnifiedStatus(OpenHandsConversationStatus status)
    {
        if (string.Equals(status.ExecutionStatus, "finished", StringComparison.OrdinalIgnoreCase))
            return "completed";

        if (string.Equals(status.ExecutionStatus, "waiting_for_confirmation", StringComparison.OrdinalIgnoreCase))
            return "requires_action";

        if (string.Equals(status.ExecutionStatus, "error", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status.ExecutionStatus, "stuck", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status.SandboxStatus, "ERROR", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status.SandboxStatus, "MISSING", StringComparison.OrdinalIgnoreCase))
        {
            return "failed";
        }

        return "in_progress";
    }

    private static string MapFinishReason(OpenHandsConversationStatus status)
    {
        if (string.Equals(status.ExecutionStatus, "finished", StringComparison.OrdinalIgnoreCase))
            return "stop";

        if (string.Equals(status.ExecutionStatus, "waiting_for_confirmation", StringComparison.OrdinalIgnoreCase))
            return "tool-calls";

        if (string.Equals(status.ExecutionStatus, "error", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status.ExecutionStatus, "stuck", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status.SandboxStatus, "ERROR", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status.SandboxStatus, "MISSING", StringComparison.OrdinalIgnoreCase))
        {
            return "error";
        }

        return "stop";
    }

    private static string ComputeTextDelta(string previousText, string currentText)
    {
        if (string.IsNullOrEmpty(currentText) || string.Equals(previousText, currentText, StringComparison.Ordinal))
            return string.Empty;

        return currentText.StartsWith(previousText, StringComparison.Ordinal)
            ? currentText[previousText.Length..]
            : $"\n{currentText}";
    }

    private IEnumerable<AIOutputItem> CreateConversationSourceOutputItems(string conversationId)
    {
        yield return new AIOutputItem
        {
            Type = "source-url",
            Role = "assistant",
            Metadata = new Dictionary<string, object?>
            {
                ["source.url"] = $"https://app.all-hands.dev/conversations/{conversationId}",
                ["source.title"] = "OpenHands conversation"
            }
        };
    }

    private Dictionary<string, object?> CreateResponseMetadata(
        OpenHandsConversationResolution conversation,
        OpenHandsConversationStatus status)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["openhands"] = new Dictionary<string, object?>
            {
                ["conversationId"] = conversation.Id,
                ["conversation_id"] = conversation.Id,
                ["app_conversation_id"] = conversation.Id,
                ["conversationUrl"] = $"https://app.all-hands.dev/conversations/{conversation.Id}",
                ["startTaskId"] = conversation.StartTaskId,
                ["created"] = conversation.Created,
                ["selected_repository"] = status.SelectedRepository ?? conversation.SelectedRepository,
                ["sandbox_id"] = status.SandboxId ?? conversation.SandboxId,
                ["sandbox_status"] = status.SandboxStatus,
                ["execution_status"] = status.ExecutionStatus,
                ["title"] = status.Title,
                ["raw"] = status.Raw.Clone()
            }
        };

    private static Dictionary<string, object?> CreateUsage(OpenHandsConversationStatus status)
        => new()
        {
            ["sandbox_status"] = status.SandboxStatus,
            ["execution_status"] = status.ExecutionStatus
        };

    private AIFinishEventData CreateFinishData(
        OpenHandsConversationResolution conversation,
        OpenHandsConversationStatus status,
        DateTimeOffset timestamp)
        => new()
        {
            FinishReason = MapFinishReason(status),
            Model = OpenHandsModelId,
            CompletedAt = timestamp.ToUnixTimeSeconds(),
            MessageMetadata = AIFinishMessageMetadata.Create(
                OpenHandsModelId,
                timestamp,
                CreateUsage(status),
                additionalProperties: new Dictionary<string, object?>
                {
                    [GetIdentifier()] = new
                    {
                        conversationId = conversation.Id,
                        sandboxStatus = status.SandboxStatus,
                        executionStatus = status.ExecutionStatus,
                        raw = status.Raw.Clone()
                    }
                })
        };

    private AIStreamEvent CreateFinishEvent(
        OpenHandsConversationResolution conversation,
        OpenHandsConversationStatus status,
        DateTimeOffset timestamp)
        => CreateStreamEvent(
            "finish",
            conversation.Id,
            CreateFinishData(conversation, status, timestamp),
            timestamp,
            CreateResponseMetadata(conversation, status));

    private AIStreamEvent CreateStreamEvent(
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

    private Dictionary<string, object?> CreateConversationToolMetadata(OpenHandsConversationResolution conversation, string toolName)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["type"] = toolName,
            ["conversationId"] = conversation.Id,
            ["conversation_id"] = conversation.Id,
            ["app_conversation_id"] = conversation.Id,
            ["conversationUrl"] = $"https://app.all-hands.dev/conversations/{conversation.Id}",
            ["startTaskId"] = conversation.StartTaskId,
            ["selected_repository"] = conversation.SelectedRepository,
            ["sandbox_id"] = conversation.SandboxId,
            ["tool_name"] = toolName
        };

    private static Dictionary<string, Dictionary<string, object>> CreateProviderScopedMetadata(Dictionary<string, object?> metadata)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["openhands"] = metadata
                .Where(kvp => kvp.Value is not null)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value!, StringComparer.OrdinalIgnoreCase)
        };

    private static Dictionary<string, object> CreateLooseProviderMetadata(JsonElement raw)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["raw"] = raw.Clone()
        };

    private static JsonElement? GetOpenHandsProviderMetadata(Dictionary<string, object?>? metadata)
    {
        if (metadata is null || !metadata.TryGetValue("openhands", out var value) || value is null)
            return null;

        var element = value is JsonElement json ? json : JsonSerializer.SerializeToElement(value, Json);
        return element.ValueKind == JsonValueKind.Object ? element.Clone() : null;
    }

    private static bool IsLocalProviderOption(string name)
        => string.Equals(name, "conversationId", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "conversation_id", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "app_conversation_id", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "start_task_id", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "startTaskId", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "start_task_max_attempts", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "start_task_poll_seconds", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "execution_max_attempts", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "execution_poll_seconds", StringComparison.OrdinalIgnoreCase);

    private static int GetProviderOption(AIRequest request, string key, int fallback)
    {
        var value = request.Metadata?.GetProviderOption<int?>("openhands", key);
        return value is > 0 ? value.Value : fallback;
    }

    private static async Task<JsonElement> ReadJsonElementAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{operation} API error ({(int)response.StatusCode}): {raw}");

        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException($"{operation} returned an empty response.");

        return JsonSerializer.Deserialize<JsonElement>(raw, Json).Clone();
    }

    private static JsonElement FirstObjectOrSelf(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                    return item.Clone();
            }

            return default;
        }

        return element.Clone();
    }

    private static string BuildConversationToolCallId(string conversationId, bool created)
        => created
            ? $"openhands-create-conversation-{conversationId}"
            : $"openhands-lookup-conversation-{conversationId}";

    private static string? ExtractString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed record OpenHandsConversationResolution(
        string Id,
        bool Created,
        string? StartTaskId,
        JsonElement? RawConversation,
        string? SelectedRepository,
        string? SandboxId);

    private sealed record OpenHandsConversationStatus(
        string Id,
        string? SandboxStatus,
        string? ExecutionStatus,
        string? SelectedRepository,
        string? Title,
        string? SandboxId,
        JsonElement Raw);
}
