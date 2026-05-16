using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Tembo;

public partial class TemboProvider
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MinimumPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultPollTimeout = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateUnifiedRequest(request);

        ApplyAuthHeader();

        var prompt = BuildPrompt(request);
        var providerOptions = GetProviderOptions(request.Metadata);
        var execution = await ExecuteTemboAsync(request, prompt, providerOptions, cancellationToken);
        return CreateUnifiedResponse(request, execution);
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateUnifiedRequest(request);

        ApplyAuthHeader();

        var providerId = GetIdentifier();
        var eventId = request.Id ?? Guid.NewGuid().ToString("N");
        var prompt = BuildPrompt(request);
        var providerOptions = GetProviderOptions(request.Metadata);
        var plan = CreateExecutionPlan(request, prompt, providerOptions, eventId);
        var timestamp = DateTimeOffset.UtcNow;
        var submittedPayloadJson = JsonSerializer.Serialize(plan.Payload, JsonOptions);
        var submittedPayload = JsonSerializer.SerializeToElement(plan.Payload, JsonOptions);

        yield return CreateStreamEvent(
            providerId,
            plan.ToolCallId,
            "tool-input-start",
            new AIToolInputStartEventData
            {
                ToolName = plan.ToolName,
                Title = plan.ToolTitle,
                ProviderExecuted = true,
                ProviderMetadata = CreateToolProviderMetadata(providerId, plan.ToolName, plan.ToolTitle, plan.ToolCallId, "tool_use")
            },
            timestamp,
            null);

        yield return CreateStreamEvent(
            providerId,
            plan.ToolCallId,
            "tool-input-delta",
            new AIToolInputDeltaEventData
            {
                InputTextDelta = submittedPayloadJson
            },
            timestamp,
            null);

        yield return CreateStreamEvent(
            providerId,
            plan.ToolCallId,
            "tool-input-available",
            new AIToolInputAvailableEventData
            {
                ToolName = plan.ToolName,
                Title = plan.ToolTitle,
                ProviderExecuted = true,
                Input = submittedPayload,
                ProviderMetadata = CreateToolProviderMetadata(providerId, plan.ToolName, plan.ToolTitle, plan.ToolCallId, "tool_use")
            },
            timestamp,
            null);

        var execution = await SubmitTemboAsync(plan, cancellationToken);

        yield return CreateStreamEvent(
            providerId,
            plan.ToolCallId,
            "tool-output-available",
            CreateToolOutputEventData(providerId, execution, preliminary: ShouldPoll(execution)),
            DateTimeOffset.UtcNow,
            null);

        var latestStatusText = GetStreamingStatusText(execution);
        var textStarted = false;

        if (!string.IsNullOrWhiteSpace(latestStatusText) && ShouldPoll(execution))
        {
            yield return CreateStreamEvent(
                providerId,
                eventId,
                "text-start",
                new AITextStartEventData(),
                DateTimeOffset.UtcNow,
                null);

            textStarted = true;

            yield return CreateStreamEvent(
                providerId,
                eventId,
                "text-delta",
                new AITextDeltaEventData { Delta = latestStatusText },
                DateTimeOffset.UtcNow,
                null);
        }

        await foreach (var updatedExecution in PollTemboStreamAsync(execution, providerOptions, cancellationToken))
        {
            execution = updatedExecution;

            yield return CreateStreamEvent(
                providerId,
                plan.ToolCallId,
                "tool-output-available",
                CreateToolOutputEventData(providerId, execution, preliminary: ShouldPoll(execution)),
                DateTimeOffset.UtcNow,
                null);

            var statusText = GetStreamingStatusText(execution);
            if (!string.IsNullOrWhiteSpace(statusText)
                && !string.Equals(statusText, latestStatusText, StringComparison.Ordinal))
            {
                if (!textStarted)
                {
                    yield return CreateStreamEvent(
                        providerId,
                        eventId,
                        "text-start",
                        new AITextStartEventData(),
                        DateTimeOffset.UtcNow,
                        null);

                    textStarted = true;
                }

                latestStatusText = statusText;

                yield return CreateStreamEvent(
                    providerId,
                    eventId,
                    "text-delta",
                    new AITextDeltaEventData { Delta = "\n\n" + statusText },
                    DateTimeOffset.UtcNow,
                    null);
            }
        }

        EnsureSuccessfulExecution(execution);

        var response = CreateUnifiedResponse(request, execution);
        var metadata = response.Metadata;
        var completionText = BuildCompletionText(execution);

        if (!string.IsNullOrWhiteSpace(execution.HtmlUrl))
        {
            yield return CreateStreamEvent(
                providerId,
                $"{eventId}_source_tembo",
                "source-url",
                new AISourceUrlEventData
                {
                    SourceId = execution.HtmlUrl!,
                    Url = execution.HtmlUrl!,
                    Title = execution.SessionTitle ?? "Tembo session",
                    Type = "session",
                    ProviderMetadata = new Dictionary<string, Dictionary<string, object>>
                    {
                        [providerId] = new Dictionary<string, object>
                        {
                            ["type"] = "session",
                            ["title"] = execution.SessionTitle ?? "Tembo session",
                            ["url"] = execution.HtmlUrl!
                        }
                    }
                },
                DateTimeOffset.UtcNow,
                metadata);
        }

        if (!textStarted)
        {
            yield return CreateStreamEvent(
                providerId,
                eventId,
                "text-start",
                new AITextStartEventData(),
                DateTimeOffset.UtcNow,
                metadata);

            textStarted = true;
        }

        yield return CreateStreamEvent(
            providerId,
            eventId,
            "text-delta",
            new AITextDeltaEventData { Delta = textStarted && !string.IsNullOrWhiteSpace(latestStatusText) ? "\n\n" + completionText : completionText },
            DateTimeOffset.UtcNow,
            metadata);

        yield return CreateStreamEvent(
            providerId,
            eventId,
            "text-end",
            new AITextEndEventData(),
            DateTimeOffset.UtcNow,
            metadata);

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
                    FinishReason = IsSuccessfulExecution(execution) ? "stop" : "error",
                    Model = response.Model,
                    CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    MessageMetadata = AIFinishMessageMetadata.Create(
                        response.Model ?? request.Model ?? "tembo",
                        DateTimeOffset.UtcNow,
                        additionalProperties: ToMessageMetadata(metadata))
                }
            },
            Metadata = metadata
        };
    }

    private async Task<TemboExecutionResult> ExecuteTemboAsync(
        AIRequest request,
        string prompt,
        JsonElement? providerOptions,
        CancellationToken cancellationToken)
    {
        var plan = CreateExecutionPlan(request, prompt, providerOptions, request.Id ?? Guid.NewGuid().ToString("N"));
        var execution = await SubmitTemboAsync(plan, cancellationToken);

        await foreach (var updatedExecution in PollTemboStreamAsync(execution, providerOptions, cancellationToken))
            execution = updatedExecution;

        EnsureSuccessfulExecution(execution);
        return execution;
    }

    private TemboExecutionPlan CreateExecutionPlan(AIRequest request, string prompt, JsonElement? providerOptions, string eventId)
    {
        var localModel = NormalizeModel(request.Model);
        var keyOrId = providerOptions is JsonElement options ? TryReadString(options, "keyOrId") : null;
        var isAutomation = string.Equals(localModel, "agent", StringComparison.OrdinalIgnoreCase);

        if (isAutomation)
        {
            if (string.IsNullOrWhiteSpace(keyOrId))
                throw new InvalidOperationException("Tembo agent model requires providerOptions.tembo.keyOrId.");

            return new TemboExecutionPlan(
                TemboExecutionKind.AutomationTrigger,
                keyOrId!,
                "tembo_automation",
                "Tembo automation",
                $"tembo_automation_{eventId}",
                CreateAutomationPayload(request, prompt, providerOptions));
        }

        return new TemboExecutionPlan(
            TemboExecutionKind.SessionCreate,
            null,
            "tembo_session",
            "Tembo session",
            $"tembo_session_{eventId}",
            CreateSessionPayload(request, prompt, localModel, providerOptions));
    }

    private async Task<TemboExecutionResult> SubmitTemboAsync(TemboExecutionPlan plan, CancellationToken cancellationToken)
        => plan.Kind switch
        {
            TemboExecutionKind.AutomationTrigger => await TriggerAutomationAsync(plan, cancellationToken),
            _ => await CreateSessionAsync(plan, cancellationToken)
        };

    private async Task<TemboExecutionResult> CreateSessionAsync(TemboExecutionPlan plan, CancellationToken cancellationToken)
    {
        var submittedPayloadJson = JsonSerializer.Serialize(plan.Payload, JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, "session/create")
        {
            Content = new StringContent(submittedPayloadJson, Encoding.UTF8, "application/json")
        };

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Tembo session/create error: {(int)response.StatusCode} {response.ReasonPhrase}: {ExtractErrorMessage(raw)}");

        var session = JsonSerializer.Deserialize<TemboSession>(raw, JsonOptions)
            ?? throw new InvalidOperationException("Tembo returned an empty session/create response.");

        return TemboExecutionResult.ForSession(plan, submittedPayloadJson, raw, session with { RawJson = raw });
    }

    private async Task<TemboExecutionResult> TriggerAutomationAsync(TemboExecutionPlan plan, CancellationToken cancellationToken)
    {
        var submittedPayloadJson = JsonSerializer.Serialize(plan.Payload, JsonOptions);
        var keyOrId = Uri.EscapeDataString(plan.KeyOrId ?? throw new InvalidOperationException("Tembo automation keyOrId is missing."));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"automation/{keyOrId}/trigger")
        {
            Content = new StringContent(submittedPayloadJson, Encoding.UTF8, "application/json")
        };

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Tembo automation trigger error: {(int)response.StatusCode} {response.ReasonPhrase}: {ExtractErrorMessage(raw)}");

        var job = JsonSerializer.Deserialize<TemboAutomationJob>(raw, JsonOptions)
            ?? throw new InvalidOperationException("Tembo returned an empty automation trigger response.");

        return TemboExecutionResult.ForAutomation(plan, submittedPayloadJson, raw, job with { RawJson = raw });
    }

    private async IAsyncEnumerable<TemboExecutionResult> PollTemboStreamAsync(
        TemboExecutionResult execution,
        JsonElement? providerOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!ShouldPoll(execution))
            yield break;

        var interval = ResolvePollInterval(providerOptions);
        var timeout = ResolvePollTimeout(providerOptions);
        var startedAt = DateTimeOffset.UtcNow;
        TemboSession? trackedSession = execution.Session;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (DateTimeOffset.UtcNow - startedAt >= timeout)
                throw new TimeoutException($"Tembo polling exceeded timeout ({timeout}).");

            await Task.Delay(interval, cancellationToken);

            var list = await ListSessionsAsync(cancellationToken);
            var nextSession = ResolveTrackedSession(execution, trackedSession, list.Issues);

            if (nextSession is null)
                continue;

            trackedSession = nextSession;
            execution = execution with
            {
                Session = nextSession,
                FinalRawJson = nextSession.RawJson,
                FinalRaw = TryParseJsonElement(nextSession.RawJson)
            };

            yield return execution;

            if (!ShouldPoll(execution))
                yield break;
        }
    }

    private async Task<TemboSessionListResponse> ListSessionsAsync(CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync("session/list?limit=100&page=1", cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Tembo session/list error: {(int)response.StatusCode} {response.ReasonPhrase}: {ExtractErrorMessage(raw)}");

        var list = JsonSerializer.Deserialize<TemboSessionListResponse>(raw, JsonOptions)
            ?? throw new InvalidOperationException("Tembo returned an empty session/list response.");

        list.RawJson = raw;

        foreach (var issue in list.Issues ?? [])
            issue.RawJson = JsonSerializer.Serialize(issue, JsonOptions);

        return list;
    }

    private static TemboSession? ResolveTrackedSession(
        TemboExecutionResult execution,
        TemboSession? trackedSession,
        IReadOnlyList<TemboSession>? issues)
    {
        if (issues is null || issues.Count == 0)
            return null;

        var existingId = trackedSession?.Id ?? execution.SessionId;
        if (!string.IsNullOrWhiteSpace(existingId))
        {
            var match = issues.FirstOrDefault(issue => string.Equals(issue.Id, existingId, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }

        if (execution.AutomationJob is not null)
        {
            var jobIdMatch = issues.FirstOrDefault(issue => string.Equals(issue.Id, execution.AutomationJob.Id, StringComparison.OrdinalIgnoreCase));
            if (jobIdMatch is not null)
                return jobIdMatch;

            if (execution.AutomationJob.CreatedAt is DateTimeOffset createdAt)
            {
                var nearCreated = issues
                    .Where(issue => issue.CreatedAt is DateTimeOffset sessionCreatedAt && sessionCreatedAt >= createdAt.AddMinutes(-1))
                    .OrderByDescending(issue => issue.CreatedAt)
                    .FirstOrDefault();

                if (nearCreated is not null)
                    return nearCreated;
            }
        }

        return null;
    }

    private AIResponse CreateUnifiedResponse(AIRequest request, TemboExecutionResult execution)
    {
        var metadata = CreateResponseMetadata(request, execution);
        var text = BuildCompletionText(execution);
        var model = request.Model?.ToModelId(GetIdentifier()) ?? GetIdentifier();

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = model,
            Status = IsSuccessfulExecution(execution) ? "completed" : "failed",
            Output = new AIOutput
            {
                Items =
                [
                    new AIOutputItem
                    {
                        Type = "tool-call",
                        Content =
                        [
                            new AIToolCallContentPart
                            {
                                Type = "tool-call",
                                ToolCallId = execution.ToolCallId,
                                ToolName = execution.ToolName,
                                Title = execution.ToolTitle,
                                Input = execution.SubmittedPayload,
                                Output = CreateToolCallResult(execution),
                                State = "output-available",
                                ProviderExecuted = true,
                                Metadata = new Dictionary<string, object?>
                                {
                                    [$"{GetIdentifier()}.tool_name"] = execution.ToolName,
                                    [$"{GetIdentifier()}.session_id"] = execution.SessionId,
                                    [$"{GetIdentifier()}.automation_job_id"] = execution.AutomationJob?.Id
                                }
                            }
                        ]
                    },
                    new AIOutputItem
                    {
                        Type = "message",
                        Role = "assistant",
                        Content =
                        [
                            new AITextContentPart
                            {
                                Type = "text",
                                Text = text
                            }
                        ]
                    }
                ]
            },
            Metadata = metadata
        };
    }

    private Dictionary<string, object?> CreateResponseMetadata(AIRequest request, TemboExecutionResult execution)
    {
        var metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["tembo.kind"] = execution.Kind.ToString(),
            ["tembo.tool_name"] = execution.ToolName,
            ["tembo.tool_call_id"] = execution.ToolCallId,
            ["tembo.session_id"] = execution.SessionId,
            ["tembo.session_status"] = execution.SessionStatus,
            ["tembo.session_title"] = execution.SessionTitle,
            ["tembo.session_url"] = execution.HtmlUrl,
            ["tembo.automation_job_id"] = execution.AutomationJob?.Id,
            ["tembo.automation_job_status"] = execution.AutomationJob?.Status,
            ["tembo.automation_job_task"] = execution.AutomationJob?.Task,
            ["tembo.submitted_payload"] = execution.SubmittedPayload,
            ["tembo.initial.raw"] = execution.InitialRaw,
            ["tembo.final.raw"] = execution.FinalRaw
        };

        if (!string.IsNullOrWhiteSpace(request.Model))
            metadata["tembo.requested_model"] = request.Model;

        return metadata;
    }

    private static Dictionary<string, object?> CreateSessionPayload(
        AIRequest request,
        string prompt,
        string? localModel,
        JsonElement? providerOptions)
    {
        var payload = MergeProviderOptions(providerOptions, ["keyOrId", "payload", "pollIntervalSeconds", "pollTimeoutSeconds"]);

        if (!payload.ContainsKey("prompt") && !payload.ContainsKey("description"))
            payload["prompt"] = prompt;

        if (!payload.ContainsKey("agent") && !string.IsNullOrWhiteSpace(localModel))
            payload["agent"] = localModel.Contains(':', StringComparison.Ordinal) ? localModel : $"claudeCode:{localModel}";

        if (!payload.ContainsKey("queueRightAway"))
            payload["queueRightAway"] = true;

        if (!string.IsNullOrWhiteSpace(request.Instructions) && !payload.ContainsKey("description"))
            payload["description"] = request.Instructions;

        return payload;
    }

    private static Dictionary<string, object?> CreateAutomationPayload(AIRequest request, string prompt, JsonElement? providerOptions)
    {
        if (providerOptions is JsonElement options
            && options.ValueKind == JsonValueKind.Object
            && options.TryGetProperty("payload", out var payload)
            && payload.ValueKind == JsonValueKind.Object)
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(payload.GetRawText(), JsonOptions) ?? [];
        }

        var result = MergeProviderOptions(providerOptions, ["keyOrId", "payload", "pollIntervalSeconds", "pollTimeoutSeconds"]);

        if (!result.ContainsKey("prompt"))
            result["prompt"] = prompt;

        if (!result.ContainsKey("description") && !string.IsNullOrWhiteSpace(request.Instructions))
            result["description"] = request.Instructions;

        if (!result.ContainsKey("model") && !string.IsNullOrWhiteSpace(request.Model))
            result["model"] = request.Model;

        return result;
    }

    private static Dictionary<string, object?> MergeProviderOptions(JsonElement? providerOptions, HashSet<string> excluded)
    {
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (providerOptions is not JsonElement options || options.ValueKind != JsonValueKind.Object)
            return payload;

        foreach (var property in options.EnumerateObject())
        {
            if (excluded.Contains(property.Name))
                continue;

            payload[property.Name] = JsonSerializer.Deserialize<object?>(property.Value.GetRawText(), JsonOptions);
        }

        return payload;
    }

    private static void ValidateUnifiedRequest(AIRequest request)
    {
        if (request.Tools is { Count: > 0 })
            throw new NotSupportedException("Tembo unified mode does not accept upstream tool definitions; it exposes Tembo execution as a synthetic provider-executed tool event stream instead.");

        if (request.ToolChoice is not null)
            throw new NotSupportedException("Tembo unified mode does not support upstream tool choice overrides.");

        if (request.ResponseFormat is not null)
            throw new NotSupportedException("Tembo unified mode does not support structured response format negotiation.");
    }

    private static string BuildPrompt(AIRequest request)
    {
        var instructionSections = new List<string>();
        var conversationSections = new List<string>();

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            instructionSections.Add(request.Instructions.Trim());

        if (!string.IsNullOrWhiteSpace(request.Input?.Text))
            conversationSections.Add(FormatConversationBlock("user", request.Input.Text!));

        if (request.Input?.Items is not null)
        {
            foreach (var item in request.Input.Items)
            {
                ValidateInputItem(item);

                var text = ExtractSupportedText(item.Content);
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                var role = (item.Role ?? "user").Trim().ToLowerInvariant();
                switch (role)
                {
                    case "system":
                        instructionSections.Add(text);
                        break;
                    case "user":
                    case "assistant":
                        conversationSections.Add(FormatConversationBlock(role, text));
                        break;
                    default:
                        throw new NotSupportedException($"Tembo unified mode only supports system, user, and assistant message roles. Role '{item.Role}' is not supported.");
                }
            }
        }

        var sections = new List<string>();

        if (instructionSections.Count > 0)
            sections.Add("instructions:\n" + string.Join("\n\n", instructionSections));

        sections.AddRange(conversationSections);

        var prompt = string.Join("\n\n", sections.Where(section => !string.IsNullOrWhiteSpace(section))).Trim();

        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("Tembo unified mode requires text input.");

        return prompt;
    }

    private static void ValidateInputItem(AIInputItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Type)
            && !string.Equals(item.Type, "message", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"Tembo unified mode only supports message input items. Item type '{item.Type}' is not supported.");
        }
    }

    private static string ExtractSupportedText(List<AIContentPart>? content)
    {
        if (content is null || content.Count == 0)
            return string.Empty;

        var textParts = new List<string>();

        foreach (var part in content)
        {
            switch (part)
            {
                case AITextContentPart textPart when !string.IsNullOrWhiteSpace(textPart.Text):
                    textParts.Add(textPart.Text.Trim());
                    break;
                case AIReasoningContentPart:
                    break;
                case AIFileContentPart:
                    throw new NotSupportedException("Tembo unified mode does not support file or image content parts.");
                case AIToolCallContentPart:
                    throw new NotSupportedException("Tembo unified mode does not support inbound tool call content parts.");
                case null:
                    break;
                default:
                    throw new NotSupportedException($"Tembo unified mode does not support content part type '{part.Type}'.");
            }
        }

        return string.Join("\n\n", textParts);
    }

    private static string FormatConversationBlock(string role, string text)
        => $"{role}: {text.Trim()}";

    private static string NormalizeModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return string.Empty;

        var trimmed = model.Trim();
        if (!trimmed.Contains('/', StringComparison.Ordinal))
            return trimmed;

        var split = trimmed.SplitModelId();
        return split.Model;
    }

    private static JsonElement? GetProviderOptions(Dictionary<string, object?>? metadata)
    {
        if (metadata is null || !metadata.TryGetValue("tembo", out var value) || value is null)
            return null;

        if (value is JsonElement json)
            return json.Clone();

        return JsonSerializer.SerializeToElement(value, JsonOptions);
    }

    private static string? TryReadString(JsonElement options, string propertyName)
    {
        if (options.ValueKind != JsonValueKind.Object || !options.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            _ => null
        };
    }

    private static TimeSpan ResolvePollInterval(JsonElement? providerOptions)
    {
        var seconds = TryReadDouble(providerOptions, "pollIntervalSeconds");
        if (seconds is null || seconds.Value <= 0)
            return DefaultPollInterval;

        var requested = TimeSpan.FromSeconds(seconds.Value);
        return requested < MinimumPollInterval ? MinimumPollInterval : requested;
    }

    private static TimeSpan ResolvePollTimeout(JsonElement? providerOptions)
    {
        var seconds = TryReadDouble(providerOptions, "pollTimeoutSeconds");
        if (seconds is null || seconds.Value <= 0)
            return DefaultPollTimeout;

        return TimeSpan.FromSeconds(seconds.Value);
    }

    private static double? TryReadDouble(JsonElement? providerOptions, string propertyName)
    {
        if (providerOptions is not JsonElement options
            || options.ValueKind != JsonValueKind.Object
            || !options.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var numeric))
            return numeric;

        if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), out var parsed))
            return parsed;

        return null;
    }

    private static bool ShouldPoll(TemboExecutionResult execution)
    {
        if (execution.Session is not null)
            return !IsTerminalStatus(execution.Session.Status);

        if (execution.AutomationJob is not null)
            return !IsTerminalStatus(execution.AutomationJob.Status);

        return false;
    }

    private static bool IsTerminalStatus(string? status)
        => IsSuccessfulStatus(status) || IsFailureStatus(status);

    private static bool IsSuccessfulStatus(string? status)
        => status is not null
           && (status.Equals("completed", StringComparison.OrdinalIgnoreCase)
               || status.Equals("complete", StringComparison.OrdinalIgnoreCase)
               || status.Equals("done", StringComparison.OrdinalIgnoreCase)
               || status.Equals("finished", StringComparison.OrdinalIgnoreCase)
               || status.Equals("success", StringComparison.OrdinalIgnoreCase)
               || status.Equals("succeeded", StringComparison.OrdinalIgnoreCase)
               || status.Equals("closed", StringComparison.OrdinalIgnoreCase)
               || status.Equals("merged", StringComparison.OrdinalIgnoreCase));

    private static bool IsFailureStatus(string? status)
        => status is not null
           && (status.Equals("failed", StringComparison.OrdinalIgnoreCase)
               || status.Equals("failure", StringComparison.OrdinalIgnoreCase)
               || status.Equals("error", StringComparison.OrdinalIgnoreCase)
               || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
               || status.Equals("canceled", StringComparison.OrdinalIgnoreCase));

    private static bool IsSuccessfulExecution(TemboExecutionResult execution)
    {
        if (execution.Session is not null)
            return IsSuccessfulStatus(execution.Session.Status);

        if (execution.AutomationJob is not null)
            return IsSuccessfulStatus(execution.AutomationJob.Status);

        return false;
    }

    private static void EnsureSuccessfulExecution(TemboExecutionResult execution)
    {
        if (IsSuccessfulExecution(execution))
            return;

        var status = execution.SessionStatus ?? execution.AutomationJob?.Status ?? "unknown";
        if (IsFailureStatus(status))
            throw new InvalidOperationException($"Tembo execution failed with status '{status}'. Body: {execution.FinalRawJson ?? execution.InitialRawJson}");
    }

    private static string? GetStreamingStatusText(TemboExecutionResult execution)
    {
        var status = execution.SessionStatus ?? execution.AutomationJob?.Status;
        if (string.IsNullOrWhiteSpace(status) || IsTerminalStatus(status))
            return null;

        return execution.SessionTitle is { Length: > 0 } title
            ? $"Tembo session '{title}' is {status}."
            : $"Tembo execution is {status}.";
    }

    private static string BuildCompletionText(TemboExecutionResult execution)
    {
        if (execution.Session is { } session)
        {
            var lines = new List<string>
            {
                !string.IsNullOrWhiteSpace(session.Title)
                    ? $"Tembo session '{session.Title}' finished with status '{session.Status}'."
                    : $"Tembo session finished with status '{session.Status}'."
            };

            if (!string.IsNullOrWhiteSpace(session.Description))
                lines.Add(session.Description!);

            if (!string.IsNullOrWhiteSpace(session.HtmlUrl))
                lines.Add($"View session: {session.HtmlUrl}");

            return string.Join("\n\n", lines);
        }

        if (execution.AutomationJob is { } job)
            return $"Tembo automation job '{job.Id}' finished with status '{job.Status}'.";

        return "Tembo execution completed.";
    }

    private static AIToolOutputAvailableEventData CreateToolOutputEventData(string providerId, TemboExecutionResult execution, bool preliminary)
        => new()
        {
            ToolName = execution.ToolName,
            ProviderExecuted = true,
            Preliminary = preliminary,
            Output = CreateToolCallResult(execution),
            ProviderMetadata = CreateToolProviderMetadata(providerId, execution.ToolName, execution.ToolTitle, execution.ToolCallId, "tool_result")
        };

    private static CallToolResult CreateToolCallResult(TemboExecutionResult execution)
    {
        var raw = execution.FinalRawJson ?? execution.InitialRawJson;
        var structuredContent = JsonSerializer.SerializeToElement(new
        {
            content = execution.FinalRaw ?? execution.InitialRaw
        }, JsonOptions);

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = raw }],
            StructuredContent = structuredContent
        };
    }

    private static Dictionary<string, Dictionary<string, object>> CreateToolProviderMetadata(
        string providerId,
        string toolName,
        string toolTitle,
        string toolCallId,
        string type)
        => new()
        {
            [providerId] = new Dictionary<string, object>
            {
                ["type"] = type,
                ["tool_name"] = toolName,
                ["title"] = toolTitle,
                ["tool_use_id"] = toolCallId
            }
        };

    private static AIStreamEvent CreateStreamEvent(
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

    private static JsonElement TryParseJsonElement(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            return JsonSerializer.SerializeToElement(new { }, JsonOptions);

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            return document.RootElement.Clone();
        }
        catch
        {
            return JsonSerializer.SerializeToElement(new { raw = rawJson }, JsonOptions);
        }
    }

    private static string ExtractErrorMessage(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "Unknown Tembo error.";

        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;

            foreach (var property in new[] { "error", "message", "detail" })
            {
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty(property, out var value)
                    && value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString() ?? raw;
                }
            }
        }
        catch
        {
        }

        return raw;
    }

    private static Dictionary<string, object?>? ToMessageMetadata(Dictionary<string, object?>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
            return null;

        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in metadata)
        {
            if (item.Value is not null)
                result[item.Key] = item.Value;
        }

        return result.Count == 0 ? null : result;
    }

    private enum TemboExecutionKind
    {
        SessionCreate,
        AutomationTrigger
    }

    private sealed record TemboExecutionPlan(
        TemboExecutionKind Kind,
        string? KeyOrId,
        string ToolName,
        string ToolTitle,
        string ToolCallId,
        Dictionary<string, object?> Payload);

    private sealed record TemboExecutionResult(
        TemboExecutionKind Kind,
        string ToolName,
        string ToolTitle,
        string ToolCallId,
        JsonElement SubmittedPayload,
        string SubmittedPayloadJson,
        JsonElement InitialRaw,
        string InitialRawJson,
        JsonElement? FinalRaw,
        string? FinalRawJson,
        TemboSession? Session,
        TemboAutomationJob? AutomationJob)
    {
        public string? SessionId => Session?.Id;

        public string? SessionStatus => Session?.Status;

        public string? SessionTitle => Session?.Title;

        public string? HtmlUrl => Session?.HtmlUrl;

        public static TemboExecutionResult ForSession(TemboExecutionPlan plan, string submittedPayloadJson, string raw, TemboSession session)
            => new(
                plan.Kind,
                plan.ToolName,
                plan.ToolTitle,
                plan.ToolCallId,
                JsonSerializer.SerializeToElement(plan.Payload, JsonOptions),
                submittedPayloadJson,
                TryParseJsonElement(raw),
                raw,
                TryParseJsonElement(raw),
                raw,
                session,
                null);

        public static TemboExecutionResult ForAutomation(TemboExecutionPlan plan, string submittedPayloadJson, string raw, TemboAutomationJob job)
            => new(
                plan.Kind,
                plan.ToolName,
                plan.ToolTitle,
                plan.ToolCallId,
                JsonSerializer.SerializeToElement(plan.Payload, JsonOptions),
                submittedPayloadJson,
                TryParseJsonElement(raw),
                raw,
                TryParseJsonElement(raw),
                raw,
                null,
                job);
    }

    private sealed record TemboSession
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("description")]
        public string? Description { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("createdAt")]
        public DateTimeOffset? CreatedAt { get; init; }

        [JsonPropertyName("updatedAt")]
        public DateTimeOffset? UpdatedAt { get; init; }

        [JsonPropertyName("organizationId")]
        public string? OrganizationId { get; init; }

        [JsonPropertyName("htmlUrl")]
        public string? HtmlUrl { get; init; }

        [JsonIgnore]
        public string RawJson { get; set; } = string.Empty;
    }

    private sealed class TemboSessionListResponse
    {
        [JsonPropertyName("issues")]
        public List<TemboSession>? Issues { get; init; }

        [JsonPropertyName("meta")]
        public JsonElement? Meta { get; init; }

        [JsonIgnore]
        public string RawJson { get; set; } = string.Empty;
    }

    private sealed record TemboAutomationJob
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("task")]
        public string? Task { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("priority")]
        public int? Priority { get; init; }

        [JsonPropertyName("createdAt")]
        public DateTimeOffset? CreatedAt { get; init; }

        [JsonPropertyName("updatedAt")]
        public DateTimeOffset? UpdatedAt { get; init; }

        [JsonIgnore]
        public string RawJson { get; set; } = string.Empty;
    }
}
