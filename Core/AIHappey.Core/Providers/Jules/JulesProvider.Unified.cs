using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Jules;

public partial class JulesProvider
{
    private const string JulesBaseModelId = "jules/jules";
    private const string CreateSessionToolName = "create_session";
    private const string ApprovePlanToolName = "approve_plan";
    private const string BashOutputToolName = "bash_output";
    private const string ChangeSetToolName = "change_set";
    private const string MediaArtifactToolName = "media_artifact";
    private const int DefaultPollMaxAttempts = 120;
    private const int DefaultPollSeconds = 2;
    private const int MaxActivitiesPageSize = 100;

    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ApplyAuthHeader();

        var resolution = await ResolveSessionAsync(request, cancellationToken);
        await ExecuteSessionActionAsync(request, resolution, cancellationToken);

        var snapshot = await WaitForStableSnapshotAsync(request, resolution.Id, cancellationToken);
        if (ShouldApprovePlan(request) && IsAwaitingPlanApproval(snapshot.Session))
        {
            await ApprovePlanAsync(resolution.Id, cancellationToken);
            snapshot = await WaitForStableSnapshotAsync(request, resolution.Id, cancellationToken);
        }

        return CreateUnifiedResponse(request, resolution, snapshot);
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ApplyAuthHeader();

        var timestamp = DateTimeOffset.UtcNow;
        var resolution = await ResolveSessionAsync(request, cancellationToken);

        if (resolution.Created)
        {
            foreach (var evt in CreateSessionToolEvents(resolution, timestamp))
                yield return evt;
        }

        await ExecuteSessionActionAsync(request, resolution, cancellationToken);

        var seenActivityIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var emittedPlanIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var emittedArtifactIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var emittedSourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var emittedApprovalPlanIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var textStarted = false;
        var lastText = string.Empty;

        var maxAttempts = GetProviderOption(request, "poll_max_attempts", DefaultPollMaxAttempts);
        var delay = TimeSpan.FromSeconds(GetProviderOption(request, "poll_seconds", DefaultPollSeconds));

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var snapshot = await GetSnapshotAsync(resolution.Id, cancellationToken);
            var orderedActivities = OrderActivities(snapshot.Activities);

            foreach (var activity in orderedActivities)
            {
                var activityId = ExtractString(activity, "id");
                if (string.IsNullOrWhiteSpace(activityId) || !seenActivityIds.Add(activityId))
                    continue;

                var activityTimestamp = ParseTimestamp(activity, "createTime") ?? DateTimeOffset.UtcNow;

                foreach (var evt in CreatePlanEvents(activity, activityId, activityTimestamp, emittedPlanIds))
                    yield return evt;

                foreach (var evt in CreateArtifactEvents(activity, activityId, activityTimestamp, emittedArtifactIds))
                    yield return evt;
            }

            var currentText = BuildAssistantText(orderedActivities);
            var delta = ComputeTextDelta(lastText, currentText);
            if (!string.IsNullOrEmpty(delta))
            {
                if (!textStarted)
                {
                    textStarted = true;
                    yield return CreateStreamEvent(
                        "text-start",
                        resolution.Id,
                        new AITextStartEventData
                        {
                            ProviderMetadata = CreateLooseProviderMetadata(snapshot.Session)
                        },
                        timestamp,
                        CreateResponseMetadata(resolution, snapshot));
                }

                yield return CreateStreamEvent(
                    "text-delta",
                    resolution.Id,
                    new AITextDeltaEventData
                    {
                        Delta = delta,
                        ProviderMetadata = CreateLooseProviderMetadata(snapshot.Session)
                    },
                    DateTimeOffset.UtcNow,
                    CreateResponseMetadata(resolution, snapshot));

                lastText = currentText;
            }

            foreach (var evt in CreateSourceEvents(snapshot, emittedSourceIds, DateTimeOffset.UtcNow))
                yield return evt;

            if (IsAwaitingPlanApproval(snapshot.Session)
                && TryGetLatestPlan(snapshot.Activities, out var planId, out _)
                && emittedApprovalPlanIds.Add(planId))
            {
                yield return CreateApprovePlanToolInputEvent(resolution, planId, DateTimeOffset.UtcNow);
            }

            if (IsStableSessionState(snapshot.Session))
            {
                if (textStarted)
                {
                    yield return CreateStreamEvent(
                        "text-end",
                        resolution.Id,
                        new AITextEndEventData
                        {
                            ProviderMetadata = CreateLooseProviderMetadata(snapshot.Session)
                        },
                        DateTimeOffset.UtcNow,
                        CreateResponseMetadata(resolution, snapshot));
                }

                if (TryGetFailedReason(snapshot.Session, snapshot.Activities) is { Length: > 0 } failureReason)
                {
                    yield return CreateStreamEvent(
                        "error",
                        resolution.Id,
                        new AIErrorEventData { ErrorText = failureReason },
                        DateTimeOffset.UtcNow,
                        CreateResponseMetadata(resolution, snapshot));
                }

                yield return CreateFinishEvent(request, resolution, snapshot, DateTimeOffset.UtcNow);
                yield break;
            }

