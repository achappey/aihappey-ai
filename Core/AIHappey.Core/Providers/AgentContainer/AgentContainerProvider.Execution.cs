using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.AgentContainer;

public partial class AgentContainerProvider
{
    private static readonly string[] CreateCommandKeys =
    [
        "versionId", "titleSource", "inputFiles", "inputRepositories", "skills", "metadata", "overrides"
    ];

    private static readonly string[] MessageCommandKeys = ["inputFiles", "overrides"];
    private static readonly string[] WakeCommandKeys = ["wakeDueId", "overrides"];

    private async Task<AgentContainerExecution> RunAgentContainerAsync(
        AIRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var route = ParseAgentContainerRoute(request.Model);
        var prompt = ExtractLatestAgentContainerUserText(request);
        var uploaded = await UploadAgentContainerInputFilesAsync(request, cancellationToken);
        var hasExisting = TryFindAgentContainerTaskId(request, out var existingTaskId);
        var idempotencyKey = ResolveAgentContainerIdempotencyKey(
            request,
            route.IsConversation ? "conversation-message" : "task-command",
            prompt,
            hasExisting ? existingTaskId : null);

        JsonElement commandResponse;
        JsonElement task;
        var created = !hasExisting;
        int? acceptedTurn = null;

        if (route.IsConversation)
        {
            if (hasExisting)
            {
                var existing = await GetAgentContainerTaskAsync(existingTaskId, cancellationToken);
                ValidateAgentContainerIdentity(existing, route);
                var status = GetAgentContainerString(existing, "status");
                if (IsTerminalAgentContainerStatus(status))
                    throw new InvalidOperationException($"AgentContainer conversation '{existingTaskId}' is terminal ({status}) and cannot accept another message.");
                if (!string.Equals(status, "awaiting_input", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"AgentContainer conversation '{existingTaskId}' must be awaiting_input before accepting another message; current status is '{status}'.");

                var payload = BuildAgentContainerPayload(request, MessageCommandKeys);
                payload["content"] = prompt;
                MergeInputFiles(payload, uploaded);
                commandResponse = await SendAgentContainerJsonAsync(
                    HttpMethod.Post,
                    $"conversations/{Uri.EscapeDataString(existingTaskId)}/messages",
                    payload,
                    "send conversation message",
                    idempotencyKey,
                    cancellationToken);
                task = RequireAgentContainerTask(commandResponse);
                acceptedTurn = TryGetAgentContainerProperty(commandResponse, "message", out var sentMessage)
                    ? GetAgentContainerInt(sentMessage, "turnNumber")
                    : GetAgentContainerInt(task, "turnNumber");
            }
            else
            {
                var payload = BuildAgentContainerPayload(request, CreateCommandKeys);
                payload["instructions"] = ResolveConversationInstructions(request, prompt);
                payload["message"] = prompt;
                MergeInputFiles(payload, uploaded);
                commandResponse = await SendAgentContainerJsonAsync(
                    HttpMethod.Post,
                    $"agents/{Uri.EscapeDataString(route.AgentId)}/conversations",
                    payload,
                    "create conversation",
                    idempotencyKey,
                    cancellationToken);
                task = RequireAgentContainerTask(commandResponse);
                acceptedTurn = TryGetAgentContainerProperty(commandResponse, "message", out var sentMessage)
                    ? GetAgentContainerInt(sentMessage, "turnNumber")
                    : GetAgentContainerInt(task, "turnNumber");
            }

            task = await PollAgentContainerTaskAsync(
                GetAgentContainerString(task, "id")!,
                status => string.Equals(status, "awaiting_input", StringComparison.OrdinalIgnoreCase)
                          || IsTerminalAgentContainerStatus(status),
                request,
                cancellationToken);
        }
        else
        {
            if (hasExisting)
            {
                var existing = await GetAgentContainerTaskAsync(existingTaskId, cancellationToken);
                ValidateAgentContainerIdentity(existing, route);
                var status = GetAgentContainerString(existing, "status");
                if (IsTerminalAgentContainerStatus(status))
                    throw new InvalidOperationException($"AgentContainer task '{existingTaskId}' is terminal ({status}) and cannot be continued.");
                if (!string.Equals(status, "hibernating", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"AgentContainer autonomous task '{existingTaskId}' must be hibernating before it can be woken; current status is '{status}'.");

                var payload = BuildAgentContainerPayload(request, WakeCommandKeys);
                payload["kind"] = "message";
                payload["message"] = prompt;
                commandResponse = await SendAgentContainerJsonAsync(
                    HttpMethod.Post,
                    $"tasks/{Uri.EscapeDataString(existingTaskId)}/wake",
                    payload,
                    "wake task",
                    idempotencyKey,
                    cancellationToken);
                task = commandResponse.Clone();
            }
            else
            {
                var payload = BuildAgentContainerPayload(request, CreateCommandKeys);
                payload.Remove("titleSource");
                payload["instructions"] = prompt;
                MergeInputFiles(payload, uploaded);
                commandResponse = await SendAgentContainerJsonAsync(
                    HttpMethod.Post,
                    $"agents/{Uri.EscapeDataString(route.AgentId)}/tasks",
                    payload,
                    "dispatch task",
                    idempotencyKey,
                    cancellationToken);
                task = commandResponse.Clone();
            }

            task = await PollAgentContainerTaskAsync(
                GetAgentContainerString(task, "id")!,
                status => string.Equals(status, "hibernating", StringComparison.OrdinalIgnoreCase)
                          || IsTerminalAgentContainerStatus(status),
                request,
                cancellationToken);
        }

        var taskId = GetAgentContainerString(task, "id")
                     ?? throw new InvalidOperationException("AgentContainer task response did not include id.");
        IReadOnlyList<JsonElement> messages = [];
        IReadOnlyList<JsonElement> activities = [];
        if (route.IsConversation)
        {
            messages = await ListAgentContainerConversationMessagesAsync(taskId, acceptedTurn, cancellationToken);
            activities = await ListAgentContainerConversationActivitiesAsync(taskId, acceptedTurn, cancellationToken);
        }

        var files = await DownloadAgentContainerOutputFilesAsync(taskId, cancellationToken);
        return new AgentContainerExecution(route, task, created, acceptedTurn, messages, activities, files, commandResponse.Clone());
    }

    private static string ResolveConversationInstructions(AIRequest request, string prompt)
    {
        var options = GetAgentContainerOptions(request);
        if (options.HasValue)
        {
            var configured = GetAgentContainerString(options.Value, "instructions");
            if (!string.IsNullOrWhiteSpace(configured))
                return configured;
        }
        return string.IsNullOrWhiteSpace(request.Instructions) ? prompt : request.Instructions!;
    }

    private static JsonElement RequireAgentContainerTask(JsonElement commandResponse)
        => TryGetAgentContainerProperty(commandResponse, "task", out var task) && task.ValueKind == JsonValueKind.Object
            ? task.Clone()
            : throw new InvalidOperationException("AgentContainer command response did not include task.");

    private async Task<JsonElement> GetAgentContainerTaskAsync(string taskId, CancellationToken cancellationToken)
        => await SendAgentContainerJsonAsync(
            HttpMethod.Get,
            $"tasks/{Uri.EscapeDataString(taskId)}",
            null,
            "get task",
            null,
            cancellationToken);

    private async Task<JsonElement> PollAgentContainerTaskAsync(
        string taskId,
        Func<string?, bool> isComplete,
        AIRequest request,
        CancellationToken cancellationToken)
        => await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            ct => GetAgentContainerTaskAsync(taskId, ct),
            task => isComplete(GetAgentContainerString(task, "status")),
            ResolveAgentContainerPollInterval(request),
            ResolveAgentContainerPollTimeout(request),
            maxAttempts: null,
            cancellationToken);

    private static void ValidateAgentContainerIdentity(JsonElement task, AgentContainerRoute route)
    {
        var taskAgentId = GetAgentContainerString(task, "agentId");
        if (!string.Equals(taskAgentId, route.AgentId, StringComparison.Ordinal))
            throw new InvalidOperationException($"AgentContainer task belongs to agent '{taskAgentId}', not requested agent '{route.AgentId}'.");
        var taskMode = GetAgentContainerString(task, "mode");
        var expectedMode = route.IsConversation ? "conversation" : "autonomous";
        if (!string.Equals(taskMode, expectedMode, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"AgentContainer task mode '{taskMode}' cannot be used through the '{route.Mode}' model.");
    }

    private async Task<IReadOnlyList<JsonElement>> ListAgentContainerConversationMessagesAsync(
        string taskId,
        int? turn,
        CancellationToken cancellationToken)
    {
        var results = await ListAllAgentContainerDataAsync(
            $"conversations/{Uri.EscapeDataString(taskId)}/messages",
            "list conversation messages",
            cancellationToken,
            extraQuery: "order=asc");
        return turn.HasValue
            ? results.Where(message => GetAgentContainerInt(message, "turnNumber") == turn.Value).ToList()
            : results;
    }

    private async Task<IReadOnlyList<JsonElement>> ListAgentContainerConversationActivitiesAsync(
        string taskId,
        int? turn,
        CancellationToken cancellationToken)
    {
        var extra = turn.HasValue ? $"order=asc&fromTurn={turn.Value}&toTurn={turn.Value}" : "order=asc";
        return await ListAllAgentContainerDataAsync(
            $"conversations/{Uri.EscapeDataString(taskId)}/activities",
            "list conversation activities",
            cancellationToken,
            extra);
    }

    private async Task<List<JsonElement>> ListAllAgentContainerDataAsync(
        string path,
        string operation,
        CancellationToken cancellationToken,
        string? extraQuery = null)
    {
        var results = new List<JsonElement>();
        string? cursor = null;
        do
        {
            var separator = path.Contains('?') ? "&" : "?";
            var uri = $"{path}{separator}limit=100";
            if (!string.IsNullOrWhiteSpace(extraQuery))
                uri += $"&{extraQuery}";
            if (!string.IsNullOrWhiteSpace(cursor))
                uri += $"&cursor={Uri.EscapeDataString(cursor)}";

            var root = await SendAgentContainerJsonAsync(HttpMethod.Get, uri, null, operation, null, cancellationToken);
            if (!TryGetAgentContainerProperty(root, "data", out var data) || data.ValueKind != JsonValueKind.Array)
                break;
            results.AddRange(data.EnumerateArray().Select(item => item.Clone()));
            cursor = TryGetAgentContainerProperty(root, "pageInfo", out var pageInfo)
                     && GetAgentContainerBoolean(pageInfo, "hasMore") == true
                ? GetAgentContainerString(pageInfo, "nextCursor")
                : null;
        }
        while (!string.IsNullOrWhiteSpace(cursor));
        return results;
    }
}
