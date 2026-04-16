using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.ChatCompletions.Models;
using AIHappey.Responses;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Kirha;

public partial class KirhaProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerOptions.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<KirhaSearchResult> ExecuteKirhaSearchAsync(
        string model,
        string query,
        Dictionary<string, object?>? passthrough,
        CancellationToken cancellationToken)
    {
        var payload = BuildKirhaSearchPayload(model, query, passthrough);
        var json = JsonSerializer.Serialize(payload, Json);

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/search")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Kirha search failed ({(int)resp.StatusCode}): {body}");

        var dto = JsonSerializer.Deserialize<KirhaSearchResponse>(body, Json) ?? new KirhaSearchResponse();

        return new KirhaSearchResult
        {
            Response = dto,
            Summary = dto.Summary ?? string.Empty,
            ToolCalls = NormalizeToolCalls(dto),
            ReasoningItems = NormalizeReasoning(dto),
            Metadata = BuildKirhaMetadata(dto)
        };
    }

    private Dictionary<string, object?> BuildKirhaSearchPayload(
        string model,
        string query,
        Dictionary<string, object?>? passthrough)
    {
        var payload = passthrough is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(passthrough, StringComparer.OrdinalIgnoreCase);

        payload["query"] = query;
        payload["summarization"] = BuildSummarizationPayload(model, passthrough);
        payload["include_planning"] = true;
        payload["include_raw_data"] = true;

        return payload;
    }

    private static Dictionary<string, object?> BuildSummarizationPayload(string model, Dictionary<string, object?>? passthrough)
    {
        string? instruction = null;

        if (passthrough is not null
            && passthrough.TryGetValue("summarization", out var summarizationRaw)
            && summarizationRaw is not null)
        {
            if (summarizationRaw is JsonElement summarizationEl
                && summarizationEl.ValueKind == JsonValueKind.Object
                && summarizationEl.TryGetProperty("instruction", out var instructionEl)
                && instructionEl.ValueKind == JsonValueKind.String)
            {
                instruction = instructionEl.GetString();
            }
            else if (summarizationRaw is Dictionary<string, object?> dict
                     && dict.TryGetValue("instruction", out var instructionRaw)
                     && instructionRaw is not null)
            {
                instruction = instructionRaw?.ToString();
            }
        }

        return new Dictionary<string, object?>
        {
            ["enable"] = true,
            ["model"] = ResolveKirhaSummarizationModel(model),
            ["instruction"] = string.IsNullOrWhiteSpace(instruction) ? null : instruction
        };
    }

    private static string ResolveKirhaSummarizationModel(string? model)
    {
        var suffix = model?.Split('/').LastOrDefault()?.Trim();
        return string.IsNullOrWhiteSpace(suffix) ? "kirha" : suffix;
    }

    private static List<KirhaToolCall> NormalizeToolCalls(KirhaSearchResponse response)
    {
        var results = new List<KirhaToolCall>();

        foreach (var step in response.Planning?.Steps ?? [])
        {
            KirhaRawDataItem? raw = response.RawData.FirstOrDefault(r => string.Equals(r.StepId, step.Id, StringComparison.OrdinalIgnoreCase));

            results.Add(new KirhaToolCall
            {
                Id = step.Id ?? $"step_{results.Count + 1}",
                ToolName = step.ToolName ?? "kirha_search_step",
                Input = step.Parameters ?? new Dictionary<string, object?>(),
                Output = raw?.Output,
                Reasoning = step.Reasoning,
                Status = step.Status ?? raw?.Status,
                ProviderExecuted = true,
                RawData = raw,
                Metadata = new Dictionary<string, object?>
                {
                    ["planning_step"] = step,
                    ["raw_data"] = raw
                }
            });
        }

        foreach (var raw in response.RawData)
        {
            if (results.Any(t => string.Equals(t.Id, raw.StepId, StringComparison.OrdinalIgnoreCase)))
                continue;

            results.Add(new KirhaToolCall
            {
                Id = raw.StepId ?? $"raw_{results.Count + 1}",
                ToolName = raw.ToolName ?? "kirha_search_step",
                Input = raw.Parameters ?? new Dictionary<string, object?>(),
                Output = raw.Output,
                Status = raw.Status,
                ProviderExecuted = true,
                RawData = raw,
                Metadata = new Dictionary<string, object?>
                {
                    ["raw_data"] = raw
                }
            });
        }

        return results;
    }

    private static List<KirhaReasoningItem> NormalizeReasoning(KirhaSearchResponse response)
    {
        var reasoning = new List<KirhaReasoningItem>();

        foreach (var step in response.Planning?.Steps ?? [])
        {
            if (string.IsNullOrWhiteSpace(step.Reasoning))
                continue;

            reasoning.Add(new KirhaReasoningItem
            {
                Id = step.Id ?? Guid.NewGuid().ToString("n"),
                Text = step.Reasoning!,
                Metadata = new Dictionary<string, object?>
                {
                    ["tool_name"] = step.ToolName,
                    ["status"] = step.Status,
                    ["parameters"] = step.Parameters ?? new Dictionary<string, object?>()
                }
            });
        }

        return reasoning;
    }

    private static Dictionary<string, object?> BuildKirhaMetadata(KirhaSearchResponse response)
        => new()
        {
            ["id"] = response.Id,
            ["summary"] = response.Summary,
            ["raw_data"] = response.RawData,
            ["planning"] = response.Planning,
            ["usage"] = response.Usage,
            ["deterministicSignature"] = response.DeterministicSignature,
            ["account"] = response.Account
        };

    private static string BuildPromptFromCompletionMessages(IEnumerable<ChatMessage>? messages)
    {
        var lastUser = (messages ?? [])
            .LastOrDefault(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase));

        return lastUser is null ? string.Empty : ExtractCompletionMessageText(lastUser.Content);
    }

    private static string BuildPromptFromUiMessages(IEnumerable<UIMessage>? messages)
    {
        var lastUser = (messages ?? [])
            .LastOrDefault(m => m.Role == Role.user);

        if (lastUser?.Parts is null)
            return string.Empty;

        return string.Join("\n", lastUser.Parts
            .OfType<TextUIPart>()
            .Select(p => p.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t)));
    }

    private static string BuildPromptFromResponseRequest(ResponseRequest request)
    {
        var prompt = BuildPromptFromResponseInput(request.Input);
        if (!string.IsNullOrWhiteSpace(prompt))
            return prompt;

        return request.Instructions ?? string.Empty;
    }

    private static string BuildPromptFromResponseInput(ResponseInput? input)
    {
        if (input is null)
            return string.Empty;

        if (input.IsText)
            return input.Text ?? string.Empty;

        if (input.IsItems != true || input.Items is null)
            return string.Empty;

        var lastUser = input.Items
            .OfType<ResponseInputMessage>()
            .LastOrDefault(m => m.Role == ResponseRole.User);

        if (lastUser is null)
            return string.Empty;

        if (lastUser.Content.IsText)
            return lastUser.Content.Text ?? string.Empty;

        return string.Join("\n", lastUser.Content.Parts?
            .OfType<InputTextPart>()
            .Select(p => p.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t)) ?? []);
    }

    private static string ExtractCompletionMessageText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? string.Empty;

        if (content.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var value = item.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                        parts.Add(value!);
                    continue;
                }

                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                if (item.TryGetProperty("type", out var typeEl)
                    && typeEl.ValueKind == JsonValueKind.String
                    && string.Equals(typeEl.GetString(), "text", StringComparison.OrdinalIgnoreCase)
                    && item.TryGetProperty("text", out var textEl)
                    && textEl.ValueKind == JsonValueKind.String)
                {
                    var value = textEl.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                        parts.Add(value!);
                    continue;
                }

                if (item.TryGetProperty("text", out var genericText) && genericText.ValueKind == JsonValueKind.String)
                {
                    var value = genericText.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                        parts.Add(value!);
                }
            }

            return string.Join("\n", parts);
        }

        if (content.ValueKind == JsonValueKind.Object
            && content.TryGetProperty("text", out var objectText)
            && objectText.ValueKind == JsonValueKind.String)
        {
            return objectText.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private Dictionary<string, object?>? GetRawProviderPassthroughFromChatRequest(ChatRequest request)
    {
        var raw = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        return JsonElementObjectToDictionary(raw);
    }

    private Dictionary<string, object?>? GetRawProviderPassthroughFromResponseRequest(ResponseRequest request)
    {
        if (request.Metadata is null)
            return null;

        if (!request.Metadata.TryGetValue(GetIdentifier(), out var providerRaw) || providerRaw is null)
            return null;

        if (providerRaw is JsonElement element)
            return JsonElementObjectToDictionary(element);

        if (providerRaw is Dictionary<string, object?> typed)
            return new Dictionary<string, object?>(typed);

        if (providerRaw is Dictionary<string, object> boxed)
            return boxed.ToDictionary(k => k.Key, v => (object?)v.Value);

        try
        {
            var serialized = JsonSerializer.SerializeToElement(providerRaw, Json);
            return JsonElementObjectToDictionary(serialized);
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, object?>? JsonElementObjectToDictionary(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        var result = new Dictionary<string, object?>();
        foreach (var prop in element.EnumerateObject())
            result[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText(), Json);

        return result;
    }

    private static object BuildKirhaUsage(KirhaSearchUsage? usage)
    {
        var completionTokens = usage?.Consumed ?? 0;
        var promptTokens = usage?.Estimated ?? 0;
        return new
        {
            prompt_tokens = promptTokens,
            completion_tokens = completionTokens,
            total_tokens = promptTokens + completionTokens
        };
    }

    private static long UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static string ExtractOutputTextFromResponseOutput(IEnumerable<object> output)
    {
        var first = output?.FirstOrDefault();
        if (first is null)
            return string.Empty;

        if (first is JsonElement outputEl && outputEl.ValueKind == JsonValueKind.Object
            && outputEl.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.Array)
        {
            var firstContent = contentEl.EnumerateArray().FirstOrDefault();
            if (firstContent.ValueKind == JsonValueKind.Object
                && firstContent.TryGetProperty("text", out var textEl)
                && textEl.ValueKind == JsonValueKind.String)
            {
                return textEl.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private sealed class KirhaSearchResult
    {
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

        public object Input { get; init; } = new { };

        public object? Output { get; init; }

        public string? Reasoning { get; init; }

        public string? Status { get; init; }

        public bool ProviderExecuted { get; init; }

        public KirhaRawDataItem? RawData { get; init; }

        public Dictionary<string, object?> Metadata { get; init; } = [];
    }

    private sealed class KirhaReasoningItem
    {
        public string Id { get; init; } = default!;

        public string Text { get; init; } = default!;

        public Dictionary<string, object?> Metadata { get; init; } = [];
    }

    private sealed class KirhaSearchResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("summary")]
        public string? Summary { get; init; }

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
