using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Abstractions.Http;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Tembo;

public partial class TemboProvider
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MinimumPollInterval = TimeSpan.FromSeconds(1);
    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateUnifiedRequest(request);

        ApplyAuthHeader();

        var prompt = BuildPrompt(request);
        var providerOptions = GetProviderOptions(request.Metadata);
        var capture = GetTemboBackendCapture(request, GetIdentifier());
        var execution = await ExecuteTemboAsync(request, prompt, providerOptions, capture, cancellationToken);
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
        var capture = GetTemboBackendCapture(request, providerId);
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

        await using var captureSink = await BeginTemboTaskCaptureAsync(plan, capture, cancellationToken);
        var execution = await SubmitTemboAsync(plan, captureSink, cancellationToken);

        yield return CreateStreamEvent(
            providerId,
            plan.ToolCallId,
            "tool-output-available",
            CreateToolOutputEventData(providerId, execution, preliminary: ShouldPoll(execution)),
            DateTimeOffset.UtcNow,
            null);

        var titleText = BuildTaskTitleText(execution);
        var titleMessageEmitted = false;

        if (!string.IsNullOrWhiteSpace(titleText))
        {
            var titleEventId = $"{eventId}_title";

            yield return CreateStreamEvent(
                providerId,
                titleEventId,
                "text-start",
                new AITextStartEventData(),
                DateTimeOffset.UtcNow,
                null);

            yield return CreateStreamEvent(
                providerId,
                titleEventId,
                "text-delta",
                new AITextDeltaEventData { Delta = titleText },
                DateTimeOffset.UtcNow,
                null);

            yield return CreateStreamEvent(
                providerId,
                titleEventId,
                "text-end",
                new AITextEndEventData(),
                DateTimeOffset.UtcNow,
                null);

            titleMessageEmitted = true;
        }

        await foreach (var updatedExecution in PollTemboStreamAsync(execution, providerOptions, captureSink, cancellationToken))
        {
            execution = updatedExecution;

            yield return CreateStreamEvent(
                providerId,
                plan.ToolCallId,
                "tool-output-available",
                CreateToolOutputEventData(providerId, execution, preliminary: ShouldPoll(execution)),
                DateTimeOffset.UtcNow,
                null);

            // Polling is intentionally silent for end users. The stream emits the
            // generated task title once, then emits a pull request summary when
            // Tembo has created pull requests.
        }

        EnsureSuccessfulExecution(execution);

        var response = CreateUnifiedResponse(request, execution);
        var metadata = response.Metadata;
        var pullRequestText = BuildPullRequestSummaryText(execution);

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

        if (!string.IsNullOrWhiteSpace(pullRequestText))
        {
            var pullRequestEventId = $"{eventId}_pull_requests";

            yield return CreateStreamEvent(
                providerId,
                pullRequestEventId,
                "text-start",
                new AITextStartEventData(),
                DateTimeOffset.UtcNow,
                metadata);

            yield return CreateStreamEvent(
                providerId,
                pullRequestEventId,
                "text-delta",
                new AITextDeltaEventData { Delta = pullRequestText },
                DateTimeOffset.UtcNow,
                metadata);

            yield return CreateStreamEvent(
                providerId,
                pullRequestEventId,
                "text-end",
                new AITextEndEventData(),
                DateTimeOffset.UtcNow,
                metadata);
        }
        else if (!titleMessageEmitted)
        {
            var fallbackText = BuildCompletionText(execution);
            if (!string.IsNullOrWhiteSpace(fallbackText))
            {
                yield return CreateStreamEvent(
                    providerId,
                    eventId,
                    "text-start",
                    new AITextStartEventData(),
                    DateTimeOffset.UtcNow,
                    metadata);

                yield return CreateStreamEvent(
                    providerId,
                    eventId,
                    "text-delta",
                    new AITextDeltaEventData { Delta = fallbackText },
                    DateTimeOffset.UtcNow,
                    metadata);

                yield return CreateStreamEvent(
                    providerId,
                    eventId,
                    "text-end",
                    new AITextEndEventData(),
                    DateTimeOffset.UtcNow,
                    metadata);
            }
        }

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
        ProviderBackendCaptureRequest? capture,
        CancellationToken cancellationToken)
    {
        var plan = CreateExecutionPlan(request, prompt, providerOptions, request.Id ?? Guid.NewGuid().ToString("N"));
        await using var captureSink = await BeginTemboTaskCaptureAsync(plan, capture, cancellationToken);
        var execution = await SubmitTemboAsync(plan, captureSink, cancellationToken);

        await foreach (var updatedExecution in PollTemboStreamAsync(execution, providerOptions, captureSink, cancellationToken))
            execution = updatedExecution;

        EnsureSuccessfulExecution(execution);
        return execution;
    }

    private TemboExecutionPlan CreateExecutionPlan(AIRequest request, string prompt, JsonElement? providerOptions, string eventId)
    {
        var localModel = request.Model;
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

    private async Task<TemboExecutionResult> SubmitTemboAsync(
        TemboExecutionPlan plan,
        TemboTaskCaptureSink? captureSink,
        CancellationToken cancellationToken)
        => plan.Kind switch
        {
            TemboExecutionKind.AutomationTrigger => await TriggerAutomationAsync(plan, captureSink, cancellationToken),
            _ => await CreateSessionAsync(plan, captureSink, cancellationToken)
        };


    private async Task<TemboExecutionResult> CreateSessionAsync(
        TemboExecutionPlan plan,
        TemboTaskCaptureSink? captureSink,
        CancellationToken cancellationToken)
    {
        var submittedPayloadJson = JsonSerializer.Serialize(plan.Payload, JsonOptions);
        var key = _keyResolver.Resolve(GetIdentifier());
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://api.tembo.io/task/create")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new StringContent(submittedPayloadJson, Encoding.UTF8)
        };

        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", key);

        request.Headers.Accept.ParseAdd("*/*");

        // mimic Postman first, because Postman works
        request.Headers.TryAddWithoutValidation("User-Agent", "PostmanRuntime/7.43.0");
        request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");

        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken);

        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        await CaptureTemboRawJsonAsync(captureSink, raw, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"""
        Tembo task/create error
        URL: {request.RequestUri}
        HTTP version: {request.Version}
        Status: {(int)response.StatusCode} {response.ReasonPhrase}
        Content-Type: {request.Content.Headers.ContentType}
        User-Agent: {string.Join(" ", request.Headers.UserAgent)}
        Payload: {submittedPayloadJson}
        Response: {raw}
        """);
        }

        var task = JsonSerializer.Deserialize<TemboSession>(raw, JsonOptions)
            ?? throw new InvalidOperationException("Tembo returned an empty task/create response.");

        return TemboExecutionResult.ForSession(
            plan,
            submittedPayloadJson,
            raw,
            task with { RawJson = raw });
    }

    private async Task<TemboExecutionResult> CreateSessionAsync2(TemboExecutionPlan plan, CancellationToken cancellationToken)
    {
        var submittedPayloadJson = JsonSerializer.Serialize(plan.Payload, JsonOptions);
        var content = new StringContent(submittedPayloadJson, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, "task/create")
        {
            Content = content
        };

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Tembo task/create error: {(int)response.StatusCode} {response.ReasonPhrase}: {ExtractErrorMessage(raw)}");

        var session = JsonSerializer.Deserialize<TemboSession>(raw, JsonOptions)
            ?? throw new InvalidOperationException("Tembo returned an empty task/create response.");

        return TemboExecutionResult.ForSession(plan, submittedPayloadJson, raw, session with { RawJson = raw });
    }

    private async Task<TemboExecutionResult> TriggerAutomationAsync(
        TemboExecutionPlan plan,
        TemboTaskCaptureSink? captureSink,
        CancellationToken cancellationToken)
    {
        var submittedPayloadJson = JsonSerializer.Serialize(plan.Payload, JsonOptions);
        var keyOrId = Uri.EscapeDataString(plan.KeyOrId ?? throw new InvalidOperationException("Tembo automation keyOrId is missing."));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"automation/{keyOrId}/trigger")
        {
            Content = new StringContent(submittedPayloadJson, Encoding.UTF8, "application/json")
        };

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        await CaptureTemboRawJsonAsync(captureSink, raw, cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Tembo automation trigger error: {(int)response.StatusCode} {response.ReasonPhrase}: {ExtractErrorMessage(raw)}");

        var job = JsonSerializer.Deserialize<TemboAutomationJob>(raw, JsonOptions)
            ?? throw new InvalidOperationException("Tembo returned an empty automation trigger response.");

        return TemboExecutionResult.ForAutomation(plan, submittedPayloadJson, raw, job with { RawJson = raw });
    }

    private async IAsyncEnumerable<TemboExecutionResult> PollTemboStreamAsync(
        TemboExecutionResult execution,
        JsonElement? providerOptions,
        TemboTaskCaptureSink? captureSink,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!ShouldPoll(execution))
            yield break;

        var interval = ResolvePollInterval(providerOptions);
        var startedAt = DateTimeOffset.UtcNow;
        TemboSession? trackedSession = execution.Session;
        var pollAttempt = execution.PollAttempt;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await Task.Delay(interval, cancellationToken);

            pollAttempt++;
            var list = await ListSessionsAsync(cancellationToken);
            var nextSession = ResolveTrackedSession(execution, trackedSession, list.Issues);

            if (nextSession is null)
            {
                yield return execution with
                {
                    PollAttempt = pollAttempt,
                    LastPollRawJson = list.RawJson
                };

                continue;
            }

            await CaptureTemboRawJsonAsync(captureSink, nextSession.RawJson, cancellationToken);

            trackedSession = nextSession;
            execution = execution with
            {
                Session = nextSession,
                FinalRawJson = nextSession.RawJson,
                FinalRaw = TryParseJsonElement(nextSession.RawJson),
                PollAttempt = pollAttempt,
                LastPollRawJson = list.RawJson
            };

            yield return execution;

            if (!ShouldPoll(execution))
                yield break;
        }

    }

    private async Task<TemboSessionListResponse> ListSessionsAsync(
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://api.tembo.io/task/list?limit=100&page=1")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        var key = _keyResolver.Resolve(GetIdentifier());

        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", key);

        request.Headers.Accept.ParseAdd("*/*");
        request.Headers.TryAddWithoutValidation("User-Agent", "PostmanRuntime/7.43.0");
        request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");

        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken);

        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"""
        Tembo task/list error
        URL: {request.RequestUri}
        HTTP version: {request.Version}
        Status: {(int)response.StatusCode} {response.ReasonPhrase}
        User-Agent: {string.Join(" ", request.Headers.UserAgent)}
        Response: {ExtractErrorMessage(raw)}
        Raw response: {raw}
        """);
        }

        var list = DeserializeSessionList(raw)
            ?? throw new InvalidOperationException("Tembo returned an empty task/list response.");

        list.RawJson = raw;

        foreach (var issue in list.Issues ?? [])
            issue.RawJson = JsonSerializer.Serialize(issue, JsonOptions);

        return list;
    }

    private static ValueTask<TemboTaskCaptureSink?> BeginTemboTaskCaptureAsync(
        TemboExecutionPlan plan,
        ProviderBackendCaptureRequest? capture,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            plan.Kind == TemboExecutionKind.AutomationTrigger
                ? $"https://api.tembo.io/automation/{Uri.EscapeDataString(plan.KeyOrId ?? "unknown")}/trigger"
                : "https://api.tembo.io/task/create");
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request
        };

        var sink = ProviderBackendCapture.BeginJsonArrayCapture("tembo-task", response, capture);
        return ValueTask.FromResult(sink is null ? null : new TemboTaskCaptureSink(sink));
    }

    private static async ValueTask CaptureTemboRawJsonAsync(
        TemboTaskCaptureSink? captureSink,
        string raw,
        CancellationToken cancellationToken)
    {
        if (captureSink is null)
            return;

        await captureSink.WriteRawJsonAsync(raw, cancellationToken);
    }

    private static ProviderBackendCaptureRequest? GetTemboBackendCapture(AIRequest request, string providerId)
    {
        if (request.Metadata is null)
            return null;

        return request.Metadata.GetProviderOption<ProviderBackendCaptureRequest>(providerId, "capture")
            ?? request.Metadata.GetProviderOption<ProviderBackendCaptureRequest>(providerId, "backend_capture");
    }

    private static TemboSessionListResponse? DeserializeSessionList(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        var issues = new List<TemboSession>();
        JsonElement? meta = null;

        if (root.ValueKind == JsonValueKind.Array)
        {
            issues.AddRange(DeserializeSessionArray(root));
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("meta", out var metaElement))
                meta = metaElement.Clone();

            foreach (var propertyName in new[] { "issues", "data", "tasks", "items", "results", "sessions" })
            {
                if (root.TryGetProperty(propertyName, out var arrayElement)
                    && arrayElement.ValueKind == JsonValueKind.Array)
                {
                    issues.AddRange(DeserializeSessionArray(arrayElement));
                    break;
                }
            }

            if (issues.Count == 0 && root.TryGetProperty("id", out _))
            {
                var session = root.Deserialize<TemboSession>(JsonOptions);
                if (session is not null)
                    issues.Add(session);
            }
        }

        return new TemboSessionListResponse
        {
            Issues = issues,
            Meta = meta
        };
    }

    private static IEnumerable<TemboSession> DeserializeSessionArray(JsonElement arrayElement)
    {
        foreach (var item in arrayElement.EnumerateArray())
        {
            var session = item.Deserialize<TemboSession>(JsonOptions);
            if (session is not null)
                yield return session;
        }
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

            match = issues.FirstOrDefault(issue => EndsWithSessionId(issue.HtmlUrl, existingId));
            if (match is not null)
                return match;
        }

        var existingHash = trackedSession?.Hash ?? execution.Session?.Hash;
        if (!string.IsNullOrWhiteSpace(existingHash))
        {
            var hashMatch = issues.FirstOrDefault(issue => string.Equals(issue.Hash, existingHash, StringComparison.OrdinalIgnoreCase));
            if (hashMatch is not null)
                return hashMatch;
        }

        var submittedPrompt = TryReadSubmittedPrompt(execution.SubmittedPayload);
        if (!string.IsNullOrWhiteSpace(submittedPrompt))
        {
            var promptMatch = issues
                .Where(issue => string.Equals(issue.Prompt, submittedPrompt, StringComparison.Ordinal)
                                || string.Equals(issue.Description, submittedPrompt, StringComparison.Ordinal))
                .OrderByDescending(issue => issue.CreatedAt)
                .FirstOrDefault();

            if (promptMatch is not null)
                return promptMatch;
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

    private static bool EndsWithSessionId(string? htmlUrl, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(htmlUrl))
            return false;

        return htmlUrl.TrimEnd('/').EndsWith(sessionId, StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryReadSubmittedPrompt(JsonElement submittedPayload)
    {
        if (submittedPayload.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var propertyName in new[] { "prompt", "description" })
        {
            if (submittedPayload.TryGetProperty(propertyName, out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    private AIResponse CreateUnifiedResponse(AIRequest request, TemboExecutionResult execution)
    {
        var metadata = CreateResponseMetadata(request, execution);
        var model = request.Model?.ToModelId(GetIdentifier()) ?? GetIdentifier();
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
            }
        };

        outputItems.AddRange(CreateResponseMessageItems(execution));

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = model,
            Status = IsSuccessfulExecution(execution) ? "completed" : "failed",
            Output = new AIOutput
            {
                Items = outputItems
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
            ["tembo.session_effective_status"] = execution.SessionEffectiveStatus,
            ["tembo.session_title"] = execution.SessionTitle,
            ["tembo.session_url"] = execution.HtmlUrl,
            ["tembo.pull_request_count"] = GetPullRequests(execution.Session).Count,
            ["tembo.pull_requests"] = JsonSerializer.SerializeToElement(
                GetPullRequests(execution.Session).Select(pr => new
                {
                    pr.Id,
                    pr.Url,
                    pr.Title,
                    pr.Status,
                    pr.MergedAt,
                    pr.IsDraft
                }),
                JsonOptions),
            ["tembo.automation_job_id"] = execution.AutomationJob?.Id,
            ["tembo.automation_job_status"] = execution.AutomationJob?.Status,
            ["tembo.automation_job_task"] = execution.AutomationJob?.Task,
            ["tembo.submitted_payload"] = execution.SubmittedPayload,
            ["tembo.initial.raw"] = execution.InitialRaw,
            ["tembo.final.raw"] = execution.FinalRaw,
            ["tembo.poll_attempt"] = execution.PollAttempt,
            ["tembo.last_poll.raw"] = TryParseJsonElement(execution.LastPollRawJson)
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
            payload["agent"] = localModel;

        if (!payload.ContainsKey("queueRightAway"))
            payload["queueRightAway"] = true;

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

        var lastUserMessage =
            string.Join(
                "\n",
                request.Input?.Items?
                    .Where(x => x.Role == "user")
                    .LastOrDefault()?
                    .Content?
                    .OfType<AITextContentPart>()
                    .Select(x => x.Text)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                ?? []
            );

        lastUserMessage = string.IsNullOrWhiteSpace(lastUserMessage)
            ? request.Input?.Text
            : lastUserMessage;

        return lastUserMessage!;
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
        {
            if (HasPullRequests(execution.Session))
                return false;

            return !IsTerminalStatus(execution.Session.EffectiveStatus);
        }

        if (execution.AutomationJob is not null)
            return string.IsNullOrWhiteSpace(execution.AutomationJob.Status)
                || !IsTerminalStatus(execution.AutomationJob.Status);

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
               || status.Equals("pull_request_created", StringComparison.OrdinalIgnoreCase)
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
            return HasSuccessfulPullRequestResult(execution.Session) || IsSuccessfulStatus(execution.Session.EffectiveStatus);

        if (execution.AutomationJob is not null)
            return IsSuccessfulStatus(execution.AutomationJob.Status);

        return false;
    }

    private static void EnsureSuccessfulExecution(TemboExecutionResult execution)
    {
        if (IsSuccessfulExecution(execution))
            return;

        var status = execution.SessionEffectiveStatus ?? execution.AutomationJob?.Status ?? "unknown";
        if (IsFailureStatus(status))
            throw new InvalidOperationException($"Tembo execution failed with status '{status}'. Body: {execution.FinalRawJson ?? execution.InitialRawJson}");
    }

    private static string BuildCompletionText(TemboExecutionResult execution)
    {
        var messages = CreateResponseMessageTexts(execution).ToList();
        if (messages.Count > 0)
            return string.Join("\n\n", messages);

        if (execution.Session is { } session)
        {
            var lines = new List<string>
            {
                !string.IsNullOrWhiteSpace(session.Title)
                    ? $"Tembo session '{session.Title}' finished with status '{session.EffectiveStatus ?? "unknown"}'."
                    : $"Tembo session finished with status '{session.EffectiveStatus ?? "unknown"}'."
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

    private static IEnumerable<AIOutputItem> CreateResponseMessageItems(TemboExecutionResult execution)
        => CreateResponseMessageTexts(execution)
            .Select(text => new AIOutputItem
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
            });

    private static IEnumerable<string> CreateResponseMessageTexts(TemboExecutionResult execution)
    {
        var titleText = BuildTaskTitleText(execution);
        if (!string.IsNullOrWhiteSpace(titleText))
            yield return titleText;

        var pullRequestText = BuildPullRequestSummaryText(execution);
        if (!string.IsNullOrWhiteSpace(pullRequestText))
            yield return pullRequestText;
    }

    private static string? BuildTaskTitleText(TemboExecutionResult execution)
    {
        if (execution.Session is { } session)
            return !string.IsNullOrWhiteSpace(session.Title)
                ? session.Title
                : !string.IsNullOrWhiteSpace(session.Id)
                    ? "Tembo task"
                    : null;

        return null;
    }

    private static string? BuildPullRequestSummaryText(TemboExecutionResult execution)
    {
        var pullRequests = GetPullRequests(execution.Session);
        if (pullRequests.Count == 0)
            return null;

        var heading = pullRequests.Count == 1
            ? "Pull request created:"
            : "Pull requests created:";

        var lines = pullRequests.Select(pr =>
        {
            var title = !string.IsNullOrWhiteSpace(pr.Title)
                ? pr.Title!
                : !string.IsNullOrWhiteSpace(pr.Id)
                    ? pr.Id!
                    : "Pull request";

            return !string.IsNullOrWhiteSpace(pr.Url)
                ? $"- [{title}]({pr.Url})"
                : $"- {title}";
        });

        return heading + "\n\n" + string.Join("\n", lines);
    }

    private static List<TemboPullRequest> GetPullRequests(TemboSession? session)
        => session?.Artifacts?
            .SelectMany(artifact => artifact.PullRequest ?? [])
            .DistinctBy(pr => !string.IsNullOrWhiteSpace(pr.Url) ? pr.Url : pr.Id)
            .ToList()
           ?? [];

    private static bool HasPullRequests(TemboSession session)
        => GetPullRequests(session).Count > 0;

    private static bool HasSuccessfulPullRequestResult(TemboSession session)
    {
        var pullRequests = GetPullRequests(session);
        return pullRequests.Count > 0 && pullRequests.Any(pr => !pr.IsFailure);
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
            content = execution.FinalRaw ?? execution.InitialRaw,
            pollAttempt = execution.PollAttempt,
            lastPoll = TryParseJsonElement(execution.LastPollRawJson)
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

    private static string NormalizeCaptureJson(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            return "{}";

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            return document.RootElement.GetRawText();
        }
        catch
        {
            return JsonSerializer.Serialize(new { raw = rawJson }, JsonOptions);
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
        int PollAttempt,
        string? LastPollRawJson,
        TemboSession? Session,
        TemboAutomationJob? AutomationJob)
    {
        public string? SessionId => Session?.Id;

        public string? SessionStatus => Session?.Status;

        public string? SessionEffectiveStatus => Session?.EffectiveStatus;

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
                0,
                null,
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
                0,
                null,
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

        [JsonPropertyName("prompt")]
        public string? Prompt { get; init; }

        [JsonPropertyName("hash")]
        public string? Hash { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("state")]
        public string? State { get; init; }

        [JsonPropertyName("lastQueuedAt")]
        public DateTimeOffset? LastQueuedAt { get; init; }

        [JsonPropertyName("lastQueuedBy")]
        public string? LastQueuedBy { get; init; }

        [JsonPropertyName("lastSeenAt")]
        public DateTimeOffset? LastSeenAt { get; init; }

        [JsonPropertyName("createdAt")]
        public DateTimeOffset? CreatedAt { get; init; }

        [JsonPropertyName("updatedAt")]
        public DateTimeOffset? UpdatedAt { get; init; }

        [JsonPropertyName("organizationId")]
        public string? OrganizationId { get; init; }

        [JsonPropertyName("htmlUrl")]
        public string? HtmlUrl { get; init; }

        [JsonPropertyName("artifacts")]
        public List<TemboArtifact>? Artifacts { get; init; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; init; }

        [JsonIgnore]
        public string? EffectiveStatus => ResolveEffectiveStatus();

        [JsonIgnore]
        public string RawJson { get; set; } = string.Empty;

        private string? ResolveEffectiveStatus()
        {
            if (!string.IsNullOrWhiteSpace(Status))
                return Status;

            if (!string.IsNullOrWhiteSpace(State))
                return State;

            var pullRequests = Artifacts?
                .SelectMany(artifact => artifact.PullRequest ?? [])
                .ToList();

            if (pullRequests is { Count: > 0 })
            {
                if (pullRequests.All(pr => pr.IsFailure))
                    return "failed";

                return "pull_request_created";
            }

            if (Artifacts is { Count: > 0 })
                return "running";

            if (LastQueuedAt is not null || !string.IsNullOrWhiteSpace(LastQueuedBy))
                return "running";

            if (Id is not null)
                return "created";

            return null;
        }
    }

    private sealed record TemboArtifact
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("type")]
        public string? Type { get; init; }

        [JsonPropertyName("jobId")]
        public string? JobId { get; init; }

        [JsonPropertyName("pullRequest")]
        public List<TemboPullRequest>? PullRequest { get; init; }
    }

    private sealed record TemboPullRequest
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("url")]
        public string? Url { get; init; }

        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("mergedAt")]
        public DateTimeOffset? MergedAt { get; init; }

        [JsonPropertyName("isDraft")]
        public bool? IsDraft { get; init; }

        [JsonIgnore]
        public bool IsMerged => MergedAt is not null || string.Equals(Status, "merged", StringComparison.OrdinalIgnoreCase);

        [JsonIgnore]
        public bool IsFailure => Status is not null
                                 && (Status.Equals("closed", StringComparison.OrdinalIgnoreCase)
                                     || Status.Equals("failed", StringComparison.OrdinalIgnoreCase)
                                     || Status.Equals("error", StringComparison.OrdinalIgnoreCase));

        [JsonIgnore]
        public bool IsOpen => Status is null
                              || Status.Equals("open", StringComparison.OrdinalIgnoreCase)
                              || Status.Equals("draft", StringComparison.OrdinalIgnoreCase)
                              || Status.Equals("pending", StringComparison.OrdinalIgnoreCase)
                              || Status.Equals("running", StringComparison.OrdinalIgnoreCase);
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

    private sealed class TemboTaskCaptureSink(ProviderBackendCaptureJsonArraySink inner) : IAsyncDisposable
    {
        public string FilePath => inner.FilePath;

        public async ValueTask WriteRawJsonAsync(string rawJson, CancellationToken cancellationToken)
            => await inner.WriteRawJsonEntryAsync(NormalizeCaptureJson(rawJson), cancellationToken);

        public ValueTask DisposeAsync()
            => inner.DisposeAsync();
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
