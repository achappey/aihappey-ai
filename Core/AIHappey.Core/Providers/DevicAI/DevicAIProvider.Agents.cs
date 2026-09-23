using System.Diagnostics;
using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.DevicAI;

public partial class DevicAIProvider
{
    private async Task<DevicAIAgentExecution> RunAgentAsync(
        AIRequest request,
        DevicAIRoute route,
        CancellationToken cancellationToken)
    {
        RejectAgentAttachments(request);
        var hasExisting = TryFindThreadId(request, out var threadId);
        JsonElement command;
        JsonElement thread;
        var baselineMessageCount = 0;

        if (hasExisting)
        {
            thread = await GetThreadAsync(threadId, cancellationToken);
            ValidateThreadAgent(thread, route.Target);
            baselineMessageCount = GetArray(thread, "messages").Count;

            if (TryGetThreadApprovalDecision(request, out var status, out var comment))
            {
                command = await SendJsonAsync(
                    HttpMethod.Post,
                    $"agents/threads/{Uri.EscapeDataString(threadId)}/approval",
                    new { status, comment },
                    "handle thread approval",
                    cancellationToken);
            }
            else if (string.Equals(GetString(thread, "status"), "AWAITING_APPROVAL", StringComparison.OrdinalIgnoreCase))
            {
                command = thread.Clone();
                return new DevicAIAgentExecution(route, thread, false, baselineMessageCount, command);
            }
            else
            {
                command = await SendJsonAsync(
                    HttpMethod.Post,
                    $"agents/threads/{Uri.EscapeDataString(threadId)}/messages",
                    new { message = ExtractLatestUserText(request) },
                    "continue thread",
                    cancellationToken);
            }

            thread = GetString(command, "id") is not null && GetString(command, "status") is not null
                ? command.Clone()
                : await GetThreadAsync(threadId, cancellationToken);
        }
        else
        {
            var payload = BuildCreateThreadPayload(request);
            command = await SendJsonAsync(
                HttpMethod.Post,
                $"agents/{Uri.EscapeDataString(route.Target)}/threads",
                payload,
                "create thread",
                cancellationToken);
            thread = command.Clone();
            threadId = GetString(thread, "id")
                       ?? throw new InvalidOperationException("DevicAI thread response did not include id.");
            ValidateThreadAgent(thread, route.Target);
        }

        thread = await PollThreadUntilStableAsync(threadId, thread, request, cancellationToken);
        return new DevicAIAgentExecution(route, thread, !hasExisting, baselineMessageCount, command.Clone());
    }

    private static Dictionary<string, object?> BuildCreateThreadPayload(AIRequest request)
    {
        var messages = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            messages.Add(new { role = "system", content = request.Instructions });

        foreach (var item in request.Input?.Items ?? [])
        {
            var role = item.Role?.ToLowerInvariant();
            if (role is not ("user" or "assistant" or "system" or "tool"))
                continue;
            var text = JoinText(item.Content);
            if (string.IsNullOrWhiteSpace(text))
                continue;
            messages.Add(new { role, content = text });
        }

        if (messages.Count == 0 && !string.IsNullOrWhiteSpace(request.Input?.Text))
            messages.Add(new { role = "user", content = request.Input.Text });
        if (messages.Count == 0)
            throw new InvalidOperationException("DevicAI agent thread creation requires at least one text message.");

        var payload = new Dictionary<string, object?>
        {
            ["messages"] = messages,
            ["async"] = false
        };
        var options = GetDevicAIOptions(request);
        CopyOption(options, payload, "metadata");
        CopyOption(options, payload, "tenantId", "tenant_id");
        CopyOption(options, payload, "subtenantId", "subtenant_id");
        return payload;
    }

    private async Task<JsonElement> GetThreadAsync(string threadId, CancellationToken cancellationToken)
        => await SendJsonAsync(
            HttpMethod.Get,
            $"agents/threads/{Uri.EscapeDataString(threadId)}",
            null,
            "get thread",
            cancellationToken);

    private async Task<JsonElement> PollThreadUntilStableAsync(
        string threadId,
        JsonElement initial,
        AIRequest request,
        CancellationToken cancellationToken)
    {
        var thread = initial.Clone();
        var stopwatch = Stopwatch.StartNew();
        var timeout = ResolvePollTimeout(request);
        while (string.Equals(GetString(thread, "status"), "RUNNING", StringComparison.OrdinalIgnoreCase))
        {
            if (stopwatch.Elapsed >= timeout)
                throw new TimeoutException($"DevicAI thread '{threadId}' did not reach a stable status within {timeout}.");
            await Task.Delay(ResolvePollInterval(request), cancellationToken);
            thread = await GetThreadAsync(threadId, cancellationToken);
        }
        return thread;
    }

    private static void ValidateThreadAgent(JsonElement thread, string expectedAgentId)
    {
        var agentId = GetString(thread, "agentId");
        if (!string.IsNullOrWhiteSpace(agentId)
            && !string.Equals(agentId, expectedAgentId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"DevicAI thread belongs to agent '{agentId}', not requested agent '{expectedAgentId}'.");
    }

    private static bool TryGetThreadApprovalDecision(
        AIRequest request,
        out string status,
        out string? comment)
    {
        var options = GetDevicAIOptions(request);
        var configured = options.HasValue
            ? GetString(options.Value, "approvalStatus", "approval_status")
            : null;
        comment = options.HasValue
            ? GetString(options.Value, "approvalComment", "approval_comment")
            : null;
        if (string.Equals(configured, "APPROVED", StringComparison.OrdinalIgnoreCase)
            || string.Equals(configured, "REJECTED", StringComparison.OrdinalIgnoreCase))
        {
            status = configured.ToUpperInvariant();
            return true;
        }

        foreach (var tool in (request.Input?.Items ?? [])
                     .SelectMany(item => item.Content?.OfType<AIToolCallContentPart>() ?? []))
        {
            if (!string.Equals(tool.ToolName, ApproveThreadToolName, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(tool.State, "approval-responded", StringComparison.OrdinalIgnoreCase)
                || !tool.Approval?.Approved.HasValue == true)
                continue;
            status = tool.Approval.Approved == true ? "APPROVED" : "REJECTED";
            comment = tool.Approval.Reason;
            return true;
        }

        status = string.Empty;
        return false;
    }
}
