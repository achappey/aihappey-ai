using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Responses;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.NinjaChat;

public partial class NinjaChatProvider
{
    private const string NativeSearchModelId = "search";
    private static readonly JsonSerializerOptions NinjaChatJson = JsonSerializerOptions.Web;

    private sealed class NinjaChatSearchRequest
    {
        [JsonPropertyName("query")]
        public string Query { get; init; } = string.Empty;

        [JsonPropertyName("group")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Group { get; init; }

        [JsonPropertyName("max_results")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? MaxResults { get; init; }

        [JsonPropertyName("search_depth")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SearchDepth { get; init; }

        [JsonPropertyName("topic")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Topic { get; init; }

        [JsonPropertyName("include_answer")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IncludeAnswer { get; init; }

        [JsonPropertyName("include_images")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IncludeImages { get; init; }
    }

    private sealed class NinjaChatSearchResponse
    {
        [JsonPropertyName("query")]
        public string? Query { get; init; }

        [JsonPropertyName("answer")]
        public string? Answer { get; init; }

        [JsonPropertyName("sources")]
        public List<NinjaChatSearchSource> Sources { get; init; } = [];

        [JsonPropertyName("images")]
        public List<NinjaChatSearchImage> Images { get; init; } = [];

        [JsonPropertyName("follow_up_questions")]
        public List<string> FollowUpQuestions { get; init; } = [];

        [JsonPropertyName("cost")]
        public NinjaChatSearchCost? Cost { get; init; }

        [JsonPropertyName("metadata")]
        public NinjaChatSearchMetadata? Metadata { get; init; }
    }

    private sealed class NinjaChatSearchSource
    {
        [JsonPropertyName("url")]
        public string? Url { get; init; }

        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("content")]
        public string? Content { get; init; }

        [JsonPropertyName("published_date")]
        public string? PublishedDate { get; init; }
    }

    private sealed class NinjaChatSearchImage
    {
        [JsonPropertyName("url")]
        public string? Url { get; init; }

        [JsonPropertyName("description")]
        public string? Description { get; init; }
    }

    private sealed class NinjaChatSearchCost
    {
        [JsonPropertyName("this_request")]
        public string? ThisRequest { get; init; }
    }

    private sealed class NinjaChatSearchMetadata
    {
        [JsonPropertyName("group")]
        public string? Group { get; init; }

        [JsonPropertyName("search_depth")]
        public string? SearchDepth { get; init; }

        [JsonPropertyName("results_count")]
        public int? ResultsCount { get; init; }

        [JsonPropertyName("latency_ms")]
        public int? LatencyMs { get; init; }
    }

    private sealed class NinjaChatSearchExecutionResult
    {
        public string Id { get; init; } = Guid.NewGuid().ToString("n");
        public long CreatedAt { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        public NinjaChatSearchRequest Request { get; init; } = default!;
        public NinjaChatSearchResponse Response { get; init; } = default!;
        public string Text { get; init; } = string.Empty;
    }

    private static bool IsNativeSearchModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return false;

        var trimmed = model.Trim();
        if (string.Equals(trimmed, NativeSearchModelId, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!trimmed.Contains('/', StringComparison.Ordinal))
            return false;

        var split = trimmed.SplitModelId();
        return string.Equals(split.Provider, nameof(NinjaChat).ToLowerInvariant(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(split.Model, NativeSearchModelId, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<NinjaChatSearchExecutionResult> ExecuteNativeSearchAsync(
        NinjaChatSearchRequest request,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(request, NinjaChatJson);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/search")
        {
            Content = new StringContent(payload, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"NinjaChat search failed ({(int)response.StatusCode}): {body}");

        var result = JsonSerializer.Deserialize<NinjaChatSearchResponse>(body, NinjaChatJson) ?? new NinjaChatSearchResponse();

        return new NinjaChatSearchExecutionResult
        {
            Request = request,
            Response = result,
            Text = BuildNativeSearchDisplayText(result)
        };
    }


    private async Task<JsonElement> ExecuteNativeSearchMessagesAsync(
        JsonElement request,
        CancellationToken cancellationToken)
    {
        var execution = await ExecuteNativeSearchAsync(BuildNativeSearchRequest(request), cancellationToken);

        return JsonSerializer.SerializeToElement(new
        {
            id = execution.Id,
            model = ExtractModelFromMessagesRequest(request),
            role = "assistant",
            content = execution.Text,
            ui_parts = BuildNativeSearchUiParts(execution),
            metadata = BuildNativeSearchMessageMetadata(execution.Response)
        }, NinjaChatJson);
    }

    private async IAsyncEnumerable<UIMessagePart> ExecuteNativeSearchMessagesStreamingAsync(
        JsonElement request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var chatRequest = BuildNativeSearchChatRequest(request);

        await foreach (var part in ExecuteNativeSearchUiStreamAsync(chatRequest, cancellationToken))
            yield return part;
    }

    private NinjaChatSearchRequest BuildNativeSearchRequest(JsonElement request)
    {
        var passthrough = JsonElementObjectToDictionary(request);
        var query = string.Empty;

        if (request.ValueKind == JsonValueKind.Object
            && request.TryGetProperty("query", out var queryEl)
            && queryEl.ValueKind == JsonValueKind.String)
        {
            query = queryEl.GetString() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(query))
            query = BuildPromptFromMessagesRequest(request);

        return BuildNativeSearchRequest(query, passthrough);
    }

    private static NinjaChatSearchRequest BuildNativeSearchRequest(
        string query,
        Dictionary<string, object?>? passthrough)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("NinjaChat search requires a non-empty query.", nameof(query));

        return new NinjaChatSearchRequest
        {
            Query = query,
            Group = TryGetString(passthrough, "group") ?? "web",
            MaxResults = TryGetInt32(passthrough, "max_results"),
            SearchDepth = TryGetString(passthrough, "search_depth"),
            Topic = TryGetString(passthrough, "topic"),
            IncludeAnswer = TryGetBoolean(passthrough, "include_answer") ?? true,
            IncludeImages = TryGetBoolean(passthrough, "include_images")
        };
    }

    private static string BuildNativeSearchDisplayText(NinjaChatSearchResponse response)
    {
        var sections = new List<string>();

        if (!string.IsNullOrWhiteSpace(response.Answer))
            sections.Add(response.Answer!.Trim());

        if (response.Sources.Count > 0)
        {
            var sourceLines = response.Sources
                .Where(s => !string.IsNullOrWhiteSpace(s.Url) || !string.IsNullOrWhiteSpace(s.Title))
                .Select((source, index) =>
                {
                    var title = string.IsNullOrWhiteSpace(source.Title) ? $"Source {index + 1}" : source.Title!;
                    var line = $"- {title}";
                    if (!string.IsNullOrWhiteSpace(source.Url))
                        line += $"\n  {source.Url}";
                    if (!string.IsNullOrWhiteSpace(source.Content))
                        line += $"\n  {source.Content}";
                    return line;
                })
                .ToArray();

            if (sourceLines.Length > 0)
                sections.Add($"Sources:\n{string.Join("\n", sourceLines)}");
        }

        if (response.Images.Count > 0)
        {
            var imageLines = response.Images
                .Where(i => !string.IsNullOrWhiteSpace(i.Url))
                .Select((image, index) => string.IsNullOrWhiteSpace(image.Description)
                    ? $"- Image {index + 1}: {image.Url}"
                    : $"- {image.Description}: {image.Url}")
                .ToArray();

            if (imageLines.Length > 0)
                sections.Add($"Images:\n{string.Join("\n", imageLines)}");
        }

        if (response.FollowUpQuestions.Count > 0)
        {
            var followUps = response.FollowUpQuestions
                .Where(q => !string.IsNullOrWhiteSpace(q))
                .Select(q => $"- {q}")
                .ToArray();

            if (followUps.Length > 0)
                sections.Add($"Follow-up questions:\n{string.Join("\n", followUps)}");
        }

        return string.Join("\n\n", sections.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

   
    private static IEnumerable<object> BuildNativeSearchAnnotationObjects(NinjaChatSearchResponse response)
        => response.Sources
            .Where(s => !string.IsNullOrWhiteSpace(s.Url))
            .Select((source, index) => new Dictionary<string, object?>
            {
                ["type"] = "url_citation",
                ["url"] = source.Url,
                ["title"] = source.Title,
                ["source_index"] = index,
                ["content"] = source.Content
            });

    private Dictionary<string, object?> MergeMetadata(
        Dictionary<string, object?>? metadata,
        NinjaChatSearchResponse response)
    {
        var result = metadata is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(metadata);

        result["query"] = response.Query;
        result["sources"] = JsonSerializer.SerializeToElement(response.Sources, NinjaChatJson);
        result["images"] = JsonSerializer.SerializeToElement(response.Images, NinjaChatJson);
        result["follow_up_questions"] = JsonSerializer.SerializeToElement(response.FollowUpQuestions, NinjaChatJson);
        result["cost"] = response.Cost is null ? null : JsonSerializer.SerializeToElement(response.Cost, NinjaChatJson);
        result["search_metadata"] = response.Metadata is null ? null : JsonSerializer.SerializeToElement(response.Metadata, NinjaChatJson);

        return result;
    }

    private static Dictionary<string, object> BuildNativeSearchMessageMetadata(NinjaChatSearchResponse response)
    {
        var metadata = new Dictionary<string, object>();

        if (!string.IsNullOrWhiteSpace(response.Query))
            metadata["query"] = response.Query!;
        if (!string.IsNullOrWhiteSpace(response.Cost?.ThisRequest))
            metadata["cost"] = response.Cost.ThisRequest!;
        if (!string.IsNullOrWhiteSpace(response.Metadata?.Group))
            metadata["group"] = response.Metadata.Group!;
        if (!string.IsNullOrWhiteSpace(response.Metadata?.SearchDepth))
            metadata["search_depth"] = response.Metadata.SearchDepth!;
        if (response.Metadata?.ResultsCount is not null)
            metadata["results_count"] = response.Metadata.ResultsCount.Value;
        if (response.Metadata?.LatencyMs is not null)
            metadata["latency_ms"] = response.Metadata.LatencyMs.Value;

        return metadata;
    }

    private static IEnumerable<string> ChunkText(string text, int chunkSize = 180)
    {
        if (string.IsNullOrEmpty(text))
            yield break;

        for (var i = 0; i < text.Length; i += chunkSize)
            yield return text.Substring(i, Math.Min(chunkSize, text.Length - i));
    }

    private static string BuildPromptFromCompletionMessages(IEnumerable<ChatMessage>? messages)
    {
        var all = messages?.ToList() ?? [];
        if (all.Count == 0)
            return string.Empty;

        var lines = new List<string>();
        var system = new List<string>();

        foreach (var message in all)
        {
            var role = (message.Role ?? string.Empty).Trim().ToLowerInvariant();
            var text = ExtractCompletionMessageText(message.Content);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            if (role == "system")
            {
                system.Add(text!);
                continue;
            }

            lines.Add($"{role}: {text}");
        }

        if (system.Count > 0)
            lines.Insert(0, $"system: {string.Join("\n\n", system)}");

        return string.Join("\n\n", lines);
    }

    private static string BuildPromptFromUiMessages(IEnumerable<UIMessage>? messages)
    {
        var all = messages?.ToList() ?? [];
        if (all.Count == 0)
            return string.Empty;

        var lastUser = all.LastOrDefault(m => m.Role == Role.user);
        return string.Join("\n", lastUser?.Parts
            .OfType<TextUIPart>()
            .Select(p => p.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t)) ?? []);
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

    private static string BuildPromptFromMessagesRequest(JsonElement request)
    {
        if (request.ValueKind != JsonValueKind.Object)
            return string.Empty;

        if (request.TryGetProperty("input", out var input))
        {
            if (input.ValueKind == JsonValueKind.String)
                return input.GetString() ?? string.Empty;

            if (input.ValueKind == JsonValueKind.Array)
            {
                var parts = input.EnumerateArray()
                    .Select(ExtractTextFromUnknownMessageItem)
                    .Where(t => !string.IsNullOrWhiteSpace(t));
                return string.Join("\n\n", parts);
            }
        }

        if (request.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
        {
            var parts = messages.EnumerateArray()
                .Select(ExtractTextFromUnknownMessageItem)
                .Where(t => !string.IsNullOrWhiteSpace(t));
            return string.Join("\n\n", parts);
        }

        if (request.TryGetProperty("prompt", out var prompt) && prompt.ValueKind == JsonValueKind.String)
            return prompt.GetString() ?? string.Empty;

        return string.Empty;
    }

    private static string? ExtractTextFromUnknownMessageItem(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return item.ValueKind == JsonValueKind.String ? item.GetString() : null;

        var role = item.TryGetProperty("role", out var roleEl) && roleEl.ValueKind == JsonValueKind.String
            ? roleEl.GetString()
            : null;

        string? content = null;
        if (item.TryGetProperty("content", out var contentEl))
            content = ExtractCompletionMessageText(contentEl);
        else if (item.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
            content = textEl.GetString();

        if (string.IsNullOrWhiteSpace(content))
            return null;

        return string.IsNullOrWhiteSpace(role) ? content : $"{role}: {content}";
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

            var text = builder.ToString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
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
            var serialized = JsonSerializer.SerializeToElement(providerRaw, NinjaChatJson);
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

        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
            result[property.Name] = JsonSerializer.Deserialize<object?>(property.Value.GetRawText(), NinjaChatJson);

        return result;
    }

    private static string? ExtractModelFromMessagesRequest(JsonElement request)
    {
        if (request.ValueKind == JsonValueKind.Object
            && request.TryGetProperty("model", out var modelEl)
            && modelEl.ValueKind == JsonValueKind.String)
        {
            return modelEl.GetString();
        }

        return null;
    }

    private static string? TryGetString(Dictionary<string, object?>? values, string key)
        => values is not null && values.TryGetValue(key, out var value)
            ? value switch
            {
                null => null,
                string s => s,
                JsonElement el when el.ValueKind == JsonValueKind.String => el.GetString(),
                _ => value.ToString()
            }
            : null;

    private static int? TryGetInt32(Dictionary<string, object?>? values, string key)
    {
        if (values is null || !values.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            int i => i,
            long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
            JsonElement el when el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var i) => i,
            _ when int.TryParse(value.ToString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static bool? TryGetBoolean(Dictionary<string, object?>? values, string key)
    {
        if (values is null || !values.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            bool b => b,
            JsonElement el when el.ValueKind is JsonValueKind.True or JsonValueKind.False => el.GetBoolean(),
            _ when bool.TryParse(value.ToString(), out var parsed) => parsed,
            _ => null
        };
    }
}
