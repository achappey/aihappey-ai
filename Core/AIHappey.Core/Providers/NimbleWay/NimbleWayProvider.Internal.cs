using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Core.AI;
using AIHappey.Responses;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.NimbleWay;

public partial class NimbleWayProvider
{
    private const string AgentModelPrefix = "agent/";

    private async Task<NimbleWayUnifiedResult> ExecuteNimbleWayAsync(
        string? model,
        string query,
        Dictionary<string, object?>? passthrough,
        CancellationToken cancellationToken)
    {
        var localModel = ResolveLocalModelId(model);
        var agentName = TryGetAgentName(localModel);

        return !string.IsNullOrWhiteSpace(agentName)
            ? await ExecuteAgentRunAsync(agentName!, query, passthrough, cancellationToken)
            : await ExecuteSearchAsync(query, passthrough, cancellationToken);
    }

    private async Task<NimbleWayUnifiedResult> ExecuteSearchAsync(
        string query,
        Dictionary<string, object?>? passthrough,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["query"] = query
        };

        if (passthrough is not null)
        {
            foreach (var kvp in passthrough)
            {
                if (string.Equals(kvp.Key, "query", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(kvp.Key, "agent", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(kvp.Key, "params", StringComparison.OrdinalIgnoreCase))
                    continue;

                payload[kvp.Key] = kvp.Value;
            }
        }

        var response = await PostAsJsonAsync<NimbleWaySearchResponse>("v1/search", payload, cancellationToken)
            ?? new NimbleWaySearchResponse();

        return new NimbleWayUnifiedResult
        {
            Answer = response.Answer,
            RequestId = response.RequestId,
            Results = response.Results
                .Where(r => !string.IsNullOrWhiteSpace(r.Url))
                .Select(r => new NimbleWayResultItem
                {
                    Title = r.Title,
                    Url = r.Url,
                    Description = r.Description,
                    Content = r.Content
                })
                .ToList(),
            Metadata = new Dictionary<string, object?>
            {
                ["requestId"] = response.RequestId,
                ["totalResults"] = response.TotalResults,
                ["answer"] = response.Answer,
                ["results"] = response.Results
            }
        };
    }

    private async Task<NimbleWayUnifiedResult> ExecuteAgentRunAsync(
        string agentName,
        string query,
        Dictionary<string, object?>? passthrough,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["agent"] = agentName
        };

        Dictionary<string, object?> parameters = [];
        if (passthrough is not null)
        {
            if (TryGetDictionary(passthrough, "params", out var providedParams) && providedParams is not null)
                parameters = new Dictionary<string, object?>(providedParams);

            foreach (var kvp in passthrough)
            {
                if (string.Equals(kvp.Key, "params", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(kvp.Key, "agent", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(kvp.Key, "query", StringComparison.OrdinalIgnoreCase))
                    continue;

                payload[kvp.Key] = kvp.Value;
            }
        }

        if (!parameters.ContainsKey("query"))
            parameters["query"] = query;

        payload["params"] = parameters;

        var response = await PostAsJsonAsync<NimbleWayAgentRunResponse>("v1/agents/run", payload, cancellationToken)
            ?? new NimbleWayAgentRunResponse();

        var unified = new NimbleWayUnifiedResult
        {
            Answer = BuildAgentAnswerText(response),
            RequestId = response.TaskId,
            Results = [],
            Metadata = new Dictionary<string, object?>
            {
                ["taskId"] = response.TaskId,
                ["status"] = response.Status,
                ["statusCode"] = response.StatusCode,
                ["url"] = response.Url,
                ["metadata"] = response.Metadata,
                ["warnings"] = response.Warnings
            }
        };

        if (!string.IsNullOrWhiteSpace(response.Url))
        {
            unified.Results.Add(new NimbleWayResultItem
            {
                Title = response.Metadata?.TryGetProperty("agent", out var agentEl) == true && agentEl.ValueKind == JsonValueKind.String
                    ? agentEl.GetString()
                    : agentName,
                Url = response.Url,
                Description = response.Status
            });
        }

        return unified;
    }

    private async Task<T?> PostAsJsonAsync<T>(
        string endpoint,
        Dictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"NimbleWay call to '{endpoint}' failed ({(int)resp.StatusCode}): {body}");

        return JsonSerializer.Deserialize<T>(body, JsonSerializerOptions.Web);
    }

    private static string BuildPromptFromCompletionMessages(IEnumerable<ChatMessage> messages)
    {
        var all = messages?.ToList() ?? [];
        if (all.Count == 0)
            return string.Empty;

        var lines = new List<string>();
        var system = new List<string>();

        foreach (var msg in all)
        {
            var role = (msg.Role ?? string.Empty).Trim().ToLowerInvariant();
            var text = ExtractCompletionMessageText(msg.Content);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            if (role == "system")
            {
                system.Add(text!);
                continue;
            }

            if (role is not ("user" or "assistant"))
                continue;

            lines.Add($"{role}: {text}");
        }

        if (system.Count > 0)
            lines.Insert(0, $"system: {string.Join("\n\n", system)}");

        return string.Join("\n\n", lines);
    }
    
    private static string BuildPromptFromUiMessages(IEnumerable<UIMessage> messages)
    {
        var all = messages?.ToList() ?? [];
        if (all.Count == 0)
            return string.Empty;

        var lines = new List<string>();
        var msg = all.LastOrDefault(a => a.Role == Role.user);

        var text = string.Join("\n", msg?.Parts
            .OfType<TextUIPart>()
            .Select(p => p.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t)) ?? []);

        return text;
    }

