using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.VibeKit;

public partial class VibeKitProvider
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PollTimeout = TimeSpan.FromMinutes(10);

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ValidateUnifiedRequest(request);

        var execution = await ExecuteTaskAsync(request, cancellationToken);
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
        var toolCallId = $"vibekit_task_{eventId}";
        var timestamp = DateTimeOffset.UtcNow;
        var prompt = BuildPrompt(request);
        var submitPayload = CreateSubmitPayload(request, prompt);
        var submitPayloadJson = JsonSerializer.Serialize(submitPayload, JsonSerializerOptions.Web);
        var submitInput = JsonSerializer.SerializeToElement(submitPayload, JsonSerializerOptions.Web);

        yield return CreateStreamEvent(
            providerId,
            toolCallId,
            "tool-input-start",
            new AIToolInputStartEventData
            {
                ToolName = "vibekit_task",
                Title = "VibeKit task",
                ProviderExecuted = true
            },
            timestamp,
            null);

        yield return CreateStreamEvent(
            providerId,
            toolCallId,
            "tool-input-delta",
            new AIToolInputDeltaEventData
            {
                InputTextDelta = submitPayloadJson
            },
            timestamp,
            null);

        yield return CreateStreamEvent(
            providerId,
            toolCallId,
            "tool-input-available",
            new AIToolInputAvailableEventData
            {
                ToolName = "vibekit_task",
                Title = "VibeKit task",
                ProviderExecuted = true,
                Input = submitInput
            },
            timestamp,
            null);

        var createResult = await SubmitTaskAsync(submitPayload, cancellationToken);
        var latestStatus = createResult.InitialStatus;

        yield return CreateStreamEvent(
            providerId,
            toolCallId,
            "tool-output-available",
            CreateToolOutputEventData(providerId, toolCallId, latestStatus, preliminary: !IsCompletedStatus(latestStatus.Status)),
            timestamp,
            null);

        if (!IsTerminalStatus(latestStatus.Status))
        {
            var startedAt = DateTime.UtcNow;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (DateTime.UtcNow - startedAt >= PollTimeout)
                    throw new TimeoutException($"VibeKit polling exceeded timeout ({PollTimeout}).");

                await Task.Delay(PollInterval, cancellationToken);
                latestStatus = await GetTaskStatusAsync(createResult.TaskId, cancellationToken);

                yield return CreateStreamEvent(
                    providerId,
                    toolCallId,
                    "tool-output-available",
                    CreateToolOutputEventData(
                        providerId,
                        toolCallId,
                        latestStatus,
                        preliminary: !IsCompletedStatus(latestStatus.Status)),
                    DateTimeOffset.UtcNow,
                    null);

                if (IsTerminalStatus(latestStatus.Status))
                    break;
            }
        }

        if (!IsCompletedStatus(latestStatus.Status))
            throw new InvalidOperationException($"VibeKit task did not complete successfully. Status='{latestStatus.Status}'. Body: {latestStatus.RawJson}");

        yield return CreateStreamEvent(
            providerId,
            toolCallId,
            "tool-output-available",
            CreateToolOutputEventData(providerId, toolCallId, latestStatus, preliminary: false),
            DateTimeOffset.UtcNow,
            null);

        var text = FormatAssistantJsonBlock(latestStatus.RawJson);
        var response = CreateUnifiedResponse(request, new VibeKitExecutionResult(createResult.TaskId, prompt, submitPayloadJson, latestStatus));
        var responseMetadata = response.Metadata;

        yield return CreateStreamEvent(
            providerId,
            eventId,
            "text-start",
            new AITextStartEventData
            {
                ProviderMetadata = ToProviderMetadata(responseMetadata)
            },
            timestamp,
            responseMetadata);

        foreach (var delta in ChunkText(text))
        {
            cancellationToken.ThrowIfCancellationRequested();

            yield return CreateStreamEvent(
                providerId,
                eventId,
                "text-delta",
                new AITextDeltaEventData
                {
                    Delta = delta,
                    ProviderMetadata = ToProviderMetadata(responseMetadata)
                },
                timestamp,
                responseMetadata);
        }

        yield return CreateStreamEvent(
            providerId,
            eventId,
            "text-end",
            new AITextEndEventData
            {
                ProviderMetadata = ToProviderMetadata(responseMetadata)
            },
            timestamp,
            responseMetadata);

        yield return new AIStreamEvent
        {
            ProviderId = providerId,
            Event = new AIEventEnvelope
            {
                Type = "finish",
                Id = eventId,
                Timestamp = timestamp,
                Output = response.Output,
                Data = new AIFinishEventData
                {
                    FinishReason = "stop",
                    Model = response.Model,
                    CompletedAt = timestamp.ToUnixTimeSeconds(),
                    MessageMetadata = ToMessageMetadata(responseMetadata)
                }
            },
            Metadata = responseMetadata
        };
    }

    private async Task<VibeKitExecutionResult> ExecuteTaskAsync(AIRequest request, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        var prompt = BuildPrompt(request);
        var submitPayload = CreateSubmitPayload(request, prompt);
        var submitPayloadJson = JsonSerializer.Serialize(submitPayload, JsonSerializerOptions.Web);
        var createResult = await SubmitTaskAsync(submitPayload, cancellationToken);

        var finalStatus = IsTerminalStatus(createResult.InitialStatus.Status)
            ? createResult.InitialStatus
            : await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
                poll: ct => GetTaskStatusAsync(createResult.TaskId, ct),
                isTerminal: status => IsTerminalStatus(status.Status),
                interval: PollInterval,
                timeout: PollTimeout,
                maxAttempts: null,
                cancellationToken: cancellationToken);

        if (!IsCompletedStatus(finalStatus.Status))
            throw new InvalidOperationException($"VibeKit task did not complete successfully. Status='{finalStatus.Status}'. Body: {finalStatus.RawJson}");

        return new VibeKitExecutionResult(createResult.TaskId, prompt, submitPayloadJson, finalStatus);
    }

    private async Task<VibeKitTaskCreateResult> SubmitTaskAsync(VibeKitTaskSubmitRequest payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/task")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonSerializerOptions.Web),
                Encoding.UTF8,
                "application/json")
        };

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"VibeKit submit error: {(int)response.StatusCode} {response.ReasonPhrase}: {ExtractErrorMessage(raw)}");

        var payloadResponse = JsonSerializer.Deserialize<VibeKitTaskStatusResponse>(raw, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException("VibeKit returned an empty task creation response.");

        var taskId = payloadResponse.TaskId;
        if (string.IsNullOrWhiteSpace(taskId))
            throw new InvalidOperationException($"VibeKit submit response did not include a taskId. Body: {raw}");

        return new VibeKitTaskCreateResult(taskId, payloadResponse with { RawJson = raw });
    }

    private async Task<VibeKitTaskStatusResponse> GetTaskStatusAsync(string taskId, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync($"api/v1/task/{Uri.EscapeDataString(taskId)}", cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"VibeKit status error: {(int)response.StatusCode} {response.ReasonPhrase}: {ExtractErrorMessage(raw)}");

        var payload = JsonSerializer.Deserialize<VibeKitTaskStatusResponse>(raw, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException("VibeKit returned an empty task status response.");

        return payload with { RawJson = raw };
    }

    private AIResponse CreateUnifiedResponse(AIRequest request, VibeKitExecutionResult execution)
    {
        var status = execution.FinalStatus;
        var text = FormatAssistantJsonBlock(status.RawJson);
        var metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["vibekit.task_id"] = execution.TaskId,
            ["vibekit.status"] = status.Status,
            ["vibekit.deploy_url"] = status.DeployUrl,
            ["vibekit.repo_url"] = status.RepoUrl,
            ["vibekit.project_id"] = status.ProjectId,
            ["vibekit.prompt"] = execution.Prompt,
            ["vibekit.submit.raw"] = TryParseJsonElement(execution.SubmitPayloadJson),
            ["vibekit.response.raw"] = TryParseJsonElement(status.RawJson)
        };

        if (!string.IsNullOrWhiteSpace(request.Model))
            metadata["vibekit.requested_model"] = request.Model;

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = request.Model,
            Status = "completed",
            Output = new AIOutput
            {
                Items =
                [
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

    private static VibeKitTaskSubmitRequest CreateSubmitPayload(AIRequest request, string prompt)
    {
        var metadata = request.Metadata;

        return new VibeKitTaskSubmitRequest
        {
            Prompt = prompt,
            Skills = TryReadStringList(metadata, "skills") ?? TryReadStringList(metadata, "vibekit.skills"),
            ProjectId = TryReadString(metadata, "projectId") ?? TryReadString(metadata, "vibekit.projectId"),
            CallbackUrl = TryReadString(metadata, "callbackUrl") ?? TryReadString(metadata, "vibekit.callbackUrl")
        };
    }

    private static void ValidateUnifiedRequest(AIRequest request)
    {
        if (request.ToolChoice is not null)
            throw new NotSupportedException("VibeKit unified mode does not support tool choice overrides.");

        if (request.ResponseFormat is not null)
            throw new NotSupportedException("VibeKit unified mode does not support structured response format negotiation.");

        if (request.Tools is { Count: > 0 })
            throw new NotSupportedException("VibeKit unified mode does not accept upstream tool definitions; it exposes the provider task as a synthetic provider-executed tool event stream instead.");
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
                        throw new NotSupportedException($"VibeKit unified mode only supports system, user, and assistant message roles. Role '{item.Role}' is not supported.");
                }
            }
        }

        var sections = new List<string>();
        if (instructionSections.Count > 0)
            sections.Add("instructions:\n" + string.Join("\n\n", instructionSections));

        sections.AddRange(conversationSections);
        return string.Join("\n\n", sections.Where(section => !string.IsNullOrWhiteSpace(section)));
    }

    private static void ValidateInputItem(AIInputItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Type)
            && !string.Equals(item.Type, "message", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"VibeKit unified mode only supports message input items. Item type '{item.Type}' is not supported.");
        }
    }

    private static string FormatConversationBlock(string role, string text)
        => $"{role}: {text.Trim()}";

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
                    throw new NotSupportedException("VibeKit unified mode does not support file or image content parts.");
                case AIToolCallContentPart:
                    throw new NotSupportedException("VibeKit unified mode does not support inbound tool call content parts.");
                case null:
                    break;
                default:
                    throw new NotSupportedException($"VibeKit unified mode does not support content part type '{part.Type}'.");
            }
        }

        return string.Join("\n\n", textParts);
    }

    private static AIToolOutputAvailableEventData CreateToolOutputEventData(
        string providerId,
        string toolCallId,
        VibeKitTaskStatusResponse status,
        bool preliminary)
    {
        var statusJson = TryParseJsonElement(status.RawJson);
        var structuredContent = JsonSerializer.SerializeToElement(new
        {
            content = statusJson
        }, JsonSerializerOptions.Web);

        return new AIToolOutputAvailableEventData
        {
            ProviderExecuted = true,
            Preliminary = preliminary,
            Output = new CallToolResult
            {
                Content = [new TextContentBlock { Text = status.RawJson }],
                StructuredContent = structuredContent
            },
            ProviderMetadata = new Dictionary<string, Dictionary<string, object>>
            {
                [providerId] = new Dictionary<string, object>
                {
                    ["type"] = "tool_result",
                    ["tool_name"] = "vibekit_task",
                    ["title"] = "VibeKit task",
                    ["tool_use_id"] = toolCallId,
                    ["status"] = status.Status ?? string.Empty,
                    ["task_id"] = status.TaskId ?? string.Empty,
                    ["project_id"] = status.ProjectId ?? string.Empty,
                    ["deploy_url"] = status.DeployUrl ?? string.Empty,
                    ["repo_url"] = status.RepoUrl ?? string.Empty
                }
            }
        };
    }

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

    private static IEnumerable<string> ChunkText(string text)
    {
        if (string.IsNullOrEmpty(text))
            yield break;

        const int chunkSize = 256;
        for (var i = 0; i < text.Length; i += chunkSize)
        {
            var length = Math.Min(chunkSize, text.Length - i);
            yield return text.Substring(i, length);
        }
    }

    private static string FormatAssistantJsonBlock(string rawJson)
        => "```json\n" + PrettyPrintJson(rawJson) + "\n```";

    private static string PrettyPrintJson(string rawJson)
    {
        try
        {
            using var document = JsonDocument.Parse(rawJson);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions(JsonSerializerOptions.Web)
            {
                WriteIndented = true
            });
        }
        catch
        {
            return rawJson;
        }
    }

    private static JsonElement TryParseJsonElement(string rawJson)
    {
        try
        {
            using var document = JsonDocument.Parse(rawJson);
            return document.RootElement.Clone();
        }
        catch
        {
            return JsonSerializer.SerializeToElement(new { raw = rawJson }, JsonSerializerOptions.Web);
        }
    }

    private static bool IsTerminalStatus(string? status)
        => status is not null
            && (status.Equals("complete", StringComparison.OrdinalIgnoreCase)
                || status.Equals("error", StringComparison.OrdinalIgnoreCase));

    private static bool IsCompletedStatus(string? status)
        => status is not null && status.Equals("complete", StringComparison.OrdinalIgnoreCase);

    private static string ExtractErrorMessage(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "Unknown VibeKit error.";

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

    private static Dictionary<string, object>? ToMessageMetadata(Dictionary<string, object?>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
            return null;

        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in metadata)
        {
            if (item.Value is not null)
                result[item.Key] = item.Value;
        }

        return result.Count == 0 ? null : result;
    }

    private static Dictionary<string, object>? ToProviderMetadata(Dictionary<string, object?>? metadata)
        => ToMessageMetadata(metadata);

    private static string? TryReadString(Dictionary<string, object?>? metadata, string key)
    {
        if (metadata is null || !metadata.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            string text when !string.IsNullOrWhiteSpace(text) => text,
            JsonElement json when json.ValueKind == JsonValueKind.String => json.GetString(),
            _ => value.ToString()
        };
    }

    private static List<string>? TryReadStringList(Dictionary<string, object?>? metadata, string key)
    {
        if (metadata is null || !metadata.TryGetValue(key, out var value) || value is null)
            return null;

        if (value is IEnumerable<string> strings)
            return strings.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();

        if (value is JsonElement json && json.ValueKind == JsonValueKind.Array)
        {
            var result = new List<string>();
            foreach (var item in json.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var text = item.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        result.Add(text!);
                }
            }

            return result.Count == 0 ? null : result;
        }

        return null;
    }

    private sealed record VibeKitExecutionResult(
        string TaskId,
        string Prompt,
        string SubmitPayloadJson,
        VibeKitTaskStatusResponse FinalStatus);

    private sealed record VibeKitTaskCreateResult(
        string TaskId,
        VibeKitTaskStatusResponse InitialStatus);

    private sealed class VibeKitTaskSubmitRequest
    {
        [JsonPropertyName("prompt")]
        public required string Prompt { get; init; }

        [JsonPropertyName("skills")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? Skills { get; init; }

        [JsonPropertyName("projectId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ProjectId { get; init; }

        [JsonPropertyName("callbackUrl")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CallbackUrl { get; init; }
    }

    private sealed record VibeKitTaskStatusResponse(
        [property: JsonPropertyName("taskId")] string? TaskId,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("deployUrl")] string? DeployUrl,
        [property: JsonPropertyName("repoUrl")] string? RepoUrl,
        [property: JsonPropertyName("projectId")] string? ProjectId)
    {
        [JsonIgnore]
        public string RawJson { get; init; } = "{}";
    }
}
