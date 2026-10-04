using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.AppNZ;

public partial class AppNZProvider
{
    private const string AgentToolName = "appnz_execute_task";

    private async Task<AIResponse> ExecuteAgentAsync(AIRequest request, CancellationToken cancellationToken)
    {
        JsonElement final = default;
        Dictionary<string, object?>? payload = null;
        await foreach (var snapshot in RunAgentAsync(request, cancellationToken))
        {
            final = snapshot.Raw;
            payload = snapshot.Payload;
        }

        var task = GetAgentTask(final);
        var id = ReadString(task, "id")!;
        var failed = AgentFailed(task);
        var metadata = AgentMetadata(final);
        return new AIResponse
        {
            ProviderId = GetIdentifier(), Model = "appnz/agent", Status = failed ? "failed" : "completed",
            Metadata = metadata,
            Output = new AIOutput
            {
                Metadata = metadata,
                Items = [new AIOutputItem
                {
                    Type = "message", Role = "assistant", Metadata = metadata,
                    Content = [new AIToolCallContentPart
                    {
                        Type = "tool-call", ToolCallId = "appnz-task-" + id, ToolName = AgentToolName,
                        Title = "Run AppNZ agent task", Input = payload!, Output = AgentResult(final),
                        ProviderExecuted = true, State = failed ? "output-error" : "output-available", Metadata = metadata
                    }]
                }]
            }
        };
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamAgentAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var first = true;
        JsonElement final = default;
        string? callId = null;
        await foreach (var snapshot in RunAgentAsync(request, cancellationToken))
        {
            final = snapshot.Raw;
            var task = GetAgentTask(final);
            callId = "appnz-task-" + ReadString(task, "id");
            var providerMetadata = AgentProviderMetadata(final);
            if (first)
            {
                first = false;
                yield return AgentEvent("tool-input-available", callId, new AIToolInputAvailableEventData
                {
                    ToolName = AgentToolName, Title = "Run AppNZ agent task", Input = snapshot.Payload,
                    ProviderExecuted = true, ProviderMetadata = providerMetadata
                }, final);
            }

            yield return AgentEvent("tool-output-available", callId, new AIToolOutputAvailableEventData
            {
                ToolName = AgentToolName, Output = AgentResult(final), ProviderExecuted = true,
                Preliminary = !AgentTerminal(task), ProviderMetadata = providerMetadata
            }, final);
        }

        var failed = AgentFailed(GetAgentTask(final));
        if (failed)
            yield return AgentEvent("error", callId,
                new AIErrorEventData { ErrorText = ReadString(GetAgentTask(final), "error") ?? "AppNZ task failed or was cancelled." }, final);

        yield return AgentEvent("finish", callId, new AIFinishEventData
        {
            FinishReason = failed ? "error" : "stop", Model = "appnz/agent",
            CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            MessageMetadata = AIFinishMessageMetadata.Create("appnz/agent", DateTimeOffset.UtcNow,
                additionalProperties: new Dictionary<string, object?> { ["appnz"] = final.Clone() })
        }, final);
    }

    private async IAsyncEnumerable<(JsonElement Raw, Dictionary<string, object?> Payload)> RunAgentAsync(
        AIRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // No task ID is ever recovered from request history or provider options.
        var latest = request.Input?.Items?.LastOrDefault(item => item.Role == "user");
        var prompt = string.Join("\n", latest?.Content?.OfType<AITextContentPart>().Select(p => p.Text) ?? []);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("AppNZ agents require a non-empty latest user prompt.", nameof(request));
        if (prompt.Length > 20000)
            throw new ArgumentException("AppNZ agent prompts cannot exceed 20,000 characters.", nameof(request));
        if (latest?.Content?.Any(p => p is not AITextContentPart) == true)
            throw new NotSupportedException("AppNZ task creation accepts a text prompt, not attachments.");

        var payload = Options(request.Metadata);
        payload.Remove("taskId");
        payload.Remove("task_id");
        payload.Remove("sessionId");
        payload.Remove("session_id");
        payload["prompt"] = prompt;
        if (ReadOptionString(payload, "model") is "agent" or "appnz/agent")
            throw new ArgumentException("The task's AppNZ model option must be an upstream model, not appnz/agent.");

        // timeoutSeconds is a documented task setting and also bounds local monitoring.
        var timeout = TimeSpan.FromMinutes(30);
        if (payload.TryGetValue("timeoutSeconds", out var value))
        {
            var seconds = JsonSerializer.SerializeToElement(value, Json).GetDouble();
            if (!double.IsFinite(seconds) || seconds <= 0 || seconds > 86400)
                throw new ArgumentException("AppNZ timeoutSeconds must be between 0 and 86400 seconds.");
            timeout = TimeSpan.FromSeconds(seconds);
        }
        using var monitoring = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        monitoring.CancelAfter(timeout);
        using var client = CreateClient();
        var raw = await SendJsonAsync(client, HttpMethod.Post, "api/agents/tasks", payload, monitoring.Token);
        var taskId = ReadString(GetAgentTask(raw), "id")
            ?? throw new InvalidOperationException("AppNZ task creation did not return task.id.");
        var terminal = false;
        try
        {
            while (true)
            {
                terminal = AgentTerminal(GetAgentTask(raw));
                yield return (raw, payload);
                if (terminal)
                    yield break;

                var previous = raw.GetRawText();
                do
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(1500), monitoring.Token);
                    raw = await SendJsonAsync(client, HttpMethod.Get,
                        "api/agents/tasks/" + Uri.EscapeDataString(taskId), null, monitoring.Token);
                    if (ReadString(GetAgentTask(raw), "id") != taskId)
                        throw new InvalidOperationException("AppNZ returned a different task while polling.");
                } while (previous == raw.GetRawText() && !AgentTerminal(GetAgentTask(raw)));
            }
        }
        finally
        {
            // Best effort cleanup also applies when the consumer stops enumerating the stream.
            if (!terminal)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    await SendJsonAsync(client, HttpMethod.Post,
                        "api/agents/tasks/" + Uri.EscapeDataString(taskId) + "/cancel", new { }, cleanup.Token);
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException or JsonException)
                {
                    // Preserve the original cancellation/error; cancellation is not a task retry.
                }
            }
        }
    }

    private static JsonElement GetAgentTask(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object || !raw.TryGetProperty("task", out var task)
            || task.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("AppNZ returned an invalid task response envelope.");
        return task;
    }

    // Verified in AppNZ's public AgentStudio and AgentSdkPage clients. Review means ready for human review.
    private static bool AgentTerminal(JsonElement task)
        => ReadString(task, "status") is "review" or "done" or "failed" or "cancelled";

    private static bool AgentFailed(JsonElement task)
        => ReadString(task, "status") is "failed" or "cancelled";

    private static Dictionary<string, object?> AgentMetadata(JsonElement raw)
        => new() { ["appnz"] = new { raw = raw.Clone() } };

    private static Dictionary<string, Dictionary<string, object>> AgentProviderMetadata(JsonElement raw)
        => new() { ["appnz"] = new() { ["raw"] = raw.Clone() } };

    private static CallToolResult AgentResult(JsonElement raw)
        => new() { IsError = AgentFailed(GetAgentTask(raw)), StructuredContent = raw.Clone() };

    private AIStreamEvent AgentEvent(string type, string? id, object data, JsonElement raw)
        => new()
        {
            ProviderId = GetIdentifier(), Event = new AIEventEnvelope
            {
                Type = type, Id = id, Data = data, Timestamp = DateTimeOffset.UtcNow, Metadata = AgentMetadata(raw)
            }
        };
}
