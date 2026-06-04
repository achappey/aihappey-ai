using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Hyperbrowser;

public partial class HyperbrowserProvider
{
    private static readonly JsonSerializerOptions HyperbrowserJson = JsonSerializerOptions.Web;
    private const int DefaultHyperbrowserPollIntervalMilliseconds = 1_000;
    private const int DefaultHyperbrowserPollTimeoutSeconds = 600;

    private async Task<AIResponse> ExecuteHyperbrowserTaskUnifiedAsync(AIRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ApplyAuthHeader();

        var target = ResolveHyperbrowserTaskTarget(request);
        var prompt = BuildHyperbrowserPrompt(request);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("Hyperbrowser requires a non-empty task derived from unified input or instructions.");

        var payload = BuildHyperbrowserTaskPayload(target, request, prompt);
        var submittedPayload = JsonSerializer.SerializeToElement(payload, HyperbrowserJson);
        HyperbrowserTaskStartResponse? created = null;
        var pollAttempt = 0;

        try
        {
            created = await StartHyperbrowserTaskAsync(target.Definition, payload, cancellationToken);
            var finalTask = await PollHyperbrowserTaskUntilTerminalAsync(
                target,
                created.JobId,
                request,
                created,
                attempt => pollAttempt = attempt,
                cancellationToken);

            return ToHyperbrowserUnifiedResponse(request, target, submittedPayload, created, finalTask, pollAttempt);
        }
        catch (OperationCanceledException) when (!string.IsNullOrWhiteSpace(created?.JobId))
        {
            await StopHyperbrowserTaskSafeAsync(target.Definition, created.JobId, CancellationToken.None);
            throw;
        }
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamHyperbrowserTaskUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ApplyAuthHeader();

        var target = ResolveHyperbrowserTaskTarget(request);
        var prompt = BuildHyperbrowserPrompt(request);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("Hyperbrowser requires a non-empty task derived from unified input or instructions.");

        var payload = BuildHyperbrowserTaskPayload(target, request, prompt);
        var submittedPayloadJson = JsonSerializer.Serialize(payload, HyperbrowserJson);
        var submittedPayload = JsonSerializer.SerializeToElement(payload, HyperbrowserJson);
        var providerId = GetIdentifier();
        var eventId = request.Id ?? $"hyperbrowser_{Guid.NewGuid():N}";
        var toolCallId = $"hb_{target.Definition.Kind.Replace('-', '_')}_{eventId}";
        var timestamp = DateTimeOffset.UtcNow;
        var metadata = BuildHyperbrowserBaseMetadata(request, target, submittedPayload, null, null, 0, toolCallId);
        HyperbrowserTaskStartResponse? created = null;
        HyperbrowserTaskResponse? task = null;
        var pollAttempt = 0;
        var emittedStepIndexes = new HashSet<int>();
        var liveUrlSourceEmitted = false;

        yield return CreateHyperbrowserStreamEvent(
            providerId,
            toolCallId,
            "tool-input-start",
            new AIToolInputStartEventData
            {
                ToolName = target.Definition.ToolName,
                Title = target.Definition.ToolTitle,
                ProviderExecuted = true,
                ProviderMetadata = CreateHyperbrowserToolProviderMetadata(providerId, target.Definition, toolCallId, "tool_use")
            },
            timestamp,
            metadata);

        yield return CreateHyperbrowserStreamEvent(
            providerId,
            toolCallId,
            "tool-input-delta",
            new AIToolInputDeltaEventData { InputTextDelta = submittedPayloadJson },
            timestamp,
            metadata);

        yield return CreateHyperbrowserStreamEvent(
            providerId,
            toolCallId,
            "tool-input-available",
            new AIToolInputAvailableEventData
            {
                ToolName = target.Definition.ToolName,
                Title = target.Definition.ToolTitle,
                ProviderExecuted = true,
                Input = submittedPayload,
                ProviderMetadata = CreateHyperbrowserToolProviderMetadata(providerId, target.Definition, toolCallId, "tool_use")
            },
            timestamp,
            metadata);

        created = await StartHyperbrowserTaskAsync(target.Definition, payload, cancellationToken);
        metadata = BuildHyperbrowserBaseMetadata(request, target, submittedPayload, created, null, pollAttempt, toolCallId);

        yield return CreateHyperbrowserStreamEvent(
            providerId,
            toolCallId,
            "tool-output-available",
            CreateHyperbrowserToolOutputEventData(providerId, target.Definition, created, null, toolCallId, preliminary: true, pollAttempt),
            DateTimeOffset.UtcNow,
            metadata);

        if (!string.IsNullOrWhiteSpace(created.LiveUrl) && !liveUrlSourceEmitted)
        {
            yield return CreateHyperbrowserLiveUrlSourceEvent(providerId, eventId, created.JobId, created.LiveUrl!, metadata);
            liveUrlSourceEmitted = true;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(GetPollInterval(target.Metadata), cancellationToken);
                pollAttempt++;
                task = await GetHyperbrowserTaskAsync(target.Definition, created.JobId, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                await StopHyperbrowserTaskSafeAsync(target.Definition, created.JobId, CancellationToken.None);
                throw;
            }

            metadata = BuildHyperbrowserBaseMetadata(request, target, submittedPayload, created, task, pollAttempt, toolCallId);

            yield return CreateHyperbrowserStreamEvent(
                providerId,
                toolCallId,
                "tool-output-available",
                CreateHyperbrowserToolOutputEventData(providerId, target.Definition, created, task, toolCallId, preliminary: !IsHyperbrowserTerminalStatus(task.Status), pollAttempt),
                DateTimeOffset.UtcNow,
                metadata);

            if (!string.IsNullOrWhiteSpace(task.LiveUrl) && !liveUrlSourceEmitted)
            {
                yield return CreateHyperbrowserLiveUrlSourceEvent(providerId, eventId, task.JobId, task.LiveUrl!, metadata);
                liveUrlSourceEmitted = true;
            }

            foreach (var stepEvent in target.Definition.CreateStepStreamEvents(
                          providerId,
                          eventId,
                          target.Definition,
                          task,
                          metadata,
                         emittedStepIndexes))
            {
                yield return stepEvent;
            }

            if (IsHyperbrowserTerminalStatus(task.Status))
                break;

            if (HasReachedPollLimit(target.Metadata, pollAttempt, created.CreatedAt))
                throw new TimeoutException($"Hyperbrowser {target.Definition.DisplayName} task '{created.JobId}' did not finish before the configured polling limit.");
        }

        if (task is null)
            yield break;

        if (IsHyperbrowserFailedStatus(task.Status))
        {
            yield return CreateHyperbrowserStreamEvent(
                providerId,
                eventId,
                "error",
                new AIErrorEventData { ErrorText = GetHyperbrowserFailureMessage(task) },
                DateTimeOffset.UtcNow,
                metadata);
        }

        var text = ExtractHyperbrowserFinalText(task, target.Definition);
        if (!string.IsNullOrWhiteSpace(text))
        {
            yield return CreateHyperbrowserStreamEvent(providerId, eventId, "text-start", new AITextStartEventData(), DateTimeOffset.UtcNow, metadata);

            foreach (var chunk in ChunkHyperbrowserText(text))
            {
                yield return CreateHyperbrowserStreamEvent(
                    providerId,
                    eventId,
                    "text-delta",
                    new AITextDeltaEventData { Delta = chunk },
                    DateTimeOffset.UtcNow,
                    metadata);
            }

            yield return CreateHyperbrowserStreamEvent(providerId, eventId, "text-end", new AITextEndEventData(), DateTimeOffset.UtcNow, metadata);
        }

        var response = ToHyperbrowserUnifiedResponse(request, target, submittedPayload, created!, task, pollAttempt, toolCallId);
        var usage = ExtractHyperbrowserUsage(task);
        yield return new AIStreamEvent
        {
            ProviderId = providerId,
            Event = new AIEventEnvelope
            {
                Type = "finish",
                Id = eventId,
                Timestamp = DateTimeOffset.UtcNow,
                Output = response.Output,
                Data = new AIFinishEventData
                {
                    FinishReason = ResolveHyperbrowserFinishReason(task.Status),
                    Model = response.Model?.ToModelId(GetIdentifier()),
                    CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    InputTokens = usage.InputTokens,
                    OutputTokens = usage.OutputTokens,
                    TotalTokens = usage.TotalTokens,
                    MessageMetadata = AIFinishMessageMetadata.Create(
                        response.Model ?? request.Model ?? target.Model,
                        DateTimeOffset.UtcNow,
                        usage: response.Usage,
                        inputTokens: usage.InputTokens,
                        outputTokens: usage.OutputTokens,
                        totalTokens: usage.TotalTokens,
                        additionalProperties: ToHyperbrowserFinishMessageMetadata(response.Metadata))
                }
            },
            Metadata = response.Metadata
        };
    }

