using System.Net.Http.Json;
using System.Text.Json;
using AIHappey.Responses;

namespace AIHappey.Core.Providers.JigsawStack;

public partial class JigsawStackProvider
{
    private static readonly JsonSerializerOptions JsonWeb = JsonSerializerOptions.Web;

    private sealed class JigsawRoute
    {
        public required string Endpoint { get; init; }
        public required Dictionary<string, object?> Payload { get; init; }
        public string ResponseLabel { get; init; } = "result";
    }

    private sealed class JigsawExecutionResult
    {
        public required string Text { get; init; }
        public JsonElement Raw { get; init; }
    }

    private sealed class JigsawStackProviderMetadata
    {
        public string? CurrentLanguage { get; set; }
        public string? SummaryUrl { get; set; }
        public string? SummaryFileStoreKey { get; set; }
        public int? SummaryMaxCharacters { get; set; }
        public int? SummaryMaxPoints { get; set; }
        public string? SafeSearch { get; set; }
        public bool? SpellCheck { get; set; }
        public int? MaxDepth { get; set; }
        public int? MaxBreadth { get; set; }
        public int? MaxOutputTokens { get; set; }
        public int? TargetOutputTokens { get; set; }
    }

    private enum JigsawModelKind
    {
        SummaryText,
        SummaryPoints,
        WebSearch,
        DeepResearch,
        Translate
    }

    private sealed record JigsawModelInfo(JigsawModelKind Kind, string? TargetLanguage = null);

    private static JigsawModelInfo ParseModel(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        var normalized = model.Trim().ToLowerInvariant();

        if (normalized == "summary/text")
            return new(JigsawModelKind.SummaryText);

        if (normalized == "summary/points")
            return new(JigsawModelKind.SummaryPoints);

        if (normalized == "web/search")
            return new(JigsawModelKind.WebSearch);

        if (normalized == "web/deep_research")
            return new(JigsawModelKind.DeepResearch);

        const string translatePrefix = "translate/";
        if (normalized.StartsWith(translatePrefix, StringComparison.Ordinal))
        {
            var targetLanguage = normalized[translatePrefix.Length..];
            if (!string.IsNullOrWhiteSpace(targetLanguage))
                return new(JigsawModelKind.Translate, targetLanguage);
        }

        throw new NotSupportedException($"JigsawStack model '{model}' is not supported.");
    }

