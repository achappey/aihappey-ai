using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Kirha;

public partial class KirhaProvider
{
    private const string KirhaSearchToolName = "kirha_search";
    private const string KirhaCreatePlanToolName = "kirha_create_search_plan";
    private const string KirhaRunPlanToolName = "kirha_run_search_plan";
    private const string KirhaProviderMetadataKey = "kirha";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerOptions.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };    

    private async Task<(AIResponse? Response, Exception? Error)> TryExecuteUnifiedForStreamAsync(
        AIRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return (await ExecuteUnifiedAsync(request, cancellationToken), null);
        }
        catch (Exception ex)
        {
            return (null, ex);
        }
    }

    private async Task<KirhaSearchResult> ExecuteKirhaSearchAsync(
        KirhaRequestContext context,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(context.Payload, Json);

        using var req = new HttpRequestMessage(HttpMethod.Post, context.RelativePath)
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Kirha {context.OperationName} failed ({(int)resp.StatusCode}): {body}");

        var dto = JsonSerializer.Deserialize<KirhaSearchResponse>(body, Json) ?? new KirhaSearchResponse();

        return new KirhaSearchResult
        {
            Context = context,
            Response = dto,
            Summary = ResolveSummary(context, dto),
            ToolCalls = NormalizeToolCalls(context, dto),
            ReasoningItems = NormalizeReasoning(dto),
            Metadata = BuildKirhaMetadata(context, dto)
        };
    }

    private KirhaRequestContext CreateKirhaRequestContext(AIRequest request)
    {
        var providerOptions = GetUnifiedProviderOptions(request);
        var reusablePlanId = TryFindReusablePlanId(request);
        var explicitPlanId = ReadString(providerOptions, "plan_id") ?? ReadString(providerOptions, "planId");
        var requestedMode = (ReadString(providerOptions, "mode")
                             ?? ReadString(providerOptions, "search_mode")
                             ?? ReadString(providerOptions, "operation")
                             ?? ReadString(providerOptions, "action")
                             ?? string.Empty).Trim().ToLowerInvariant();

        var operation = !string.IsNullOrWhiteSpace(reusablePlanId)
            || requestedMode is "run" or "execute" or "plan_run" or "run_plan" or "execute_plan"
                ? KirhaSearchOperation.RunPlan
                : requestedMode is "plan" or "create_plan" or "planning"
                    ? KirhaSearchOperation.CreatePlan
                    : KirhaSearchOperation.AutoSearch;

        var planId = reusablePlanId ?? explicitPlanId;
        if (operation == KirhaSearchOperation.RunPlan && string.IsNullOrWhiteSpace(planId))
            throw new InvalidOperationException("Kirha plan execution requires a provider-executed Kirha plan ID in conversation history or kirha.plan_id metadata.");

        var query = ExtractLatestUserText(request);
        if (operation is KirhaSearchOperation.AutoSearch or KirhaSearchOperation.CreatePlan
            && string.IsNullOrWhiteSpace(query))
        {
            throw new InvalidOperationException("Kirha search requires text parts from the last user message.");
        }

        var payload = BuildKirhaPayload(operation, request.Model, query, planId, providerOptions);

        return new KirhaRequestContext
        {
            Operation = operation,
            Query = query,
            PlanId = planId,
            RelativePath = operation switch
            {
                KirhaSearchOperation.CreatePlan => "v1/search/plan",
                KirhaSearchOperation.RunPlan => "v1/search/plan/run",
                _ => "v1/search"
            },
            OperationName = operation switch
            {
                KirhaSearchOperation.CreatePlan => "plan",
                KirhaSearchOperation.RunPlan => "plan_run",
                _ => "search"
            },
            EndpointToolName = operation switch
            {
                KirhaSearchOperation.CreatePlan => KirhaCreatePlanToolName,
                KirhaSearchOperation.RunPlan => KirhaRunPlanToolName,
                _ => KirhaSearchToolName
            },
            Payload = payload,
            ProviderOptions = providerOptions
        };
    }

    private static Dictionary<string, object?> BuildKirhaPayload(
        KirhaSearchOperation operation,
        string? model,
        string query,
        string? planId,
        Dictionary<string, object?> providerOptions)
    {
        var payload = CopyPassthroughOptions(providerOptions);

        switch (operation)
        {
            case KirhaSearchOperation.CreatePlan:
                payload["query"] = query;
                payload.Remove("summarization");
                payload.Remove("include_raw_data");
                payload.Remove("include_planning");
                break;

            case KirhaSearchOperation.RunPlan:
                payload["plan_id"] = planId;
                EnsureSummarization(payload, model);
                EnsureBoolean(payload, "include_raw_data", true);
                EnsureBoolean(payload, "include_planning", true);
                break;

            default:
                payload["query"] = query;
                EnsureSummarization(payload, model);
                EnsureBoolean(payload, "include_raw_data", true);
                EnsureBoolean(payload, "include_planning", true);
                break;
        }

        return payload;
    }

    private static Dictionary<string, object?> CopyPassthroughOptions(Dictionary<string, object?> providerOptions)
    {
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in providerOptions)
        {
            if (IsControlOption(option.Key))
                continue;

            payload[option.Key] = option.Value;
        }

        return payload;
    }

    private static bool IsControlOption(string key)
        => key is "mode" or "search_mode" or "operation" or "action"
            or "plan_id" or "planId" or "endpoint" or "api"
            or "completed" or "plan_completed" or "capture" or "backend_capture";

    private static void EnsureSummarization(Dictionary<string, object?> payload, string? model)
    {
        if (payload.ContainsKey("summarization"))
            return;

        payload["summarization"] = new Dictionary<string, object?>
        {
            ["enable"] = true,
            ["model"] = ResolveKirhaSummarizationModel(model)
        };
    }

    private static void EnsureBoolean(Dictionary<string, object?> payload, string key, bool value)
    {
        if (!payload.ContainsKey(key))
            payload[key] = value;
    }

    private static string ResolveKirhaSummarizationModel(string? model)
    {
        var suffix = model?.Split('/').LastOrDefault()?.Trim();
        return string.IsNullOrWhiteSpace(suffix) ? "kirha" : suffix;
    }

    private AIResponse CreateUnifiedResponse(AIRequest request, KirhaSearchResult result)
    {
        var outputItems = new List<AIOutputItem>();

        foreach (var reasoning in result.ReasoningItems)
        {
            outputItems.Add(new AIOutputItem
            {
                Type = "message",
                Role = "assistant",
                Content =
                [
                    new AIReasoningContentPart
                    {
                        Type = "reasoning",
                        Text = reasoning.Text,
                        Metadata = reasoning.Metadata
                    }
                ]
            });
        }

        foreach (var toolCall in result.ToolCalls)
        {
            outputItems.Add(new AIOutputItem
            {
                Type = "message",
                Role = "assistant",
                Content =
                [
                    new AIToolCallContentPart
                    {
                        Type = "tool-call",
                        ToolCallId = toolCall.Id,
                        ToolName = toolCall.ToolName,
                        Title = toolCall.Title,
                        Input = toolCall.Input,
                        Output = toolCall.Output,
                        ProviderExecuted = toolCall.ProviderExecuted,
                        State = toolCall.State,
                        Metadata = toolCall.Metadata
                    }
                ]
            });
        }

        if (!string.IsNullOrWhiteSpace(result.Summary))
        {
            outputItems.Add(new AIOutputItem
            {
                Type = "message",
                Role = "assistant",
                Content =
                [
                    new AITextContentPart
                    {
                        Type = "text",
                        Text = result.Summary,
                        Metadata = new Dictionary<string, object?>
                        {
                            ["kirha"] = new Dictionary<string, object?>
                            {
                                ["plan_id"] = result.Response.Id,
                                ["operation"] = result.Context.OperationName,
                                ["completed"] = result.Context.Operation != KirhaSearchOperation.CreatePlan
                            }
                        }
                    }
                ]
            });
        }

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = request.Model,
            Status = "completed",
            Output = new AIOutput
            {
                Items = outputItems,
                Metadata = result.Metadata
            },
            Usage = BuildKirhaUsage(result.Response.Usage),
            Metadata = result.Metadata
        };
    }

    private static string ResolveSummary(KirhaRequestContext context, KirhaSearchResponse response)
    {
        if (!string.IsNullOrWhiteSpace(response.Summary))
            return response.Summary!;

        if (context.Operation == KirhaSearchOperation.CreatePlan && !string.IsNullOrWhiteSpace(response.Id))
            return $"Kirha created search plan {response.Id}.";

        return string.Empty;
    }

    private static List<KirhaToolCall> NormalizeToolCalls(KirhaRequestContext context, KirhaSearchResponse response)
    {
        var results = new List<KirhaToolCall>
        {
            CreateEndpointToolCall(context, response)
        };

        if (context.Operation == KirhaSearchOperation.CreatePlan)
            return results;

        foreach (var step in GetPlanningSteps(response))
        {
            KirhaRawDataItem? raw = response.RawData.FirstOrDefault(r => string.Equals(r.StepId, step.Id, StringComparison.OrdinalIgnoreCase));

            results.Add(new KirhaToolCall
            {
                Id = step.Id ?? $"kirha_step_{results.Count}",
                ToolName = step.ToolName ?? "kirha_search_step",
                Title = step.ToolName,
                Input = step.Parameters ?? new Dictionary<string, object?>(),
                Output = raw?.Output is null ? CreateKirhaToolResult(new Dictionary<string, object?>
                {
                    ["step_id"] = step.Id,
                    ["tool_name"] = step.ToolName,
                    ["status"] = step.Status,
                    ["reasoning"] = step.Reasoning
                }) : CreateKirhaToolResult(raw.Output),
                State = raw?.Status ?? step.Status ?? "output-available",
                ProviderExecuted = true,
                Metadata = CreateToolMetadata(context, response, step, raw)
            });
        }

        foreach (var raw in response.RawData)
        {
            if (results.Any(t => string.Equals(t.Id, raw.StepId, StringComparison.OrdinalIgnoreCase)))
                continue;

            results.Add(new KirhaToolCall
            {
                Id = raw.StepId ?? $"kirha_raw_{results.Count}",
                ToolName = raw.ToolName ?? "kirha_search_step",
                Title = raw.ToolName,
                Input = raw.Parameters ?? new Dictionary<string, object?>(),
                Output = CreateKirhaToolResult(raw.Output ?? new Dictionary<string, object?>()),
                State = raw.Status ?? "output-available",
                ProviderExecuted = true,
                Metadata = CreateToolMetadata(context, response, null, raw)
            });
        }

        return results;
    }

    private static KirhaToolCall CreateEndpointToolCall(KirhaRequestContext context, KirhaSearchResponse response)
    {
        var completed = context.Operation != KirhaSearchOperation.CreatePlan;
        var planId = response.Id ?? context.PlanId;
        var outputPayload = new Dictionary<string, object?>
        {
            ["provider"] = "kirha",
            ["operation"] = context.OperationName,
            ["plan_id"] = planId,
            ["completed"] = completed,
            ["status"] = response.Status ?? response.Planning?.Status,
            ["summary"] = response.Summary,
            ["planning"] = response.Planning ?? CreatePlanningFromRoot(response),
            ["raw_data"] = response.RawData,
            ["response"] = response
        };

        return new KirhaToolCall
        {
            Id = context.Operation switch
            {
                KirhaSearchOperation.CreatePlan => $"kirha-plan-{PlanIdSuffix(planId)}",
                KirhaSearchOperation.RunPlan => $"kirha-run-plan-{PlanIdSuffix(planId)}",
                _ => $"kirha-search-{PlanIdSuffix(planId)}"
            },
            ToolName = context.EndpointToolName,
            Title = context.Operation switch
            {
                KirhaSearchOperation.CreatePlan => "Create Kirha search plan",
                KirhaSearchOperation.RunPlan => "Run Kirha search plan",
                _ => "Kirha search"
            },
            Input = context.Payload,
            Output = CreateKirhaToolResult(outputPayload),
            State = "output-available",
            ProviderExecuted = true,
            Metadata = CreateToolMetadata(context, response, null, null, completed)
        };
    }

    private static string PlanIdSuffix(string? planId)
        => string.IsNullOrWhiteSpace(planId) ? Guid.NewGuid().ToString("N") : planId;

    private static List<KirhaReasoningItem> NormalizeReasoning(KirhaSearchResponse response)
    {
        var reasoning = new List<KirhaReasoningItem>();

        foreach (var step in GetPlanningSteps(response))
        {
            if (string.IsNullOrWhiteSpace(step.Reasoning))
                continue;

            reasoning.Add(new KirhaReasoningItem
            {
                Id = step.Id ?? Guid.NewGuid().ToString("n"),
                Text = step.Reasoning!,
                Metadata = new Dictionary<string, object?>
                {
                    ["kirha"] = new Dictionary<string, object?>
                    {
                        ["id"] = step.Id,
                        ["tool_name"] = step.ToolName,
                        ["status"] = step.Status,
                        ["parameters"] = step.Parameters ?? new Dictionary<string, object?>()
                    }
                }
            });
        }

        return reasoning;
    }

    private static Dictionary<string, object?> BuildKirhaMetadata(KirhaRequestContext context, KirhaSearchResponse response)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = response.Id,
            ["summary"] = response.Summary,
            ["raw_data"] = response.RawData,
            ["planning"] = response.Planning ?? CreatePlanningFromRoot(response),
            ["status"] = response.Status,
            ["usage"] = response.Usage,
            ["deterministicSignature"] = response.DeterministicSignature,
            ["account"] = response.Account,
            ["kirha.operation"] = context.OperationName,
            ["kirha.endpoint"] = context.RelativePath,
            ["kirha.plan_id"] = response.Id ?? context.PlanId,
            ["kirha.completed"] = context.Operation != KirhaSearchOperation.CreatePlan
        };

    private static Dictionary<string, object?> CreateToolMetadata(
        KirhaRequestContext context,
        KirhaSearchResponse response,
        KirhaPlanningStep? step,
        KirhaRawDataItem? raw,
        bool? completedOverride = null)
    {
        var completed = completedOverride ?? context.Operation != KirhaSearchOperation.CreatePlan;
        return new Dictionary<string, object?>
        {
            ["kirha"] = new Dictionary<string, object?>
            {
                ["operation"] = context.OperationName,
                ["endpoint"] = context.RelativePath,
                ["plan_id"] = response.Id ?? context.PlanId,
                ["completed"] = completed,
                ["tool_name"] = step?.ToolName ?? raw?.ToolName ?? context.EndpointToolName,
                ["step_id"] = step?.Id ?? raw?.StepId,
                ["status"] = step?.Status ?? raw?.Status ?? response.Status ?? response.Planning?.Status
            },
            ["planning_step"] = step,
            ["raw_data"] = raw
        };
    }

    private static CallToolResult CreateKirhaToolResult(object? payload)
        => new()
        {
            IsError = false,
            StructuredContent = JsonSerializer.SerializeToElement(payload ?? new Dictionary<string, object?>(), Json),
            Content = []
        };

    private static IEnumerable<KirhaPlanningStep> GetPlanningSteps(KirhaSearchResponse response)
        => response.Planning?.Steps is { Count: > 0 } planningSteps
            ? planningSteps
            : response.Steps;

    private static KirhaPlanning? CreatePlanningFromRoot(KirhaSearchResponse response)
        => response.Steps.Count == 0 && string.IsNullOrWhiteSpace(response.Status)
            ? null
            : new KirhaPlanning
            {
                Status = response.Status,
                Steps = response.Steps
            };

    private static string ExtractLatestUserText(AIRequest request)
    {
        foreach (var item in (request.Input?.Items ?? []).AsEnumerable().Reverse())
        {
            if (!string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))
                continue;

            var text = string.Join("\n", item.Content?
                .OfType<AITextContentPart>()
                .Select(static part => part.Text)
                .Where(static value => !string.IsNullOrWhiteSpace(value)) ?? []);

            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return request.Input?.Text ?? string.Empty;
    }

    private static Dictionary<string, object?> GetUnifiedProviderOptions(AIRequest request)
    {
        if (request.Metadata is null)
            return [];

        if (!request.Metadata.TryGetValue(KirhaProviderMetadataKey, out var raw) || raw is null)
            return [];

        if (raw is JsonElement element)
            return JsonElementObjectToDictionary(element) ?? [];

        if (raw is Dictionary<string, object?> typed)
            return new Dictionary<string, object?>(typed, StringComparer.OrdinalIgnoreCase);

        if (raw is Dictionary<string, object> boxed)
            return boxed.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value, StringComparer.OrdinalIgnoreCase);

        try
        {
            return JsonElementObjectToDictionary(JsonSerializer.SerializeToElement(raw, Json)) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static Dictionary<string, object?>? JsonElementObjectToDictionary(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in element.EnumerateObject())
            result[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText(), Json);

        return result;
    }

    private static string? ReadString(Dictionary<string, object?> dictionary, string key)
    {
        if (!dictionary.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            string text => text,
            JsonElement json when json.ValueKind == JsonValueKind.String => json.GetString(),
            _ => value.ToString()
        };
    }

    private static string? TryFindReusablePlanId(AIRequest request)
    {
        string? pendingPlanId = null;
        var completedPlanIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in request.Input?.Items ?? [])
        {
            foreach (var toolPart in item.Content?.OfType<AIToolCallContentPart>() ?? [])
            {
                if (toolPart.ProviderExecuted != true)
                    continue;

                var planInfo = ExtractPlanInfo(toolPart);
                if (string.IsNullOrWhiteSpace(planInfo.PlanId))
                    continue;

                if (planInfo.Completed)
                {
                    completedPlanIds.Add(planInfo.PlanId!);
                    if (string.Equals(pendingPlanId, planInfo.PlanId, StringComparison.OrdinalIgnoreCase))
                        pendingPlanId = null;
                    continue;
                }

                if (!completedPlanIds.Contains(planInfo.PlanId!))
                    pendingPlanId = planInfo.PlanId;
            }
        }

        return string.IsNullOrWhiteSpace(pendingPlanId) || completedPlanIds.Contains(pendingPlanId)
            ? null
            : pendingPlanId;
    }

    private static KirhaPlanInfo ExtractPlanInfo(AIToolCallContentPart toolPart)
    {
        var metadata = ExtractPlanInfo(toolPart.Metadata);
        var output = ExtractPlanInfo(toolPart.Output);
        var planId = metadata.PlanId ?? output.PlanId;
        var completed = metadata.Completed || output.Completed;

        if (string.Equals(toolPart.ToolName, KirhaRunPlanToolName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(toolPart.ToolName, KirhaSearchToolName, StringComparison.OrdinalIgnoreCase))
        {
            completed = true;
        }

        if (string.Equals(toolPart.ToolName, KirhaCreatePlanToolName, StringComparison.OrdinalIgnoreCase)
            && !metadata.Completed && !output.Completed)
        {
            completed = false;
        }

        return new KirhaPlanInfo(planId, completed);
    }

    private static KirhaPlanInfo ExtractPlanInfo(object? value)
    {
        if (value is null)
            return new KirhaPlanInfo(null, false);

        if (value is CallToolResult callToolResult && callToolResult.StructuredContent is { } structured)
            return ExtractPlanInfo(structured);

        JsonElement element;
        try
        {
            element = value is JsonElement json
                ? json
                : JsonSerializer.SerializeToElement(value, Json);
        }
        catch
        {
            return new KirhaPlanInfo(null, false);
        }

        if (element.ValueKind != JsonValueKind.Object)
            return new KirhaPlanInfo(null, false);

        if (TryGetProperty(element, "structuredContent", out var structuredContent)
            && structuredContent.ValueKind == JsonValueKind.Object)
        {
            var nested = ExtractPlanInfo(structuredContent);
            if (!string.IsNullOrWhiteSpace(nested.PlanId))
                return nested;
        }

        if (TryGetProperty(element, KirhaProviderMetadataKey, out var providerScoped)
            && providerScoped.ValueKind == JsonValueKind.Object)
        {
            var nested = ExtractPlanInfo(providerScoped);
            if (!string.IsNullOrWhiteSpace(nested.PlanId))
                return nested;
        }

        var planId = TryGetString(element, "plan_id")
                     ?? TryGetString(element, "planId")
                     ?? (TryGetString(element, "id") is { } id && id.StartsWith("plan_", StringComparison.OrdinalIgnoreCase) ? id : null);
        var operation = TryGetString(element, "operation") ?? TryGetString(element, "tool_name");
        var completed = TryGetBool(element, "completed")
                        ?? TryGetBool(element, "plan_completed")
                        ?? operation is "search" or "plan_run" or KirhaSearchToolName or KirhaRunPlanToolName;

        return new KirhaPlanInfo(planId, completed);
    }

    private static string? TryGetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(element, name, out var property) && property.ValueKind == JsonValueKind.String)
                return property.GetString();
        }

        return null;
    }

    private static bool? TryGetBool(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var property))
                continue;

            if (property.ValueKind == JsonValueKind.True)
                return true;

            if (property.ValueKind == JsonValueKind.False)
                return false;
        }

        return null;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement property)
    {
        if (element.TryGetProperty(name, out property))
            return true;

        foreach (var prop in element.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                property = prop.Value;
                return true;
            }
        }

        property = default;
        return false;
    }

    private static object BuildKirhaUsage(KirhaSearchUsage? usage)
    {
        var completionTokens = usage?.Consumed ?? 0;
        var promptTokens = usage?.Estimated ?? 0;
        return new Dictionary<string, object?>
        {
            ["prompt_tokens"] = promptTokens,
            ["completion_tokens"] = completionTokens,
            ["total_tokens"] = promptTokens + completionTokens,
            ["inputTokens"] = promptTokens,
            ["outputTokens"] = completionTokens,
            ["totalTokens"] = promptTokens + completionTokens
        };
    }

    private static (int? InputTokens, int? OutputTokens, int? TotalTokens) ExtractUsage(object? usage)
    {
        if (usage is null)
            return (null, null, null);

        var element = usage is JsonElement json ? json : JsonSerializer.SerializeToElement(usage, Json);
        if (element.ValueKind != JsonValueKind.Object)
            return (null, null, null);

        var input = TryGetInt(element, "inputTokens", "prompt_tokens", "estimated");
        var output = TryGetInt(element, "outputTokens", "completion_tokens", "consumed");
        var total = TryGetInt(element, "totalTokens", "total_tokens") ?? ((input ?? 0) + (output ?? 0));

        return (input, output, total);
    }

    private static int? TryGetInt(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var property))
                continue;

            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value))
                return value;

            if (property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out value))
                return value;
        }

        return null;
    }

    private static Dictionary<string, Dictionary<string, object>>? ToProviderMetadataEnvelope(
        Dictionary<string, object?>? metadata,
        string providerId)
    {
        if (metadata is null)
            return null;

        object? providerMetadata = null;
        if (metadata.TryGetValue(providerId, out var exact))
            providerMetadata = exact;
        else if (metadata.TryGetValue(KirhaProviderMetadataKey, out var kirha))
            providerMetadata = kirha;

        if (providerMetadata is null)
            return null;

        var normalized = JsonSerializer.SerializeToElement(providerMetadata, Json)
            .Deserialize<Dictionary<string, object>>(Json);

        return normalized is null ? null : new Dictionary<string, Dictionary<string, object>> { [providerId] = normalized };
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

    private enum KirhaSearchOperation
    {
        AutoSearch,
        CreatePlan,
        RunPlan
    }

    private sealed class KirhaRequestContext
    {
        public KirhaSearchOperation Operation { get; init; }

        public string OperationName { get; init; } = default!;

        public string RelativePath { get; init; } = default!;

        public string EndpointToolName { get; init; } = default!;

        public string Query { get; init; } = string.Empty;

        public string? PlanId { get; init; }

        public Dictionary<string, object?> Payload { get; init; } = [];

        public Dictionary<string, object?> ProviderOptions { get; init; } = [];
    }

    private sealed class KirhaSearchResult
    {
        public KirhaRequestContext Context { get; init; } = default!;

        public KirhaSearchResponse Response { get; init; } = new();

        public string Summary { get; init; } = string.Empty;

        public List<KirhaToolCall> ToolCalls { get; init; } = [];

        public List<KirhaReasoningItem> ReasoningItems { get; init; } = [];

        public Dictionary<string, object?> Metadata { get; init; } = [];
    }

    private sealed class KirhaToolCall
    {
        public string Id { get; init; } = default!;

        public string ToolName { get; init; } = default!;

        public string? Title { get; init; }

        public object Input { get; init; } = new { };

        public object? Output { get; init; }

        public string? State { get; init; }

        public bool ProviderExecuted { get; init; }

        public Dictionary<string, object?> Metadata { get; init; } = [];
    }

    private sealed class KirhaReasoningItem
    {
        public string Id { get; init; } = default!;

        public string Text { get; init; } = default!;

        public Dictionary<string, object?> Metadata { get; init; } = [];
    }

    private sealed record KirhaPlanInfo(string? PlanId, bool Completed);

    private sealed class KirhaSearchResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("summary")]
        public string? Summary { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("steps")]
        public List<KirhaPlanningStep> Steps { get; init; } = [];

        [JsonPropertyName("raw_data")]
        public List<KirhaRawDataItem> RawData { get; init; } = [];

        [JsonPropertyName("planning")]
        public KirhaPlanning? Planning { get; init; }

        [JsonPropertyName("usage")]
        public KirhaSearchUsage? Usage { get; init; }

        [JsonPropertyName("deterministicSignature")]
        public string? DeterministicSignature { get; init; }

        [JsonPropertyName("account")]
        public KirhaAccount? Account { get; init; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
    }

    private sealed class KirhaPlanning
    {
        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("steps")]
        public List<KirhaPlanningStep> Steps { get; init; } = [];

        [JsonPropertyName("reason")]
        public string? Reason { get; init; }
    }

    private sealed class KirhaPlanningStep
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("tool_name")]
        public string? ToolName { get; init; }

        [JsonPropertyName("parameters")]
        public object? Parameters { get; init; }

        [JsonPropertyName("reasoning")]
        public string? Reasoning { get; init; }
    }

    private sealed class KirhaRawDataItem
    {
        [JsonPropertyName("step_id")]
        public string? StepId { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("tool_name")]
        public string? ToolName { get; init; }

        [JsonPropertyName("parameters")]
        public object? Parameters { get; init; }

        [JsonPropertyName("output")]
        public object? Output { get; init; }
    }

    private sealed class KirhaSearchUsage
    {
        [JsonPropertyName("estimated")]
        public int Estimated { get; init; }

        [JsonPropertyName("consumed")]
        public int Consumed { get; init; }
    }

    private sealed class KirhaAccount
    {
        [JsonPropertyName("balance")]
        public decimal? Balance { get; init; }

        [JsonPropertyName("balance_timestamp")]
        public string? BalanceTimestamp { get; init; }
    }
}