    private static HyperbrowserTaskTarget ResolveHyperbrowserTaskTarget(AIRequest request)
    {
        var metadata = ResolveHyperbrowserMetadata(request.Metadata);
        var model = NormalizeHyperbrowserModel(request.Model);
        var explicitKind = metadata.AgentType ?? metadata.TaskType ?? metadata.Kind;
        var definitions = GetHyperbrowserTaskDefinitions();

        if (!string.IsNullOrWhiteSpace(explicitKind))
        {
            var explicitDefinition = definitions.FirstOrDefault(d => IsHyperbrowserTaskAlias(d, explicitKind));
            if (explicitDefinition is null)
                throw new InvalidOperationException($"Unsupported Hyperbrowser agent type '{explicitKind}'.");

            var llm = !string.IsNullOrWhiteSpace(metadata.Llm)
                ? metadata.Llm!
                : ResolveHyperbrowserLlmFromModel(model, explicitDefinition) ?? explicitDefinition.DefaultLlm;

            return new HyperbrowserTaskTarget(explicitDefinition, llm, model, metadata);
        }

        foreach (var definition in definitions)
        {
            if (string.Equals(model, definition.Kind, StringComparison.OrdinalIgnoreCase))
                return new HyperbrowserTaskTarget(definition, metadata.Llm ?? definition.DefaultLlm, model, metadata);

            var prefix = definition.Kind + "/";
            if (model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var llm = metadata.Llm ?? model[prefix.Length..];
                return new HyperbrowserTaskTarget(definition, string.IsNullOrWhiteSpace(llm) ? definition.DefaultLlm : llm, model, metadata);
            }
        }

        if (!string.IsNullOrWhiteSpace(metadata.Llm))
            return new HyperbrowserTaskTarget(HyperAgentTaskDefinition, metadata.Llm!, model, metadata);

        return new HyperbrowserTaskTarget(HyperAgentTaskDefinition, HyperAgentTaskDefinition.DefaultLlm, model, metadata);
    }