    private static string BuildPromptFromResponseRequest(ResponseRequest request)
    {
        var prompt = BuildPromptFromResponseInput(request.Input);
        if (string.IsNullOrWhiteSpace(prompt))
            prompt = request.Instructions ?? string.Empty;

        return prompt;
    }

    private static string BuildPromptFromResponseInput(ResponseInput? input)
    {
        if (input is null)
            return string.Empty;

        if (input.IsText)
            return input.Text ?? string.Empty;

        if (input.IsItems != true || input.Items is null)
            return string.Empty;

        var lines = new List<string>();
        foreach (var item in input.Items)
        {
            if (item is not ResponseInputMessage message)
                continue;

            var role = message.Role.ToString().ToLowerInvariant();
            var text = message.Content.IsText
                ? message.Content.Text
                : string.Join("\n", message.Content.Parts?.OfType<InputTextPart>().Select(p => p.Text) ?? []);

            if (!string.IsNullOrWhiteSpace(text))
                lines.Add($"{role}: {text}");
        }

        return string.Join("\n\n", lines);
    }

    private static string? ExtractCompletionMessageText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString();

        if (content.ValueKind == JsonValueKind.Array)
        {
            var builder = new StringBuilder();
            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.String)
                {
                    builder.Append(part.GetString());
                    continue;
                }

                if (part.ValueKind != JsonValueKind.Object)
                    continue;

                if (part.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                    builder.Append(textEl.GetString());
                else if (part.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
                    builder.Append(contentEl.GetString());
            }

            var value = builder.ToString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        if (content.ValueKind == JsonValueKind.Object
            && content.TryGetProperty("text", out var objectText)
            && objectText.ValueKind == JsonValueKind.String)
        {
            return objectText.GetString();
        }

        return content.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? null
            : content.GetRawText();
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
            var serialized = JsonSerializer.SerializeToElement(providerRaw, JsonSerializerOptions.Web);
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
            result[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText(), JsonSerializerOptions.Web);

        return result;
    }

    private static bool TryGetDictionary(
        Dictionary<string, object?> source,
        string key,
        out Dictionary<string, object?>? value)
    {
        value = null;

        if (!source.TryGetValue(key, out var raw) || raw is null)
            return false;

        if (raw is Dictionary<string, object?> typed)
        {
            value = new Dictionary<string, object?>(typed);
            return true;
        }

        if (raw is Dictionary<string, object> boxed)
        {
            value = boxed.ToDictionary(k => k.Key, v => (object?)v.Value);
            return true;
        }

        if (raw is JsonElement jsonEl)
        {
            value = JsonElementObjectToDictionary(jsonEl);
            return value is not null;
        }

        try
        {
            var element = JsonSerializer.SerializeToElement(raw, JsonSerializerOptions.Web);
            value = JsonElementObjectToDictionary(element);
            return value is not null;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildPrimaryAnswerText(NimbleWayUnifiedResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.Answer))
            return result.Answer!;

        var first = result.Results.FirstOrDefault();
        if (first is null)
            return string.Empty;

        if (!string.IsNullOrWhiteSpace(first.Description))
            return first.Description!;

        if (!string.IsNullOrWhiteSpace(first.Title))
            return first.Title!;

        return first.Url ?? string.Empty;
    }