            await Task.Delay(delay, cancellationToken);
        }

        throw new TimeoutException($"Timed out waiting for Jules session '{resolution.Id}' to reach a stable state.");
    }

    private async Task<JulesSessionResolution> ResolveSessionAsync(AIRequest request, CancellationToken cancellationToken)
    {
        if (TryFindSessionId(request, out var existingSessionId))
            return new JulesSessionResolution(existingSessionId, false, null, ResolveSourceName(request), ResolveStartingBranch(request));

        var source = ResolveSourceName(request)
            ?? throw new InvalidOperationException("Jules requires a source shortcut model or metadata.jules.source when creating a session.");
        var startingBranch = ResolveStartingBranch(request)
            ?? throw new InvalidOperationException("Jules requires metadata.jules.startingBranch (or metadata.jules.starting_branch) when creating a session.");

        var payload = BuildCreateSessionPayload(request, source, startingBranch);
        using var response = await _client.PostAsJsonAsync("sessions", payload, Json, cancellationToken);
        var session = await ReadJsonElementAsync(response, "Jules create session", cancellationToken);
        var id = ExtractString(session, "id")
                 ?? throw new InvalidOperationException("Jules create session response did not include an id.");

        return new JulesSessionResolution(id, true, session.Clone(), source, startingBranch);
    }

    private async Task ExecuteSessionActionAsync(
        AIRequest request,
        JulesSessionResolution resolution,
        CancellationToken cancellationToken)
    {
        if (resolution.Created)
            return;

        var prompt = ExtractFollowUpPrompt(request);
        if (string.IsNullOrWhiteSpace(prompt))
            return;

        using var response = await _client.PostAsJsonAsync(
            $"sessions/{Uri.EscapeDataString(resolution.Id)}:sendMessage",
            new Dictionary<string, object?>
            {
                ["prompt"] = prompt
            },
            Json,
            cancellationToken);

        await EnsureSuccessAsync(response, "Jules send message", cancellationToken);
    }

    private Dictionary<string, object?> BuildCreateSessionPayload(AIRequest request, string source, string startingBranch)
    {
        var prompt = BuildCreatePrompt(request)
                     ?? throw new InvalidOperationException("Jules requires an initial user prompt.");

        var payload = new Dictionary<string, object?>
        {
            ["prompt"] = prompt,
            ["sourceContext"] = new Dictionary<string, object?>
            {
                ["source"] = source,
                ["githubRepoContext"] = new Dictionary<string, object?>
                {
                    ["startingBranch"] = startingBranch
                }
            }
        };

        if (request.Metadata?.GetProviderOption<string>(GetIdentifier(), "title") is { Length: > 0 } title)
            payload["title"] = title;

        var automationMode = request.Metadata?.GetProviderOption<string>(GetIdentifier(), "automationMode");
        var automationModeSnake = request.Metadata?.GetProviderOption<string>(GetIdentifier(), "automation_mode");
        if (!string.IsNullOrWhiteSpace(automationMode) || !string.IsNullOrWhiteSpace(automationModeSnake))
            payload["automationMode"] = automationMode ?? automationModeSnake;

        var requirePlanApproval = request.Metadata?.GetProviderOption<bool?>(GetIdentifier(), "requirePlanApproval")
                                  ?? request.Metadata?.GetProviderOption<bool?>(GetIdentifier(), "require_plan_approval");
        if (requirePlanApproval.HasValue)
            payload["requirePlanApproval"] = requirePlanApproval.Value;

        var providerMetadata = GetJulesProviderMetadata(request.Metadata);
        if (providerMetadata is not null)
        {
            foreach (var property in providerMetadata.Value.EnumerateObject())
            {
                if (IsLocalProviderOption(property.Name))
                    continue;

                payload[property.Name] = property.Value.Clone();
            }
        }

        return payload;
    }

    private async Task ApprovePlanAsync(string sessionId, CancellationToken cancellationToken)
    {
        using var response = await _client.PostAsJsonAsync(
            $"sessions/{Uri.EscapeDataString(sessionId)}:approvePlan",
            new Dictionary<string, object?>(),
            Json,
            cancellationToken);

        await EnsureSuccessAsync(response, "Jules approve plan", cancellationToken);
    }

    private async Task<JulesSnapshot> WaitForStableSnapshotAsync(
        AIRequest request,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var maxAttempts = GetProviderOption(request, "poll_max_attempts", DefaultPollMaxAttempts);
        var delay = TimeSpan.FromSeconds(GetProviderOption(request, "poll_seconds", DefaultPollSeconds));

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var snapshot = await GetSnapshotAsync(sessionId, cancellationToken);
            if (IsStableSessionState(snapshot.Session))
                return snapshot;

            await Task.Delay(delay, cancellationToken);
        }

        throw new TimeoutException($"Timed out waiting for Jules session '{sessionId}' to reach a stable state.");
    }

    private async Task<JulesSnapshot> GetSnapshotAsync(string sessionId, CancellationToken cancellationToken)
    {
        var session = await GetSessionAsync(sessionId, cancellationToken);
        var activities = await ListActivitiesAsync(sessionId, cancellationToken);
        return new JulesSnapshot(session, activities);
    }

    private async Task<JsonElement> GetSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync($"sessions/{Uri.EscapeDataString(sessionId)}", cancellationToken);
        return await ReadJsonElementAsync(response, "Jules get session", cancellationToken);
    }

    private async Task<List<JsonElement>> ListActivitiesAsync(string sessionId, CancellationToken cancellationToken)
    {
        var activities = new List<JsonElement>();
        string? pageToken = null;

        do
        {
            var path = string.IsNullOrWhiteSpace(pageToken)
                ? $"sessions/{Uri.EscapeDataString(sessionId)}/activities?pageSize={MaxActivitiesPageSize}"
                : $"sessions/{Uri.EscapeDataString(sessionId)}/activities?pageSize={MaxActivitiesPageSize}&pageToken={Uri.EscapeDataString(pageToken)}";

            using var response = await _client.GetAsync(path, cancellationToken);
            var json = await ReadJsonElementAsync(response, "Jules list activities", cancellationToken);

            if (json.TryGetProperty("activities", out var activitiesElement)
                && activitiesElement.ValueKind == JsonValueKind.Array)
            {
                activities.AddRange(activitiesElement.EnumerateArray().Select(activity => activity.Clone()));
            }

            pageToken = ExtractString(json, "nextPageToken");
        }
        while (!string.IsNullOrWhiteSpace(pageToken));

        return activities;
    }

    private AIResponse CreateUnifiedResponse(AIRequest request, JulesSessionResolution resolution, JulesSnapshot snapshot)
    {
        var content = new List<AIContentPart>();

        if (resolution.Created && resolution.RawSession is JsonElement rawSession)
            content.Add(CreateSessionToolPart(resolution.Id, rawSession));

        foreach (var activity in OrderActivities(snapshot.Activities))
            AddActivityContentParts(activity, content);

        if (IsAwaitingPlanApproval(snapshot.Session)
            && TryGetLatestPlan(snapshot.Activities, out var latestPlanId, out var latestPlan))
        {
            content.Add(CreateApprovePlanToolPart(resolution.Id, latestPlanId, latestPlan));
        }

        if (content.Count == 0)
        {
            content.Add(new AITextContentPart
            {
                Type = "text",
                Text = BuildFallbackStatusText(resolution.Id, snapshot.Session),
                Metadata = new Dictionary<string, object?>
                {
                    ["jules.raw"] = snapshot.Session.Clone()
                }
            });
        }

        var responseMetadata = CreateResponseMetadata(resolution, snapshot);
        var items = new List<AIOutputItem>
        {
            new()
            {
                Type = "message",
                Role = "assistant",
                Content = content,
                Metadata = responseMetadata
            }
        };

        items.AddRange(CreateSourceOutputItems(snapshot));

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = NormalizeModelId(request.Model),
            Status = MapUnifiedStatus(snapshot.Session),
            Output = new AIOutput
            {
                Items = items,
                Metadata = responseMetadata
            },
            Usage = null,
            Metadata = responseMetadata
        };
    }

    private void AddActivityContentParts(JsonElement activity, List<AIContentPart> content)
    {
        if (activity.TryGetProperty("planGenerated", out var planGenerated)
            && planGenerated.ValueKind == JsonValueKind.Object
            && planGenerated.TryGetProperty("plan", out var plan)
            && plan.ValueKind == JsonValueKind.Object)
        {
            var planText = FormatPlan(plan);
            if (!string.IsNullOrWhiteSpace(planText))
            {
                content.Add(new AIReasoningContentPart
                {
                    Type = "reasoning",
                    Text = planText,
                    Metadata = new Dictionary<string, object?>
                    {
                        ["jules.activityId"] = ExtractString(activity, "id"),
                        ["jules.planId"] = ExtractString(plan, "id"),
                        ["jules.raw"] = activity.Clone()
                    }
                });
            }
        }

        var agentMessage = activity.TryGetProperty("agentMessaged", out var agentMessaged)
            ? ExtractString(agentMessaged, "agentMessage")
            : null;
        if (!string.IsNullOrWhiteSpace(agentMessage))
        {
            content.Add(new AITextContentPart
            {
                Type = "text",
                Text = agentMessage,
                Metadata = new Dictionary<string, object?>
                {
                    ["jules.activityId"] = ExtractString(activity, "id"),
                    ["jules.raw"] = activity.Clone()
                }
            });
        }

        if (activity.TryGetProperty("progressUpdated", out var progressUpdated)
            && progressUpdated.ValueKind == JsonValueKind.Object)
        {
            var progressText = CombineProgressText(progressUpdated);
            if (!string.IsNullOrWhiteSpace(progressText))
            {
                content.Add(new AITextContentPart
                {
                    Type = "text",
                    Text = progressText,
                    Metadata = new Dictionary<string, object?>
                    {
                        ["jules.activityId"] = ExtractString(activity, "id"),
                        ["jules.raw"] = activity.Clone()
                    }
                });
            }
        }

        if (activity.TryGetProperty("sessionFailed", out var sessionFailed)
            && sessionFailed.ValueKind == JsonValueKind.Object)
        {
            var reason = ExtractString(sessionFailed, "reason");
            if (!string.IsNullOrWhiteSpace(reason))
            {
                content.Add(new AITextContentPart
                {
                    Type = "text",
                    Text = $"Jules session failed: {reason}",
                    Metadata = new Dictionary<string, object?>
                    {
                        ["jules.activityId"] = ExtractString(activity, "id"),
                        ["jules.raw"] = activity.Clone()
                    }
                });
            }
        }

        if (!activity.TryGetProperty("artifacts", out var artifactsElement)
            || artifactsElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var artifactIndex = 0;
        foreach (var artifact in artifactsElement.EnumerateArray())
        {
            if (artifact.ValueKind != JsonValueKind.Object)
            {
                artifactIndex++;
                continue;
            }

            var artifactPart = CreateArtifactContentPart(activity, artifact, artifactIndex);
            if (artifactPart is not null)
                content.Add(artifactPart);

            artifactIndex++;
        }
    }

    private AIContentPart? CreateArtifactContentPart(JsonElement activity, JsonElement artifact, int artifactIndex)
    {
        if (artifact.TryGetProperty("bashOutput", out var bashOutput)
            && bashOutput.ValueKind == JsonValueKind.Object)
        {
            var activityId = ExtractString(activity, "id") ?? $"activity-{artifactIndex}";
            return new AIToolCallContentPart
            {
                Type = "tool-call",
                ToolCallId = BuildArtifactToolCallId(activityId, artifactIndex, BashOutputToolName),
                ToolName = BashOutputToolName,
                Title = "Jules bash output",
                Input = JsonSerializer.SerializeToElement(new
                {
                    command = ExtractString(bashOutput, "command")
                }, Json),
                Output = CreateArtifactToolResult(new
                {
                    command = ExtractString(bashOutput, "command"),
                    output = ExtractString(bashOutput, "output"),
                    exitCode = ExtractInt32(bashOutput, "exitCode")
                }),
                ProviderExecuted = true,
                State = "output-available",
                Metadata = CreateArtifactToolMetadata(activity, BashOutputToolName, artifactIndex)
            };
        }

        if (artifact.TryGetProperty("changeSet", out var changeSet)
            && changeSet.ValueKind == JsonValueKind.Object)
        {
            var gitPatch = changeSet.TryGetProperty("gitPatch", out var patch) && patch.ValueKind == JsonValueKind.Object
                ? patch
                : default;
            var activityId = ExtractString(activity, "id") ?? $"activity-{artifactIndex}";
            return new AIToolCallContentPart
            {
                Type = "tool-call",
                ToolCallId = BuildArtifactToolCallId(activityId, artifactIndex, ChangeSetToolName),
                ToolName = ChangeSetToolName,
                Title = "Jules change set",
                Input = JsonSerializer.SerializeToElement(new
                {
                    source = ExtractString(changeSet, "source")
                }, Json),
                Output = CreateArtifactToolResult(new
                {
                    source = ExtractString(changeSet, "source"),
                    baseCommitId = ExtractString(gitPatch, "baseCommitId"),
                    unidiffPatch = ExtractString(gitPatch, "unidiffPatch"),
                    suggestedCommitMessage = ExtractString(gitPatch, "suggestedCommitMessage"),
                    changeSet = changeSet.Clone()
                }),
                ProviderExecuted = true,
                State = "output-available",
                Metadata = CreateArtifactToolMetadata(activity, ChangeSetToolName, artifactIndex)
            };
        }

        if (artifact.TryGetProperty("media", out var media)
            && media.ValueKind == JsonValueKind.Object)
        {
            var mimeType = ExtractString(media, "mimeType") ?? "application/octet-stream";
            var data = ExtractString(media, "data");
            if (!string.IsNullOrWhiteSpace(data))
            {
                return new AIFileContentPart
                {
                    Type = "file",
                    MediaType = mimeType,
                    Filename = BuildMediaFilename(ExtractString(activity, "id") ?? $"artifact-{artifactIndex}", mimeType),
                    Data = $"data:{mimeType};base64,{data}",
                    Metadata = new Dictionary<string, object?>
                    {
                        ["jules.activityId"] = ExtractString(activity, "id"),
                        ["jules.artifactIndex"] = artifactIndex,
                        ["jules.raw"] = media.Clone()
                    }
                };
            }

            return new AIToolCallContentPart
            {
                Type = "tool-call",
                ToolCallId = BuildArtifactToolCallId(ExtractString(activity, "id") ?? $"activity-{artifactIndex}", artifactIndex, MediaArtifactToolName),
                ToolName = MediaArtifactToolName,
                Title = "Jules media artifact",
                Input = JsonSerializer.SerializeToElement(new { mimeType }, Json),
                Output = CreateArtifactToolResult(new
                {
                    mimeType,
                    media = media.Clone()
                }),
                ProviderExecuted = true,
                State = "output-available",
                Metadata = CreateArtifactToolMetadata(activity, MediaArtifactToolName, artifactIndex)
            };
        }

        return null;
    }

    private IEnumerable<AIOutputItem> CreateSourceOutputItems(JulesSnapshot snapshot)
    {
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (ExtractString(snapshot.Session, "url") is { Length: > 0 } sessionUrl && emitted.Add(sessionUrl))
        {
            yield return new AIOutputItem
            {
                Type = "source-url",
                Role = "assistant",
                Metadata = new Dictionary<string, object?>
                {
                    ["source.url"] = sessionUrl,
                    ["source.title"] = "Jules session"
                }
            };
        }

        if (snapshot.Session.TryGetProperty("outputs", out var outputs)
            && outputs.ValueKind == JsonValueKind.Array)
        {
            foreach (var output in outputs.EnumerateArray())
            {
                if (!output.TryGetProperty("pullRequest", out var pullRequest)
                    || pullRequest.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var url = ExtractString(pullRequest, "url");
                if (string.IsNullOrWhiteSpace(url) || !emitted.Add(url))
                    continue;

                yield return new AIOutputItem
                {
                    Type = "source-url",
                    Role = "assistant",
                    Metadata = new Dictionary<string, object?>
                    {
                        ["source.url"] = url,
                        ["source.title"] = ExtractString(pullRequest, "title") ?? "Jules pull request",
                        ["source.description"] = ExtractString(pullRequest, "description")
                    }
                };
            }
        }
    }

    private IEnumerable<AIStreamEvent> CreatePlanEvents(
        JsonElement activity,
        string activityId,
        DateTimeOffset timestamp,
        HashSet<string> emittedPlanIds)
    {
        if (!activity.TryGetProperty("planGenerated", out var planGenerated)
            || planGenerated.ValueKind != JsonValueKind.Object
            || !planGenerated.TryGetProperty("plan", out var plan)
            || plan.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        var planId = ExtractString(plan, "id") ?? activityId;
        if (!emittedPlanIds.Add(planId))
            yield break;

        var planText = FormatPlan(plan);
        if (string.IsNullOrWhiteSpace(planText))
            yield break;

        var providerMetadata = CreateProviderScopedMetadata(new Dictionary<string, object?>
        {
            ["activityId"] = activityId,
            ["planId"] = planId,
            ["raw"] = activity.Clone()
        });

        yield return CreateStreamEvent(
            "reasoning-start",
            planId,
            new AIReasoningStartEventData
            {
                ProviderMetadata = providerMetadata
            },
            timestamp,
            null);

        yield return CreateStreamEvent(
            "reasoning-delta",
            planId,
            new AIReasoningDeltaEventData
            {
                Delta = planText,
                ProviderMetadata = providerMetadata
            },
            timestamp,
            null);

        yield return CreateStreamEvent(
            "reasoning-end",
            planId,
            new AIReasoningEndEventData
            {
                ProviderMetadata = providerMetadata
            },
            timestamp,
            null);
    }

    private IEnumerable<AIStreamEvent> CreateArtifactEvents(
        JsonElement activity,
        string activityId,
        DateTimeOffset timestamp,
        HashSet<string> emittedArtifactIds)
    {
        if (!activity.TryGetProperty("artifacts", out var artifactsElement)
            || artifactsElement.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        var artifactIndex = 0;
        foreach (var artifact in artifactsElement.EnumerateArray())
        {
            var emittedAny = false;

            if (artifact.TryGetProperty("bashOutput", out var bashOutput)
                && bashOutput.ValueKind == JsonValueKind.Object)
            {
                var artifactId = BuildArtifactToolCallId(activityId, artifactIndex, BashOutputToolName);
                if (emittedArtifactIds.Add(artifactId))
                {
                    emittedAny = true;
                    yield return CreateStreamEvent(
                        "tool-output-available",
                        artifactId,
                        new AIToolOutputAvailableEventData
                        {
                            ToolName = BashOutputToolName,
                            ProviderExecuted = true,
                            Output = CreateArtifactToolResult(new
                            {
                                command = ExtractString(bashOutput, "command"),
                                output = ExtractString(bashOutput, "output"),
                                exitCode = ExtractInt32(bashOutput, "exitCode")
                            }),
                            ProviderMetadata = CreateProviderScopedMetadata(CreateArtifactToolMetadata(activity, BashOutputToolName, artifactIndex))
                        },
                        timestamp,
                        null);
                }
            }

            if (artifact.TryGetProperty("changeSet", out var changeSet)
                && changeSet.ValueKind == JsonValueKind.Object)
            {
                var artifactId = BuildArtifactToolCallId(activityId, artifactIndex, ChangeSetToolName);
                if (emittedArtifactIds.Add(artifactId))
                {
                    emittedAny = true;
                    var gitPatch = changeSet.TryGetProperty("gitPatch", out var patch) && patch.ValueKind == JsonValueKind.Object
                        ? patch
                        : default;
                    yield return CreateStreamEvent(
                        "tool-output-available",
                        artifactId,
                        new AIToolOutputAvailableEventData
                        {
                            ToolName = ChangeSetToolName,
                            ProviderExecuted = true,
                            Output = CreateArtifactToolResult(new
                            {
                                source = ExtractString(changeSet, "source"),
                                baseCommitId = ExtractString(gitPatch, "baseCommitId"),
                                unidiffPatch = ExtractString(gitPatch, "unidiffPatch"),
                                suggestedCommitMessage = ExtractString(gitPatch, "suggestedCommitMessage"),
                                changeSet = changeSet.Clone()
                            }),
                            ProviderMetadata = CreateProviderScopedMetadata(CreateArtifactToolMetadata(activity, ChangeSetToolName, artifactIndex))
                        },
                        timestamp,
                        null);
                }
            }

            if (artifact.TryGetProperty("media", out var media)
                && media.ValueKind == JsonValueKind.Object)
            {
                var artifactId = BuildArtifactToolCallId(activityId, artifactIndex, MediaArtifactToolName);
                if (emittedArtifactIds.Add(artifactId))
                {
                    emittedAny = true;
                    yield return CreateStreamEvent(
                        "tool-output-available",
                        artifactId,
                        new AIToolOutputAvailableEventData
                        {
                            ToolName = MediaArtifactToolName,
                            ProviderExecuted = true,
                            Output = CreateArtifactToolResult(new
                            {
                                mimeType = ExtractString(media, "mimeType"),
                                data = ExtractString(media, "data"),
                                media = media.Clone()
                            }),
                            ProviderMetadata = CreateProviderScopedMetadata(CreateArtifactToolMetadata(activity, MediaArtifactToolName, artifactIndex))
                        },
                        timestamp,
                        null);
                }
            }

            if (!emittedAny)
                artifactIndex++;
            else
                artifactIndex++;
        }
    }

    private IEnumerable<AIStreamEvent> CreateSourceEvents(
        JulesSnapshot snapshot,
        HashSet<string> emittedSourceIds,
        DateTimeOffset timestamp)
    {
        if (ExtractString(snapshot.Session, "url") is { Length: > 0 } sessionUrl)
        {
            var sourceId = $"jules-session-{Uri.EscapeDataString(sessionUrl)}";
            if (emittedSourceIds.Add(sourceId))
            {
                yield return CreateStreamEvent(
                    "source-url",
                    sourceId,
                    new AISourceUrlEventData
                    {
                        SourceId = sourceId,
                        Url = sessionUrl,
                        Title = "Jules session",
                        ProviderMetadata = CreateProviderScopedMetadata(new Dictionary<string, object?>
                        {
                            ["url"] = sessionUrl
                        })
                    },
                    timestamp,
                    null);
            }
        }

        if (!snapshot.Session.TryGetProperty("outputs", out var outputs)
            || outputs.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var output in outputs.EnumerateArray())
        {
            if (!output.TryGetProperty("pullRequest", out var pullRequest)
                || pullRequest.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var url = ExtractString(pullRequest, "url");
            if (string.IsNullOrWhiteSpace(url))
                continue;

            var sourceId = $"jules-pr-{Uri.EscapeDataString(url)}";
            if (!emittedSourceIds.Add(sourceId))
                continue;

            yield return CreateStreamEvent(
                "source-url",
                sourceId,
                new AISourceUrlEventData
                {
                    SourceId = sourceId,
                    Url = url,
                    Title = ExtractString(pullRequest, "title") ?? "Jules pull request",
                    ProviderMetadata = CreateProviderScopedMetadata(new Dictionary<string, object?>
                    {
                        ["url"] = url,
                        ["description"] = ExtractString(pullRequest, "description")
                    })
                },
                timestamp,
                null);
        }
    }

    private AIToolCallContentPart CreateSessionToolPart(string sessionId, JsonElement rawSession)
        => new()
        {
            Type = "tool-call",
            ToolCallId = BuildSessionToolCallId(sessionId),
            ToolName = CreateSessionToolName,
            Title = "Create Jules session",
            Input = JsonSerializer.SerializeToElement(new { }, Json),
            Output = CreateSessionToolResult(rawSession),
            ProviderExecuted = true,
            State = "output-available",
            Metadata = new Dictionary<string, object?>
            {
                ["type"] = CreateSessionToolName,
                ["sessionId"] = sessionId,
                ["session_id"] = sessionId,
                ["tool_name"] = CreateSessionToolName
            }
        };

    private IEnumerable<AIStreamEvent> CreateSessionToolEvents(JulesSessionResolution resolution, DateTimeOffset timestamp)
    {
        if (resolution.RawSession is not JsonElement rawSession)
            yield break;

        var toolCallId = BuildSessionToolCallId(resolution.Id);
        var providerMetadata = CreateProviderScopedMetadata(new Dictionary<string, object?>
        {
            ["type"] = CreateSessionToolName,
            ["sessionId"] = resolution.Id,
            ["session_id"] = resolution.Id,
            ["tool_name"] = CreateSessionToolName
        });

        yield return CreateStreamEvent(
            "tool-input-available",
            toolCallId,
            new AIToolInputAvailableEventData
            {
                ToolName = CreateSessionToolName,
                Title = "Create Jules session",
                Input = JsonSerializer.SerializeToElement(new { }, Json),
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
                ToolName = CreateSessionToolName,
                ProviderExecuted = true,
                Output = CreateSessionToolResult(rawSession),
                ProviderMetadata = providerMetadata
            },
            timestamp,
            null);
    }

    private AIToolCallContentPart CreateApprovePlanToolPart(string sessionId, string planId, JsonElement plan)
        => new()
        {
            Type = "tool-call",
            ToolCallId = BuildApprovePlanToolCallId(sessionId, planId),
            ToolName = ApprovePlanToolName,
            Title = "Approve Jules plan",
            Input = JsonSerializer.SerializeToElement(new
            {
                sessionId,
                session_id = sessionId,
                planId,
                plan_id = planId
            }, Json),
            ProviderExecuted = true,
            State = "input-available",
            Approval = new AIToolCallApproval
            {
                Approved = false,
                Id = planId
            },
            Metadata = new Dictionary<string, object?>
            {
                ["type"] = ApprovePlanToolName,
                ["sessionId"] = sessionId,
                ["session_id"] = sessionId,
                ["planId"] = planId,
                ["plan_id"] = planId,
                ["tool_name"] = ApprovePlanToolName,
                ["jules.plan"] = plan.Clone()
            }
        };

    private AIStreamEvent CreateApprovePlanToolInputEvent(JulesSessionResolution resolution, string planId, DateTimeOffset timestamp)
        => CreateStreamEvent(
            "tool-input-available",
            BuildApprovePlanToolCallId(resolution.Id, planId),
            new AIToolInputAvailableEventData
            {
                ToolName = ApprovePlanToolName,
                Title = "Approve Jules plan",
                Input = JsonSerializer.SerializeToElement(new
                {
                    sessionId = resolution.Id,
                    session_id = resolution.Id,
                    planId,
                    plan_id = planId
                }, Json),
                ProviderExecuted = true,
                ProviderMetadata = CreateProviderScopedMetadata(new Dictionary<string, object?>
                {
                    ["type"] = ApprovePlanToolName,
                    ["sessionId"] = resolution.Id,
                    ["session_id"] = resolution.Id,
                    ["planId"] = planId,
                    ["plan_id"] = planId,
                    ["tool_name"] = ApprovePlanToolName
                })
            },
            timestamp,
            null);

    private static CallToolResult CreateSessionToolResult(JsonElement rawSession)
        => new()
        {
            Content = [],
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                sessionId = ExtractString(rawSession, "id"),
                session_id = ExtractString(rawSession, "id"),
                source = ExtractNestedString(rawSession, "sourceContext", "source"),
                startingBranch = ExtractNestedString(rawSession, "sourceContext", "githubRepoContext", "startingBranch"),
                url = ExtractString(rawSession, "url"),
                session = rawSession.Clone()
            }, Json)
        };

    private static CallToolResult CreateArtifactToolResult(object payload)
        => new()
        {
            Content = [],
            StructuredContent = JsonSerializer.SerializeToElement(payload, Json)
        };

    private static bool TryFindSessionId(AIRequest request, out string sessionId)
    {
        sessionId = request.Metadata.GetProviderOption<string>("jules", "sessionId")
                    ?? request.Metadata.GetProviderOption<string>("jules", "session_id")
                    ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(sessionId))
            return true;

        foreach (var item in request.Input?.Items ?? [])
        {
            foreach (var toolPart in item.Content?.OfType<AIToolCallContentPart>() ?? [])
            {
                if (toolPart.ProviderExecuted != true)
                    continue;

                if (TryExtractSessionId(toolPart.Output, out sessionId))
                    return true;

                if (TryExtractSessionId(toolPart.Metadata, out sessionId))
                    return true;

                if (IsCreateSessionToolPart(toolPart) && TryExtractSessionId(toolPart.Input, out sessionId))
                    return true;
            }
        }

        sessionId = string.Empty;
        return false;
    }

    private static bool IsCreateSessionToolPart(AIToolCallContentPart toolPart)
        => string.Equals(toolPart.ToolName, CreateSessionToolName, StringComparison.OrdinalIgnoreCase)
           || string.Equals(toolPart.ToolName, $"tool-{CreateSessionToolName}", StringComparison.OrdinalIgnoreCase)
           || string.Equals(toolPart.Title, CreateSessionToolName, StringComparison.OrdinalIgnoreCase)
           || string.Equals(toolPart.Title, "Create Jules session", StringComparison.OrdinalIgnoreCase)
           || string.Equals(toolPart.Type, $"tool-{CreateSessionToolName}", StringComparison.OrdinalIgnoreCase)
           || string.Equals(toolPart.Metadata?.GetValueOrDefault("messages.block.type")?.ToString(), CreateSessionToolName, StringComparison.OrdinalIgnoreCase);

    private static bool TryExtractSessionId(object? value, out string sessionId)
    {
        sessionId = string.Empty;
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
            if (TryExtractSessionId(output, out sessionId))
                return true;
        }

        if (element.TryGetProperty("sessionId", out var id) && id.ValueKind == JsonValueKind.String)
            sessionId = id.GetString() ?? string.Empty;
        else if (element.TryGetProperty("session_id", out var snakeId) && snakeId.ValueKind == JsonValueKind.String)
            sessionId = snakeId.GetString() ?? string.Empty;
        else if (element.TryGetProperty("session", out var session) && session.ValueKind == JsonValueKind.Object)
            sessionId = ExtractString(session, "id") ?? string.Empty;
        else if (element.TryGetProperty("jules", out var jules) && jules.ValueKind == JsonValueKind.Object)
            sessionId = ExtractString(jules, "sessionId") ?? ExtractString(jules, "session_id") ?? string.Empty;

        return !string.IsNullOrWhiteSpace(sessionId);
    }

    private static string? ResolveSourceName(AIRequest request)
    {
        var explicitSource = request.Metadata?.GetProviderOption<string>("jules", "source");
        if (!string.IsNullOrWhiteSpace(explicitSource))
            return explicitSource;

        var localModelId = ExtractLocalModelId(request.Model);
        return localModelId.StartsWith("sources/", StringComparison.OrdinalIgnoreCase)
            ? localModelId
            : null;
    }

    private static string? ResolveStartingBranch(AIRequest request)
        => request.Metadata?.GetProviderOption<string>("jules", "startingBranch")
           ?? request.Metadata?.GetProviderOption<string>("jules", "starting_branch")
           ?? request.Metadata?.GetProviderOption<string>("jules", "branch");

    private static string? BuildCreatePrompt(AIRequest request)
    {
        var latestUserText = ExtractLatestUserText(request);
        if (!string.IsNullOrWhiteSpace(request.Instructions) && !string.IsNullOrWhiteSpace(latestUserText))
            return $"{request.Instructions}\n\n{latestUserText}";

        return latestUserText
               ?? request.Input?.Text
               ?? request.Instructions;
    }

    private static string? ExtractFollowUpPrompt(AIRequest request)
        => ExtractLatestUserText(request)
           ?? request.Input?.Text
           ?? request.Instructions;

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

    private static List<JsonElement> OrderActivities(IEnumerable<JsonElement> activities)
        => activities
            .OrderBy(activity => ParseTimestamp(activity, "createTime") ?? DateTimeOffset.MinValue)
            .ThenBy(activity => ExtractString(activity, "id"))
            .ToList();

    private static string BuildAssistantText(IEnumerable<JsonElement> activities)
    {
        var parts = new List<string>();

        foreach (var activity in activities)
        {
            var agentMessage = activity.TryGetProperty("agentMessaged", out var agentMessaged)
                ? ExtractString(agentMessaged, "agentMessage")
                : null;
            if (!string.IsNullOrWhiteSpace(agentMessage))
                parts.Add(agentMessage.Trim());

            if (activity.TryGetProperty("progressUpdated", out var progressUpdated)
                && progressUpdated.ValueKind == JsonValueKind.Object)
            {
                var progressText = CombineProgressText(progressUpdated);
                if (!string.IsNullOrWhiteSpace(progressText))
                    parts.Add(progressText.Trim());
            }

            if (activity.TryGetProperty("sessionFailed", out var sessionFailed)
                && sessionFailed.ValueKind == JsonValueKind.Object)
            {
                var reason = ExtractString(sessionFailed, "reason");
                if (!string.IsNullOrWhiteSpace(reason))
                    parts.Add($"Jules session failed: {reason}".Trim());
            }
        }

        return string.Join("\n\n", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string CombineProgressText(JsonElement progressUpdated)
    {
        var title = ExtractString(progressUpdated, "title");
        var description = ExtractString(progressUpdated, "description");

        if (string.IsNullOrWhiteSpace(title))
            return description ?? string.Empty;

        if (string.IsNullOrWhiteSpace(description)
            || string.Equals(title, description, StringComparison.OrdinalIgnoreCase))
        {
            return title;
        }

        return $"{title}\n{description}";
    }

    private static string FormatPlan(JsonElement plan)
    {
        var lines = new List<string>();
        if (ExtractString(plan, "id") is { Length: > 0 } planId)
            lines.Add($"Plan {planId}");
        else
            lines.Add("Plan");

        if (plan.TryGetProperty("steps", out var steps)
            && steps.ValueKind == JsonValueKind.Array)
        {
            foreach (var step in steps.EnumerateArray().Where(step => step.ValueKind == JsonValueKind.Object))
            {
                var title = ExtractString(step, "title");
                var description = ExtractString(step, "description");
                var index = ExtractInt32(step, "index") ?? lines.Count - 1;

                if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(description))
                    lines.Add($"{index + 1}. {title} — {description}");
                else if (!string.IsNullOrWhiteSpace(title))
                    lines.Add($"{index + 1}. {title}");
                else if (!string.IsNullOrWhiteSpace(description))
                    lines.Add($"{index + 1}. {description}");
            }
        }

        return string.Join("\n", lines.Where(line => !string.IsNullOrWhiteSpace(line)));
    }

    private static bool TryGetLatestPlan(IEnumerable<JsonElement> activities, out string planId, out JsonElement plan)
    {
        foreach (var activity in OrderActivities(activities).AsEnumerable().Reverse())
        {
            if (!activity.TryGetProperty("planGenerated", out var planGenerated)
                || planGenerated.ValueKind != JsonValueKind.Object
                || !planGenerated.TryGetProperty("plan", out var planElement)
                || planElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            planId = ExtractString(planElement, "id") ?? ExtractString(activity, "id") ?? Guid.NewGuid().ToString("N");
            plan = planElement.Clone();
            return true;
        }

        planId = string.Empty;
        plan = default;
        return false;
    }

    private static string BuildFallbackStatusText(string sessionId, JsonElement session)
    {
        var state = ExtractString(session, "state") ?? "unknown";
        return $"Jules session {sessionId} is {state}.";
    }

    private static bool IsStableSessionState(JsonElement session)
    {
        var state = ExtractString(session, "state");
        return string.Equals(state, "COMPLETED", StringComparison.OrdinalIgnoreCase)
               || string.Equals(state, "FAILED", StringComparison.OrdinalIgnoreCase)
               || string.Equals(state, "AWAITING_PLAN_APPROVAL", StringComparison.OrdinalIgnoreCase)
               || string.Equals(state, "AWAITING_USER_FEEDBACK", StringComparison.OrdinalIgnoreCase)
               || string.Equals(state, "PAUSED", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAwaitingPlanApproval(JsonElement session)
        => string.Equals(ExtractString(session, "state"), "AWAITING_PLAN_APPROVAL", StringComparison.OrdinalIgnoreCase);

    private static string MapUnifiedStatus(JsonElement session)
    {
        var state = ExtractString(session, "state");
        if (string.Equals(state, "COMPLETED", StringComparison.OrdinalIgnoreCase))
            return "completed";

        if (string.Equals(state, "FAILED", StringComparison.OrdinalIgnoreCase))
            return "failed";

        if (string.Equals(state, "AWAITING_PLAN_APPROVAL", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, "AWAITING_USER_FEEDBACK", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, "PAUSED", StringComparison.OrdinalIgnoreCase))
        {
            return "requires_action";
        }

        return "in_progress";
    }

    private static string MapFinishReason(JsonElement session)
    {
        var state = ExtractString(session, "state");
        if (string.Equals(state, "FAILED", StringComparison.OrdinalIgnoreCase))
            return "error";

        if (string.Equals(state, "AWAITING_PLAN_APPROVAL", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, "AWAITING_USER_FEEDBACK", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, "PAUSED", StringComparison.OrdinalIgnoreCase))
        {
            return "tool-calls";
        }

        return "stop";
    }

    private static string? TryGetFailedReason(JsonElement session, IEnumerable<JsonElement> activities)
    {
        if (string.Equals(ExtractString(session, "state"), "FAILED", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var activity in OrderActivities(activities).AsEnumerable().Reverse())
            {
                if (activity.TryGetProperty("sessionFailed", out var sessionFailed)
                    && sessionFailed.ValueKind == JsonValueKind.Object)
                {
                    return ExtractString(sessionFailed, "reason") ?? "Jules session failed.";
                }
            }

            return "Jules session failed.";
        }

        return null;
    }

    private AIFinishEventData CreateFinishData(
        AIRequest request,
        JulesSessionResolution resolution,
        JulesSnapshot snapshot,
        DateTimeOffset timestamp)
        => new()
        {
            FinishReason = MapFinishReason(snapshot.Session),
            Model = NormalizeModelId(request.Model),
            CompletedAt = timestamp.ToUnixTimeSeconds(),
            MessageMetadata = AIFinishMessageMetadata.Create(
                NormalizeModelId(request.Model),
                timestamp,
                usage: null,
                additionalProperties: new Dictionary<string, object?>
                {
                    [GetIdentifier()] = new
                    {
                        sessionId = resolution.Id,
                        session_id = resolution.Id,
                        state = ExtractString(snapshot.Session, "state"),
                        raw = snapshot.Session.Clone()
                    }
                })
        };

    private AIStreamEvent CreateFinishEvent(
        AIRequest request,
        JulesSessionResolution resolution,
        JulesSnapshot snapshot,
        DateTimeOffset timestamp)
        => CreateStreamEvent(
            "finish",
            resolution.Id,
            CreateFinishData(request, resolution, snapshot, timestamp),
            timestamp,
            CreateResponseMetadata(resolution, snapshot));

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

    private Dictionary<string, object?> CreateResponseMetadata(JulesSessionResolution resolution, JulesSnapshot snapshot)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["jules"] = new Dictionary<string, object?>
            {
                ["sessionId"] = resolution.Id,
                ["session_id"] = resolution.Id,
                ["created"] = resolution.Created,
                ["source"] = resolution.Source ?? ExtractNestedString(snapshot.Session, "sourceContext", "source"),
                ["startingBranch"] = resolution.StartingBranch ?? ExtractNestedString(snapshot.Session, "sourceContext", "githubRepoContext", "startingBranch"),
                ["state"] = ExtractString(snapshot.Session, "state"),
                ["url"] = ExtractString(snapshot.Session, "url"),
                ["raw"] = snapshot.Session.Clone()
            }
        };

    private static Dictionary<string, Dictionary<string, object>> CreateProviderScopedMetadata(Dictionary<string, object?> metadata)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["jules"] = metadata
                .Where(kvp => kvp.Value is not null)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value!, StringComparer.OrdinalIgnoreCase)
        };

    private static Dictionary<string, object> CreateLooseProviderMetadata(JsonElement raw)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["raw"] = raw.Clone()
        };

    private static Dictionary<string, object?> CreateArtifactToolMetadata(JsonElement activity, string toolName, int artifactIndex)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["type"] = toolName,
            ["tool_name"] = toolName,
            ["activityId"] = ExtractString(activity, "id"),
            ["artifactIndex"] = artifactIndex,
            ["raw"] = activity.Clone()
        };

    private static JsonElement? GetJulesProviderMetadata(Dictionary<string, object?>? metadata)
    {
        if (metadata is null || !metadata.TryGetValue("jules", out var value) || value is null)
            return null;

        var element = value is JsonElement json ? json : JsonSerializer.SerializeToElement(value, Json);
        return element.ValueKind == JsonValueKind.Object ? element.Clone() : null;
    }

    private static bool IsLocalProviderOption(string name)
        => string.Equals(name, "sessionId", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "session_id", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "source", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "startingBranch", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "starting_branch", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "branch", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "title", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "automationMode", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "automation_mode", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "requirePlanApproval", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "require_plan_approval", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "approvePlan", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "approve_plan", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "poll_max_attempts", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "poll_seconds", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldApprovePlan(AIRequest request)
        => request.Metadata?.GetProviderOption<bool?>("jules", "approvePlan") == true
           || request.Metadata?.GetProviderOption<bool?>("jules", "approve_plan") == true;

    private static int GetProviderOption(AIRequest request, string key, int fallback)
    {
        var value = request.Metadata?.GetProviderOption<int?>("jules", key);
        return value is > 0 ? value.Value : fallback;
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException($"{operation} API error ({(int)response.StatusCode}): {raw}");
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

    private static string NormalizeModelId(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return JulesBaseModelId;

        return model.StartsWith($"{nameof(Jules).ToLowerInvariant()}/", StringComparison.OrdinalIgnoreCase)
            ? model
            : model.ToModelId(nameof(Jules).ToLowerInvariant());
    }

    private static string ExtractLocalModelId(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return string.Empty;

        var parts = model.Split('/');
        return parts.Length > 1 ? string.Join("/", parts.Skip(1)) : model;
    }

    private static string ComputeTextDelta(string previousText, string currentText)
    {
        if (string.IsNullOrEmpty(currentText) || string.Equals(previousText, currentText, StringComparison.Ordinal))
            return string.Empty;

        return currentText.StartsWith(previousText, StringComparison.Ordinal)
            ? currentText[previousText.Length..]
            : $"\n{currentText}";
    }

    private static DateTimeOffset? ParseTimestamp(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.String
           && DateTimeOffset.TryParse(value.GetString(), out var timestamp)
            ? timestamp
            : null;

    private static int? ExtractInt32(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var result)
            ? result
            : null;

    private static string? ExtractString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ExtractNestedString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return null;
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static string BuildSessionToolCallId(string sessionId)
        => $"jules-create-session-{sessionId}";

    private static string BuildApprovePlanToolCallId(string sessionId, string planId)
        => $"jules-approve-plan-{sessionId}-{planId}";

    private static string BuildArtifactToolCallId(string activityId, int artifactIndex, string toolName)
        => $"jules-{toolName}-{activityId}-{artifactIndex}";

    private static string BuildMediaFilename(string activityId, string mimeType)
    {
        var extension = mimeType.ToLowerInvariant() switch
        {
            "image/png" => "png",
            "image/jpeg" => "jpg",
            "image/webp" => "webp",
            "image/gif" => "gif",
            "image/svg+xml" => "svg",
            _ => "bin"
        };

        return $"jules-{activityId}.{extension}";
    }

    private sealed record JulesSessionResolution(
        string Id,
        bool Created,
        JsonElement? RawSession,
        string? Source,
        string? StartingBranch);

    private sealed record JulesSnapshot(
        JsonElement Session,
        List<JsonElement> Activities);
}