    private static IReadOnlyList<HyperbrowserTaskDefinition> GetHyperbrowserTaskDefinitions()
        => [HyperAgentTaskDefinition, BrowserUseTaskDefinition];

    private static bool IsHyperbrowserTaskAlias(HyperbrowserTaskDefinition definition, string value)
    {
        var normalized = value.Trim().Replace('_', '-');
        return string.Equals(normalized, definition.Kind, StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalized, definition.Kind.Replace("-", string.Empty), StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalized, definition.DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeHyperbrowserModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return string.Empty;

        var normalized = model.Trim();
        const string providerPrefix = "hyperbrowser/";
        if (normalized.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            normalized = normalized[providerPrefix.Length..];

        return normalized;
    }

    private static string? ResolveHyperbrowserLlmFromModel(string model, HyperbrowserTaskDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(model))
            return null;

        var prefix = definition.Kind + "/";
        if (model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return model[prefix.Length..];

        return definition.SupportedLlms.Contains(model, StringComparer.OrdinalIgnoreCase) ? model : null;
    }

    private static HyperbrowserTaskMetadata ResolveHyperbrowserMetadata(Dictionary<string, object?>? metadata)
    {
        if (metadata is null)
            return new HyperbrowserTaskMetadata();

        if (!metadata.TryGetValue("hyperbrowser", out var raw) || raw is null)
            return new HyperbrowserTaskMetadata();

        try
        {
            return JsonSerializer.Deserialize<HyperbrowserTaskMetadata>(JsonSerializer.Serialize(raw, HyperbrowserJson), HyperbrowserJson)
                   ?? new HyperbrowserTaskMetadata();
        }
        catch
        {
            return new HyperbrowserTaskMetadata();
        }
    }

    private static Dictionary<string, object?> BuildHyperbrowserTaskPayload(HyperbrowserTaskTarget target, AIRequest request, string task)
    {
        var metadata = target.Metadata;
        var payload = new Dictionary<string, object?>
        {
            ["task"] = task,
            ["llm"] = target.Llm,
            ["maxSteps"] = metadata.MaxSteps ?? request.MaxToolCalls,
            ["sessionId"] = metadata.SessionId,
            ["keepBrowserOpen"] = metadata.KeepBrowserOpen,
            ["sessionOptions"] = metadata.SessionOptions,
            ["useCustomApiKeys"] = metadata.UseCustomApiKeys,
            ["apiKeys"] = metadata.ApiKeys
        };

        if (string.IsNullOrWhiteSpace(metadata.Version) && !string.IsNullOrWhiteSpace(target.Definition.DefaultVersion))
            payload["version"] = target.Definition.DefaultVersion;

        target.Definition.ApplySpecificPayload(payload, metadata);

        if (metadata.ExtraBody is not null)
        {
            foreach (var item in metadata.ExtraBody)
                payload[item.Key] = item.Value.Clone();
        }

        return payload
            .Where(kvp => kvp.Value is not null)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    private static string BuildHyperbrowserPrompt(AIRequest request)
    {
        var lines = new List<string>();

        if (!string.IsNullOrWhiteSpace(request.Input?.Text))
            lines.Add(request.Input.Text);

        foreach (var item in request.Input?.Items ?? [])
        {
            var text = ExtractHyperbrowserContentText(item.Content);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            lines.Add($"{(string.IsNullOrWhiteSpace(item.Role) ? "user" : item.Role)}: {text}");
        }

        return string.Join("\n\n", lines).Trim();
    }

    private static string ExtractHyperbrowserContentText(IEnumerable<AIContentPart>? content)
        => string.Join("\n", (content ?? [])
            .OfType<AITextContentPart>()
            .Select(part => part.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text)));

    private async Task<HyperbrowserTaskStartResponse> StartHyperbrowserTaskAsync(
        HyperbrowserTaskDefinition definition,
        Dictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, HyperbrowserJson);
        using var request = new HttpRequestMessage(HttpMethod.Post, definition.Endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Hyperbrowser {definition.DisplayName} start failed ({(int)response.StatusCode}): {raw}");

        var result = JsonSerializer.Deserialize<HyperbrowserTaskStartResponse>(raw, HyperbrowserJson)
                     ?? throw new InvalidOperationException($"Hyperbrowser {definition.DisplayName} start returned empty payload.");

        if (string.IsNullOrWhiteSpace(result.JobId))
            throw new InvalidOperationException($"Hyperbrowser {definition.DisplayName} start response did not include jobId.");

        result.Raw = ParseHyperbrowserRaw(raw);
        result.CreatedAt = DateTimeOffset.UtcNow;
        return result;
    }

    private async Task<HyperbrowserTaskResponse> GetHyperbrowserTaskAsync(
        HyperbrowserTaskDefinition definition,
        string jobId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{definition.Endpoint}/{Uri.EscapeDataString(jobId)}");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Hyperbrowser {definition.DisplayName} poll failed ({(int)response.StatusCode}): {raw}");

        var result = JsonSerializer.Deserialize<HyperbrowserTaskResponse>(raw, HyperbrowserJson)
                     ?? throw new InvalidOperationException($"Hyperbrowser {definition.DisplayName} poll returned empty payload.");

        result.Raw = ParseHyperbrowserRaw(raw);
        return result;
    }

    private async Task StopHyperbrowserTaskSafeAsync(HyperbrowserTaskDefinition definition, string jobId, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, $"{definition.Endpoint}/{Uri.EscapeDataString(jobId)}/stop");
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            _ = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch
        {
            // best-effort cancellation cleanup
        }
    }