    private async Task<JigsawExecutionResult> ExecuteModelAsync(
        string model,
        IReadOnlyList<string> texts,
        JigsawStackProviderMetadata? metadata,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        if (texts.Count == 0 || texts.All(string.IsNullOrWhiteSpace))
            throw new ArgumentException("No prompt provided.", nameof(texts));

        var route = BuildRoute(model, texts, metadata);
        using var response = await _client.PostAsJsonAsync(route.Endpoint, route.Payload, JsonWeb, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"JigsawStack error: {(int)response.StatusCode} {response.ReasonPhrase}: {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();

        return new JigsawExecutionResult
        {
            Text = NormalizeResponseText(ParseModel(model), texts, root, route.ResponseLabel),
            Raw = root
        };
    }

    private static JigsawRoute BuildRoute(
        string model,
        IReadOnlyList<string> texts,
        JigsawStackProviderMetadata? metadata)
    {
        var info = ParseModel(model);
        var joinedText = string.Join("\n", texts.Where(t => !string.IsNullOrWhiteSpace(t)));

        return info.Kind switch
        {
            JigsawModelKind.SummaryText => new JigsawRoute
            {
                Endpoint = "v1/ai/summary",
                ResponseLabel = "summary",
                Payload = BuildSummaryPayload(joinedText, false, metadata)
            },
            JigsawModelKind.SummaryPoints => new JigsawRoute
            {
                Endpoint = "v1/ai/summary",
                ResponseLabel = "summary",
                Payload = BuildSummaryPayload(joinedText, true, metadata)
            },
            JigsawModelKind.WebSearch => new JigsawRoute
            {
                Endpoint = "v1/web/search",
                ResponseLabel = "results",
                Payload = BuildSearchPayload(joinedText, metadata)
            },
            JigsawModelKind.DeepResearch => new JigsawRoute
            {
                Endpoint = "v1/web/deep_research",
                ResponseLabel = "results",
                Payload = BuildDeepResearchPayload(joinedText, metadata)
            },
            JigsawModelKind.Translate => new JigsawRoute
            {
                Endpoint = "v1/ai/translate",
                ResponseLabel = "translated_text",
                Payload = BuildTranslatePayload(texts, info.TargetLanguage!, metadata)
            },
            _ => throw new NotSupportedException($"JigsawStack model '{model}' is not supported.")
        };
    }

    private static Dictionary<string, object?> BuildSummaryPayload(
        string text,
        bool points,
        JigsawStackProviderMetadata? metadata)
    {
        var payload = new Dictionary<string, object?>();

        if (!string.IsNullOrWhiteSpace(metadata?.SummaryUrl))
            payload["url"] = metadata.SummaryUrl;
        else if (!string.IsNullOrWhiteSpace(metadata?.SummaryFileStoreKey))
            payload["file_store_key"] = metadata.SummaryFileStoreKey;
        else
            payload["text"] = text;

        payload["type"] = points ? "points" : "text";

        if (points)
            payload["max_points"] = Math.Clamp(metadata?.SummaryMaxPoints ?? 3, 1, 100);

        if (metadata?.SummaryMaxCharacters is int maxCharacters && maxCharacters > 0)
            payload["max_characters"] = maxCharacters;

        return payload;
    }

    private static Dictionary<string, object?> BuildTranslatePayload(
        IReadOnlyList<string> texts,
        string targetLanguage,
        JigsawStackProviderMetadata? metadata)
    {
        var effectiveTexts = texts.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();

        var payload = new Dictionary<string, object?>
        {
            ["text"] = effectiveTexts.Count == 1 ? effectiveTexts[0] : effectiveTexts,
            ["target_language"] = targetLanguage
        };

        if (!string.IsNullOrWhiteSpace(metadata?.CurrentLanguage))
            payload["current_language"] = metadata.CurrentLanguage;

        return payload;
    }

    private static Dictionary<string, object?> BuildSearchPayload(string query, JigsawStackProviderMetadata? metadata)
    {
        var payload = new Dictionary<string, object?>
        {
            ["query"] = query
        };

        if (!string.IsNullOrWhiteSpace(metadata?.SafeSearch))
            payload["safe_search"] = metadata.SafeSearch;

        if (metadata?.SpellCheck is not null)
            payload["spell_check"] = metadata.SpellCheck;

        return payload;
    }

    private static Dictionary<string, object?> BuildDeepResearchPayload(string query, JigsawStackProviderMetadata? metadata)
    {
        var payload = BuildSearchPayload(query, metadata);

        if (metadata?.MaxDepth is int maxDepth)
            payload["max_depth"] = maxDepth;

        if (metadata?.MaxBreadth is int maxBreadth)
            payload["max_breadth"] = maxBreadth;

        if (metadata?.MaxOutputTokens is int maxOutputTokens)
            payload["max_output_tokens"] = maxOutputTokens;

        if (metadata?.TargetOutputTokens is int targetOutputTokens)
            payload["target_output_tokens"] = targetOutputTokens;

        return payload;
    }

    private static string NormalizeResponseText(
        JigsawModelInfo info,
        IReadOnlyList<string> originalTexts,
        JsonElement root,
        string fallbackProperty)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return root.ToString();

        return info.Kind switch
        {
            JigsawModelKind.SummaryText => ReadSummary(root),
            JigsawModelKind.SummaryPoints => ReadSummary(root),
            JigsawModelKind.Translate => ReadTranslate(root),
            JigsawModelKind.WebSearch => ReadSearchLike(root, includeSources: true),
            JigsawModelKind.DeepResearch => ReadSearchLike(root, includeSources: true),
            _ => TryReadPropertyAsText(root, fallbackProperty) ?? root.ToString()
        };
    }

