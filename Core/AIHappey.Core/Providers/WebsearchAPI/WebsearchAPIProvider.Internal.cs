using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.ChatCompletions.Models;
using AIHappey.Responses;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.WebsearchAPI;

public partial class WebsearchAPIProvider
{
    private async Task<WebsearchAiSearchResult> ExecuteAiSearchAsync(
        string query,
        Dictionary<string, object?>? passthrough,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["query"] = query,
            ["includeAnswer"] = true,
            ["includeContent"] = true
        };

        if (passthrough is not null)
        {
            foreach (var kvp in passthrough)
            {
                if (string.Equals(kvp.Key, "query", StringComparison.OrdinalIgnoreCase))
                    continue;

                payload[kvp.Key] = kvp.Value;
            }
        }

        var json = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);
        using var req = new HttpRequestMessage(HttpMethod.Post, "ai-search")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"WebsearchAPI search failed ({(int)resp.StatusCode}): {body}");

        var result = JsonSerializer.Deserialize<WebsearchAiSearchResult>(body, JsonSerializerOptions.Web);
        return result ?? new WebsearchAiSearchResult();
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
        {
            result[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText(), JsonSerializerOptions.Web);
        }

        return result;
    }

    private static string BuildPrimaryAnswerText(WebsearchAiSearchResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.Answer))
            return result.Answer!;

        var first = result.Organic.FirstOrDefault();
        if (first is null)
            return string.Empty;

        return string.IsNullOrWhiteSpace(first.Description)
            ? first.Title ?? first.Url ?? string.Empty
            : first.Description;
    }

    private static string BuildAnswerWithSourceMarkdown(WebsearchAiSearchResult result)
    {
        var answer = BuildPrimaryAnswerText(result);
        if (result.Organic.Count == 0)
            return answer;

        var sb = new StringBuilder();
        sb.Append(answer);
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("## Sources");

        foreach (var source in result.Organic.Where(s => !string.IsNullOrWhiteSpace(s.Url)))
        {
            var title = string.IsNullOrWhiteSpace(source.Title) ? source.Url! : source.Title!;
            sb.AppendLine($"<details><summary>{title}</summary>");
            sb.AppendLine();
            sb.AppendLine($"- **URL**: {source.Url}");

            if (!string.IsNullOrWhiteSpace(source.Title))
                sb.AppendLine($"- **Title**: {source.Title}");

            if (!string.IsNullOrWhiteSpace(source.Description))
                sb.AppendLine($"- **Description**: {source.Description}");

            if (!string.IsNullOrWhiteSpace(source.Content))
            {
                sb.AppendLine();
                sb.AppendLine("### Content");
                sb.AppendLine(source.Content);
            }

            sb.AppendLine();
            sb.AppendLine("</details>");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static Dictionary<string, object?> BuildResultMetadata(WebsearchAiSearchResult result)
        => new()
        {
            ["searchParameters"] = result.SearchParameters,
            ["organic"] = result.Organic,
            ["answer"] = result.Answer,
            ["responseTime"] = result.ResponseTime
        };

    private static Dictionary<string, object?>? MergeMetadata(
        Dictionary<string, object?>? existing,
        Dictionary<string, object?>? add)
    {
        if (existing is null && add is null)
            return null;

        var merged = existing is null
            ? []
            : new Dictionary<string, object?>(existing);

        if (add is not null)
        {
            foreach (var kvp in add)
                merged[kvp.Key] = kvp.Value;
        }

        return merged;
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

    private sealed class WebsearchAiSearchResult
    {
        [JsonPropertyName("searchParameters")]
        public object? SearchParameters { get; init; }

        [JsonPropertyName("answer")]
        public string? Answer { get; init; }

        [JsonPropertyName("organic")]
        public List<WebsearchOrganicResult> Organic { get; init; } = [];

        [JsonPropertyName("responseTime")]
        public double ResponseTime { get; init; }
    }

    private sealed class WebsearchOrganicResult
    {
        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("url")]
        public string? Url { get; init; }

        [JsonPropertyName("description")]
        public string? Description { get; init; }

        [JsonPropertyName("content")]
        public string? Content { get; init; }

        [JsonPropertyName("position")]
        public int? Position { get; init; }

        [JsonPropertyName("score")]
        public double? Score { get; init; }
    }
}