    private async Task<HyperbrowserTaskResponse> PollHyperbrowserTaskUntilTerminalAsync(
        HyperbrowserTaskTarget target,
        string jobId,
        AIRequest request,
        HyperbrowserTaskStartResponse created,
        Action<int>? updateAttempt,
        CancellationToken cancellationToken)
    {
        var pollAttempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            pollAttempt++;
            updateAttempt?.Invoke(pollAttempt);
            var task = await GetHyperbrowserTaskAsync(target.Definition, jobId, cancellationToken);
            if (IsHyperbrowserTerminalStatus(task.Status))
                return task;

            if (HasReachedPollLimit(target.Metadata, pollAttempt, created.CreatedAt))
                throw new TimeoutException($"Hyperbrowser {target.Definition.DisplayName} task '{jobId}' did not finish before the configured polling limit.");

            await Task.Delay(GetPollInterval(target.Metadata), cancellationToken);
        }

        throw new OperationCanceledException(cancellationToken);
    }

    private AIResponse ToHyperbrowserUnifiedResponse(
        AIRequest request,
        HyperbrowserTaskTarget target,
        JsonElement submittedPayload,
        HyperbrowserTaskStartResponse created,
        HyperbrowserTaskResponse task,
        int pollAttempt,
        string? toolCallId = null)
    {
        var metadata = BuildHyperbrowserBaseMetadata(request, target, submittedPayload, created, task, pollAttempt, toolCallId);
        var status = ResolveHyperbrowserResponseStatus(task.Status);
        var outputItems = new List<AIOutputItem>
        {
            new()
            {
                Type = "tool-call",
                Content =
                [
                    new AIToolCallContentPart
                    {
                        Type = "tool-call",
                        ToolCallId = toolCallId ?? $"hb_{target.Definition.Kind.Replace('-', '_')}_{created.JobId}",
                        ToolName = target.Definition.ToolName,
                        Title = target.Definition.ToolTitle,
                        Input = submittedPayload,
                        Output = CreateHyperbrowserTaskToolResult(created, task, pollAttempt),
                        State = IsHyperbrowserFailedStatus(task.Status) ? "output-error" : "output-available",
                        ProviderExecuted = true,
                        Metadata = new Dictionary<string, object?>
                        {
                            ["hyperbrowser.agent_type"] = target.Definition.Kind,
                            ["hyperbrowser.job_id"] = created.JobId,
                            ["hyperbrowser.status"] = task.Status,
                            ["hyperbrowser.poll_attempt"] = pollAttempt
                        }
                    }
                ]
            }
        };

        outputItems.AddRange(target.Definition.CreateStepOutputItems(target.Definition, created, task));

        var text = ExtractHyperbrowserFinalText(task, target.Definition);
        if (!string.IsNullOrWhiteSpace(text))
        {
            outputItems.Add(new AIOutputItem
            {
                Type = "message",
                Role = "assistant",
                Content = [new AITextContentPart { Type = "text", Text = text }]
            });
        }

        if (!string.IsNullOrWhiteSpace(task.LiveUrl ?? created.LiveUrl))
        {
            outputItems.Add(new AIOutputItem
            {
                Type = "source-url",
                Content = [new AITextContentPart { Type = "text", Text = task.LiveUrl ?? created.LiveUrl! }],
                Metadata = new Dictionary<string, object?>
                {
                    ["source.url"] = task.LiveUrl ?? created.LiveUrl,
                    ["source.title"] = "Live browser session",
                    ["chatcompletions.source.url"] = task.LiveUrl ?? created.LiveUrl,
                    ["chatcompletions.source.title"] = "Live browser session",
                    ["messages.source.url"] = task.LiveUrl ?? created.LiveUrl,
                    ["messages.source.title"] = "Live browser session",
                    ["hyperbrowser.source.type"] = "live_browser",
                    ["hyperbrowser.source.url"] = task.LiveUrl ?? created.LiveUrl,
                    ["hyperbrowser.job_id"] = created.JobId
                }
            });
        }

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = request.Model ?? $"hyperbrowser/{target.Definition.Kind}/{target.Llm}",
            Status = status,
            Output = new AIOutput { Items = outputItems },
            Usage = BuildHyperbrowserUsage(task),
            Metadata = metadata
        };
    }

    private Dictionary<string, object?> BuildHyperbrowserBaseMetadata(
        AIRequest request,
        HyperbrowserTaskTarget target,
        JsonElement submittedPayload,
        HyperbrowserTaskStartResponse? created,
        HyperbrowserTaskResponse? task,
        int pollAttempt,
        string? toolCallId)
    {
        var status = task?.Status ?? "pending";
        var jobId = task?.JobId ?? created?.JobId;
        var now = DateTimeOffset.UtcNow;
        var metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["hyperbrowser.agent_type"] = target.Definition.Kind,
            ["hyperbrowser.endpoint"] = target.Definition.Endpoint,
            ["hyperbrowser.llm"] = target.Llm,
            ["hyperbrowser.model"] = target.Model,
            ["hyperbrowser.job_id"] = jobId,
            ["hyperbrowser.status"] = status,
            ["hyperbrowser.live_url"] = task?.LiveUrl ?? created?.LiveUrl,
            ["hyperbrowser.submitted_payload"] = submittedPayload,
            ["hyperbrowser.poll_attempt"] = pollAttempt,
            ["hyperbrowser.tool_name"] = target.Definition.ToolName,
            ["hyperbrowser.tool_call_id"] = toolCallId,
            ["responses.id"] = jobId ?? Guid.NewGuid().ToString("N"),
            ["responses.object"] = "response",
            ["responses.created_at"] = created?.CreatedAt.ToUnixTimeSeconds() ?? now.ToUnixTimeSeconds(),
            ["responses.completed_at"] = task is not null && IsHyperbrowserTerminalStatus(status) ? now.ToUnixTimeSeconds() : null,
            ["responses.temperature"] = request.Temperature,
            ["responses.max_output_tokens"] = request.MaxOutputTokens,
            ["responses.error"] = task is not null && IsHyperbrowserFailedStatus(status)
                ? new Responses.ResponseResultError { Code = "hyperbrowser_task_failed", Message = GetHyperbrowserFailureMessage(task) }
                : null,
            ["chatcompletions.response.id"] = jobId,
            ["chatcompletions.response.object"] = "chat.completion",
            ["chatcompletions.response.created"] = created?.CreatedAt.ToUnixTimeSeconds() ?? now.ToUnixTimeSeconds(),
            ["chatcompletions.response.model"] = request.Model ?? $"hyperbrowser/{target.Definition.Kind}/{target.Llm}"
        };

        if (created?.Raw is not null)
            metadata["hyperbrowser.start_raw"] = created.Raw.Value;
        if (task?.Raw is not null)
            metadata["hyperbrowser.task_raw"] = task.Raw.Value;
        if (task?.Data is not null)
            metadata["hyperbrowser.data"] = task.Data.Value;
        if (task is not null)
        {
            var usage = ExtractHyperbrowserUsage(task);
            metadata["hyperbrowser.input_tokens"] = usage.InputTokens;
            metadata["hyperbrowser.output_tokens"] = usage.OutputTokens;
            metadata["hyperbrowser.total_tokens"] = usage.TotalTokens;
            metadata["hyperbrowser.num_task_steps_completed"] = usage.NumTaskStepsCompleted;
            metadata["responses.usage"] = BuildHyperbrowserUsage(task);
            metadata["chatcompletions.response.usage"] = BuildHyperbrowserChatCompletionsUsage(task);
        }
        if (task?.Metadata is not null)
            metadata["hyperbrowser.task_metadata"] = task.Metadata.Value;
        if (!string.IsNullOrWhiteSpace(task?.Error))
            metadata["hyperbrowser.error"] = task.Error;

        return metadata;
    }

    private static AIToolOutputAvailableEventData CreateHyperbrowserToolOutputEventData(
        string providerId,
        HyperbrowserTaskDefinition definition,
        HyperbrowserTaskStartResponse created,
        HyperbrowserTaskResponse? task,
        string toolCallId,
        bool preliminary,
        int pollAttempt)
        => new()
        {
            ToolName = definition.ToolName,
            Output = CreateHyperbrowserTaskToolResult(created, task, pollAttempt),
            ProviderExecuted = true,
            Preliminary = preliminary,
            Dynamic = true,
            ProviderMetadata = CreateHyperbrowserToolProviderMetadata(providerId, definition, toolCallId, "tool_result")
        };

    private static CallToolResult CreateHyperbrowserTaskToolResult(
        HyperbrowserTaskStartResponse created,
        HyperbrowserTaskResponse? task,
        int pollAttempt)
    {
        var structuredContent = JsonSerializer.SerializeToElement(new
        {
            jobId = task?.JobId ?? created.JobId,
            status = task?.Status ?? "pending",
            liveUrl = task?.LiveUrl ?? created.LiveUrl,
            metadata = task?.Metadata,
            data = task?.Data,
            finalResult = ExtractHyperbrowserFinalResult(task),
            error = task?.Error,
            pollAttempt
        }, HyperbrowserJson);

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = task?.Raw?.GetRawText() ?? created.Raw?.GetRawText() ?? JsonSerializer.Serialize(structuredContent, HyperbrowserJson) }],
            StructuredContent = structuredContent
        };
    }

    private static string BuildHyperbrowserStepToolCallId(string jobId, int stepIndex, int actionIndex, string? toolName)
        => $"hb_step_{SanitizeHyperbrowserIdentifier(jobId)}_{stepIndex}_action_{actionIndex}_{SanitizeHyperbrowserIdentifier(toolName ?? "action")}";

    private static string SanitizeHyperbrowserIdentifier(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
            builder.Append(char.IsLetterOrDigit(ch) ? ch : '_');

        return builder.ToString().Trim('_');
    }

    private static Dictionary<string, Dictionary<string, object>> CreateHyperbrowserToolProviderMetadata(
        string providerId,
        HyperbrowserTaskDefinition definition,
        string toolCallId,
        string type)
        => new()
        {
            [providerId] = new Dictionary<string, object>
            {
                ["type"] = type,
                ["agent_type"] = definition.Kind,
                ["tool_name"] = definition.ToolName,
                ["title"] = definition.ToolTitle,
                ["tool_use_id"] = toolCallId
            }
        };

    private static AIStreamEvent CreateHyperbrowserStreamEvent(
        string providerId,
        string eventId,
        string type,
        object data,
        DateTimeOffset timestamp,
        Dictionary<string, object?>? metadata)
        => new()
        {
            ProviderId = providerId,
            Event = new AIEventEnvelope
            {
                Type = type,
                Id = eventId,
                Timestamp = timestamp,
                Data = data
            },
            Metadata = metadata
        };

    private static AIStreamEvent CreateHyperbrowserLiveUrlSourceEvent(
        string providerId,
        string eventId,
        string jobId,
        string liveUrl,
        Dictionary<string, object?>? metadata)
        => CreateHyperbrowserStreamEvent(
            providerId,
            $"{eventId}_live_{jobId}",
            "source-url",
            new AISourceUrlEventData
            {
                SourceId = $"hyperbrowser-live-{jobId}",
                Url = liveUrl,
                Title = "Live browser session",
                Type = "live_browser",
                ProviderMetadata = new Dictionary<string, Dictionary<string, object>>
                {
                    [providerId] = new Dictionary<string, object>
                    {
                        ["job_id"] = jobId,
                        ["type"] = "live_browser"
                    }
                }
            },
            DateTimeOffset.UtcNow,
            metadata);

    private static bool IsHyperbrowserTerminalStatus(string? status)
        => string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "stopped", StringComparison.OrdinalIgnoreCase);

    private static bool IsHyperbrowserFailedStatus(string? status)
        => string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "stopped", StringComparison.OrdinalIgnoreCase);

    private static string ResolveHyperbrowserResponseStatus(string? status)
        => string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
            ? "completed"
            : IsHyperbrowserFailedStatus(status)
                ? "failed"
                : "in_progress";

    private static string ResolveHyperbrowserFinishReason(string? status)
        => string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
            ? "stop"
            : string.Equals(status, "stopped", StringComparison.OrdinalIgnoreCase)
                ? "cancelled"
                : "error";

    private static string GetHyperbrowserFailureMessage(HyperbrowserTaskResponse task)
        => !string.IsNullOrWhiteSpace(task.Error)
            ? task.Error!
            : $"Hyperbrowser task '{task.JobId}' finished with status '{task.Status}'.";

    private static string ExtractHyperbrowserFinalText(HyperbrowserTaskResponse task, HyperbrowserTaskDefinition definition)
    {
        var finalResult = ExtractHyperbrowserFinalResult(task);
        if (!string.IsNullOrWhiteSpace(finalResult))
            return finalResult!;

        if (!string.IsNullOrWhiteSpace(task.Error))
            return task.Error!;

        return $"Hyperbrowser {definition.DisplayName} task {task.JobId} finished with status {task.Status}.";
    }

    private static string? ExtractHyperbrowserFinalResult(HyperbrowserTaskResponse? task)
    {
        if (task?.Data is null || task.Data.Value.ValueKind != JsonValueKind.Object)
            return null;

        if (!task.Data.Value.TryGetProperty("finalResult", out var finalResult))
            return null;

        return finalResult.ValueKind == JsonValueKind.String
            ? finalResult.GetString()
            : finalResult.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                ? null
                : finalResult.GetRawText();
    }

    private static object BuildHyperbrowserUsage(HyperbrowserTaskResponse task)
    {
        var usage = ExtractHyperbrowserUsage(task);
        return new Dictionary<string, object?>
        {
            ["inputTokens"] = usage.InputTokens,
            ["outputTokens"] = usage.OutputTokens,
            ["totalTokens"] = usage.TotalTokens,
            ["prompt_tokens"] = usage.InputTokens,
            ["completion_tokens"] = usage.OutputTokens,
            ["total_tokens"] = usage.TotalTokens,
            ["hyperbrowserStatus"] = task.Status,
            ["stepCount"] = usage.StepCount,
            ["numTaskStepsCompleted"] = usage.NumTaskStepsCompleted
        };
    }

    private static object BuildHyperbrowserChatCompletionsUsage(HyperbrowserTaskResponse task)
    {
        var usage = ExtractHyperbrowserUsage(task);
        return new Dictionary<string, object?>
        {
            ["prompt_tokens"] = usage.InputTokens,
            ["completion_tokens"] = usage.OutputTokens,
            ["total_tokens"] = usage.TotalTokens
        };
    }

    private static HyperbrowserUsage ExtractHyperbrowserUsage(HyperbrowserTaskResponse task)
    {
        var inputTokens = task.Metadata is null ? null : TryGetInt(task.Metadata.Value, "inputTokens");
        var outputTokens = task.Metadata is null ? null : TryGetInt(task.Metadata.Value, "outputTokens");
        var stepCount = TryGetHyperbrowserStepCount(task);
        var numTaskStepsCompleted = task.Metadata is null ? null : TryGetInt(task.Metadata.Value, "numTaskStepsCompleted");

        return new HyperbrowserUsage(
            inputTokens,
            outputTokens,
            inputTokens is not null || outputTokens is not null ? (inputTokens ?? 0) + (outputTokens ?? 0) : null,
            stepCount,
            numTaskStepsCompleted ?? stepCount);
    }

    private static int? TryGetHyperbrowserStepCount(HyperbrowserTaskResponse task)
    {
        if (task.Data is null || task.Data.Value.ValueKind != JsonValueKind.Object)
            return null;

        return task.Data.Value.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array
            ? steps.GetArrayLength()
            : null;
    }

    private static JsonElement ParseHyperbrowserRaw(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.Clone();
        }
        catch
        {
            return JsonSerializer.SerializeToElement(new { raw }, HyperbrowserJson);
        }
    }

    private static TimeSpan GetPollInterval(HyperbrowserTaskMetadata metadata)
        => TimeSpan.FromMilliseconds(Math.Max(100, metadata.PollIntervalMilliseconds ?? DefaultHyperbrowserPollIntervalMilliseconds));

    private static bool HasReachedPollLimit(HyperbrowserTaskMetadata metadata, int pollAttempt, DateTimeOffset startedAt)
    {
        if (metadata.PollMaxAttempts is > 0 && pollAttempt >= metadata.PollMaxAttempts.Value)
            return true;

        var timeout = TimeSpan.FromSeconds(Math.Max(1, metadata.PollTimeoutSeconds ?? DefaultHyperbrowserPollTimeoutSeconds));
        return DateTimeOffset.UtcNow - startedAt >= timeout;
    }

    private static IEnumerable<string> ChunkHyperbrowserText(string text, int chunkSize = 96)
    {
        if (string.IsNullOrEmpty(text))
            yield break;

        for (var i = 0; i < text.Length; i += chunkSize)
            yield return text.Substring(i, Math.Min(chunkSize, text.Length - i));
    }

    private static Dictionary<string, object?>? ToHyperbrowserFinishMessageMetadata(Dictionary<string, object?>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
            return null;

        var result = new Dictionary<string, object?>();
        foreach (var item in metadata)
        {
            if (item.Value is not null)
                result[item.Key] = item.Value;
        }

        return result.Count == 0 ? null : result;
    }

    private static string? ExtractString(JsonElement root, string property)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(property, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value.GetRawText()
        };
    }

    private static int? TryGetInt(JsonElement root, string property)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(property, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var parsed) => parsed,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private sealed record HyperbrowserUsage(
        int? InputTokens,
        int? OutputTokens,
        int? TotalTokens,
        int? StepCount,
        int? NumTaskStepsCompleted);

    private sealed record HyperbrowserTaskDefinition(
        string Kind,
        string Endpoint,
        string ToolName,
        string ToolTitle,
        string DisplayName,
        string DefaultLlm,
        string DefaultVersion,
        IReadOnlyList<string> SupportedLlms,
        Action<Dictionary<string, object?>, HyperbrowserTaskMetadata> ApplySpecificPayload,
        Func<HyperbrowserTaskDefinition, HyperbrowserTaskStartResponse, HyperbrowserTaskResponse, IEnumerable<AIOutputItem>> CreateStepOutputItems,
        Func<string, string, HyperbrowserTaskDefinition, HyperbrowserTaskResponse, Dictionary<string, object?>?, HashSet<int>, IEnumerable<AIStreamEvent>> CreateStepStreamEvents);

    private sealed record HyperbrowserTaskTarget(
        HyperbrowserTaskDefinition Definition,
        string Llm,
        string Model,
        HyperbrowserTaskMetadata Metadata);

    private sealed class HyperbrowserTaskStartResponse
    {
        [JsonPropertyName("jobId")]
        public string JobId { get; set; } = default!;

        [JsonPropertyName("liveUrl")]
        public string? LiveUrl { get; set; }

        [JsonPropertyName("metadata")]
        public JsonElement? Metadata { get; set; }

        [JsonIgnore]
        public JsonElement? Raw { get; set; }

        [JsonIgnore]
        public DateTimeOffset CreatedAt { get; set; }
    }

    private sealed class HyperbrowserTaskResponse
    {
        [JsonPropertyName("jobId")]
        public string JobId { get; set; } = default!;

        [JsonPropertyName("status")]
        public string Status { get; set; } = default!;

        [JsonPropertyName("data")]
        public JsonElement? Data { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("liveUrl")]
        public string? LiveUrl { get; set; }

        [JsonPropertyName("metadata")]
        public JsonElement? Metadata { get; set; }

        [JsonIgnore]
        public JsonElement? Raw { get; set; }
    }

    private sealed class HyperbrowserTaskMetadata
    {
        [JsonPropertyName("agentType")]
        public string? AgentType { get; set; }

        [JsonPropertyName("taskType")]
        public string? TaskType { get; set; }

        [JsonPropertyName("kind")]
        public string? Kind { get; set; }

        [JsonPropertyName("llm")]
        public string? Llm { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("sessionId")]
        public string? SessionId { get; set; }

        [JsonPropertyName("maxSteps")]
        public int? MaxSteps { get; set; }

        [JsonPropertyName("keepBrowserOpen")]
        public bool? KeepBrowserOpen { get; set; }

        [JsonPropertyName("sessionOptions")]
        public JsonElement? SessionOptions { get; set; }

        [JsonPropertyName("useCustomApiKeys")]
        public bool? UseCustomApiKeys { get; set; }

        [JsonPropertyName("apiKeys")]
        public JsonElement? ApiKeys { get; set; }

        [JsonPropertyName("pollIntervalMilliseconds")]
        public int? PollIntervalMilliseconds { get; set; }

        [JsonPropertyName("pollTimeoutSeconds")]
        public int? PollTimeoutSeconds { get; set; }

        [JsonPropertyName("pollMaxAttempts")]
        public int? PollMaxAttempts { get; set; }

        [JsonPropertyName("validateOutput")]
        public bool? ValidateOutput { get; set; }

        [JsonPropertyName("useVision")]
        public bool? UseVision { get; set; }

        [JsonPropertyName("useVisionForPlanner")]
        public bool? UseVisionForPlanner { get; set; }

        [JsonPropertyName("maxActionsPerStep")]
        public int? MaxActionsPerStep { get; set; }

        [JsonPropertyName("maxInputTokens")]
        public int? MaxInputTokens { get; set; }

        [JsonPropertyName("plannerLlm")]
        public string? PlannerLlm { get; set; }

        [JsonPropertyName("pageExtractionLlm")]
        public string? PageExtractionLlm { get; set; }

        [JsonPropertyName("plannerInterval")]
        public int? PlannerInterval { get; set; }

        [JsonPropertyName("maxFailures")]
        public int? MaxFailures { get; set; }

        [JsonPropertyName("initialActions")]
        public JsonElement? InitialActions { get; set; }

        [JsonPropertyName("sensitiveData")]
        public JsonElement? SensitiveData { get; set; }

        [JsonPropertyName("messageContext")]
        public string? MessageContext { get; set; }

        [JsonPropertyName("extraBody")]
        public Dictionary<string, JsonElement>? ExtraBody { get; set; }
    }
}