    private static string ReadSummary(JsonElement root)
    {
        if (!root.TryGetProperty("summary", out var summary))
            return root.ToString();

        return summary.ValueKind switch
        {
            JsonValueKind.String => summary.GetString() ?? string.Empty,
            JsonValueKind.Array => string.Join("\n", summary.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => "- " + x.GetString())),
            _ => summary.ToString()
        };
    }

    private static string ReadTranslate(JsonElement root)
    {
        if (!root.TryGetProperty("translated_text", out var translated))
            return root.ToString();

        return translated.ValueKind switch
        {
            JsonValueKind.String => translated.GetString() ?? string.Empty,
            JsonValueKind.Array => string.Join("\n", translated.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString())),
            _ => translated.ToString()
        };
    }

    private static string ReadSearchLike(JsonElement root, bool includeSources)
    {
        var sections = new List<string>();

        if (TryReadPropertyAsText(root, "results") is { Length: > 0 } results)
            sections.Add(results);

        if (TryReadPropertyAsText(root, "answer") is { Length: > 0 } answer)
            sections.Add(answer);

        if (root.TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Array && includeSources)
        {
            var mapped = sources.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.Object)
                .Select(x =>
                {
                    var title = TryReadPropertyAsText(x, "title");
                    var url = TryReadPropertyAsText(x, "url");
                    var description = TryReadPropertyAsText(x, "description");

                    var line = string.Empty;
                    if (!string.IsNullOrWhiteSpace(title))
                        line += $"- {title}";
                    if (!string.IsNullOrWhiteSpace(url))
                        line += $" ({url})";
                    if (!string.IsNullOrWhiteSpace(description))
                        line += $": {description}";

                    return line;
                })
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Take(10)
                .ToList();

            if (mapped.Count > 0)
                sections.Add("Sources:\n" + string.Join("\n", mapped));
        }

        if (sections.Count > 0)
            return string.Join("\n\n", sections);

        return root.ToString();
    }

    private static string? TryReadPropertyAsText(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Array => string.Join("\n", value.EnumerateArray().Select(x => x.ToString())),
            JsonValueKind.Object => value.ToString(),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static IEnumerable<string> ExtractChatMessageTexts(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            var text = content.GetString();
            if (!string.IsNullOrWhiteSpace(text))
                yield return text!;

            yield break;
        }

        if (content.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object)
                continue;

            if (!part.TryGetProperty("text", out var textProp) || textProp.ValueKind != JsonValueKind.String)
                continue;

            var text = textProp.GetString();
            if (!string.IsNullOrWhiteSpace(text))
                yield return text!;
        }
    }

    private static List<string> ExtractResponseRequestTexts(ResponseRequest options)
    {
        var texts = new List<string>();

        if (options.Input?.IsText == true)
        {
            if (!string.IsNullOrWhiteSpace(options.Input.Text))
                texts.Add(options.Input.Text!);

            return texts;
        }

        var items = options.Input?.Items;
        if (items is null)
            return texts;

        foreach (var msg in items.OfType<ResponseInputMessage>().Where(m => m.Role == ResponseRole.User))
        {
            if (msg.Content.IsText)
            {
                if (!string.IsNullOrWhiteSpace(msg.Content.Text))
                    texts.Add(msg.Content.Text!);
                continue;
            }

            if (!msg.Content.IsParts || msg.Content.Parts is null)
                continue;

            foreach (var part in msg.Content.Parts.OfType<InputTextPart>())
            {
                if (!string.IsNullOrWhiteSpace(part.Text))
                    texts.Add(part.Text);
            }
        }

        return texts;
    }

   
}