    private static List<string> BuildOrderedTextParts(NimbleWayUnifiedResult result)
    {
        var parts = new List<string>();
        var answer = BuildPrimaryAnswerText(result);
        if (!string.IsNullOrWhiteSpace(answer))
            parts.Add(answer);

        foreach (var item in result.Results.Where(r => !string.IsNullOrWhiteSpace(r.Url)))
        {
            if (!string.IsNullOrWhiteSpace(item.Title))
                parts.Add($"Title: {item.Title}");

            parts.Add($"URL: {item.Url}");
        }

        return parts;
    }

    private static Dictionary<string, object?> BuildResultMetadata(NimbleWayUnifiedResult result)
    {
        var metadata = result.Metadata is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(result.Metadata);

        metadata["answer"] = result.Answer;
        metadata["results"] = result.Results;
        metadata["requestId"] = result.RequestId;

        return metadata;
    }

    private static Dictionary<string, object?>? MergeMetadata(
        Dictionary<string, object?>? existing,
        Dictionary<string, object?>? add)
    {
        if (existing is null && add is null)
            return null;

        var merged = existing is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(existing);

        if (add is not null)
        {
            foreach (var kvp in add)
                merged[kvp.Key] = kvp.Value;
        }

        return merged;
    }

    private string ResolveLocalModelId(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return "search";

        var trimmed = model.Trim();
        var providerPrefix = GetIdentifier() + "/";
        if (trimmed.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            return trimmed[providerPrefix.Length..];

        return trimmed;
    }

    private static string? TryGetAgentName(string localModel)
    {
        if (!localModel.StartsWith(AgentModelPrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var agentName = localModel[AgentModelPrefix.Length..].Trim();
        return string.IsNullOrWhiteSpace(agentName) ? null : agentName;
    }

    private static string BuildAgentAnswerText(NimbleWayAgentRunResponse response)
    {
        if (response.Data.HasValue && response.Data.Value.ValueKind == JsonValueKind.Object)
        {
            var data = response.Data.Value;

            if (data.TryGetProperty("markdown", out var markdownEl)
                && markdownEl.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(markdownEl.GetString()))
            {
                return markdownEl.GetString()!;
            }

            if (data.TryGetProperty("parsing", out var parsingEl)
                && parsingEl.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                var raw = parsingEl.GetRawText();
                if (!string.IsNullOrWhiteSpace(raw) && raw != "{}" && raw != "[]")
                    return raw;
            }
        }

        return !string.IsNullOrWhiteSpace(response.Url)
            ? $"Agent result URL: {response.Url}"
            : $"Agent run finished with status '{response.Status ?? "unknown"}'.";
    }

    private static long UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private sealed class NimbleWayUnifiedResult
    {
        public string? Answer { get; init; }
        public string? RequestId { get; init; }
        public List<NimbleWayResultItem> Results { get; init; } = [];
        public Dictionary<string, object?>? Metadata { get; init; }
    }

    private sealed class NimbleWayResultItem
    {
        public string? Title { get; init; }
        public string? Url { get; init; }
        public string? Description { get; init; }
        public string? Content { get; init; }
    }

    private sealed class NimbleWaySearchResponse
    {
        [JsonPropertyName("answer")]
        public string? Answer { get; init; }

        [JsonPropertyName("total_results")]
        public int TotalResults { get; init; }

        [JsonPropertyName("results")]
        public List<NimbleWaySearchResultItem> Results { get; init; } = [];

        [JsonPropertyName("request_id")]
        public string? RequestId { get; init; }
    }

    private sealed class NimbleWaySearchResultItem
    {
        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("description")]
        public string? Description { get; init; }

        [JsonPropertyName("url")]
        public string? Url { get; init; }

        [JsonPropertyName("content")]
        public string? Content { get; init; }

        [JsonPropertyName("extra_fields")]
        public object? ExtraFields { get; init; }
    }

    private sealed class NimbleWayAgentRunResponse
    {
        [JsonPropertyName("url")]
        public string? Url { get; init; }

        [JsonPropertyName("task_id")]
        public string? TaskId { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("status_code")]
        public int? StatusCode { get; init; }

        [JsonPropertyName("data")]
        public JsonElement? Data { get; init; }

        [JsonPropertyName("metadata")]
        public JsonElement? Metadata { get; init; }

        [JsonPropertyName("warnings")]
        public List<string>? Warnings { get; init; }
    }

    private sealed class NimbleWayAgentInfo
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("display_name")]
        public string? DisplayName { get; init; }

        [JsonPropertyName("description")]
        public string? Description { get; init; }

        [JsonPropertyName("managed_by")]
        public string? ManagedBy { get; init; }
    }
}

