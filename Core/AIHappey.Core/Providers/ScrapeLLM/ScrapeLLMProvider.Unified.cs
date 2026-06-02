using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.ScrapeLLM;

public partial class ScrapeLLMProvider
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var modelId = request.Model;
        if (string.IsNullOrWhiteSpace(modelId))
            throw new InvalidOperationException("ScrapeLLM unified mode requires an explicit model id.");

        var scraper = ResolveScraperEndpoint(modelId!);
        var prompt = BuildPromptFromLatestUserMessage(request);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("ScrapeLLM unified mode requires non-empty text from the latest user message text parts.");

        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("ScrapeLLM API key is not configured.");

        var providerOptions = request.Metadata.GetProviderMetadata<JsonElement>(GetIdentifier());
        var query = BuildQuery(scraper, prompt, providerOptions);
        var uri = $"scrapers/{scraper}?{query}";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, uri);
        httpRequest.Headers.Remove("X-API-Key");
        httpRequest.Headers.Add("X-API-Key", key);

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw CreateScrapeHttpException(response.StatusCode, scraper, body);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement.Clone();

        var text = BuildPreferredOutputText(scraper, root);
        var outputItems = new List<AIOutputItem>
        {
            new()
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
        };

        var sourceRecords = ExtractSourceRecords(scraper, root);
        outputItems.AddRange(sourceRecords.Select(CreateSourceOutputItem));

        var metadata = BuildResponseMetadata(scraper, request, root, prompt);

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = NormalizeModelId(request.Model),
            Status = "completed",
            Usage = BuildUsage(root),
            Output = new AIOutput { Items = outputItems },
            Metadata = metadata
        };
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await ExecuteUnifiedAsync(request, cancellationToken);

        var providerId = GetIdentifier();
        var eventId = request.Id ?? $"scrapellm_{Guid.NewGuid():N}";
        var timestamp = DateTimeOffset.UtcNow;
        var metadata = response.Metadata is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(response.Metadata);

        var outputText = response.Output?.Items?
            .FirstOrDefault(item => string.Equals(item.Type, "message", StringComparison.OrdinalIgnoreCase))?
            .Content?
            .OfType<AITextContentPart>()
            .FirstOrDefault()?
            .Text
            ?? string.Empty;

        yield return CreateStreamEvent(providerId, "text-start", eventId, timestamp, new AITextStartEventData(), metadata);
        yield return CreateStreamEvent(providerId, "text-delta", eventId, timestamp, new AITextDeltaEventData { Delta = outputText }, metadata);
        yield return CreateStreamEvent(providerId, "text-end", eventId, timestamp, new AITextEndEventData(), metadata);

        foreach (var sourceItem in response.Output?.Items?.Where(item => string.Equals(item.Type, "source-url", StringComparison.OrdinalIgnoreCase)) ?? [])
        {
            var url = sourceItem.Metadata?.TryGetValue("chatcompletions.source.url", out var rawUrl) == true
                ? rawUrl?.ToString()
                : null;

            if (string.IsNullOrWhiteSpace(url))
                continue;

            var title = sourceItem.Metadata?.TryGetValue("chatcompletions.source.title", out var rawTitle) == true
                ? rawTitle?.ToString()
                : null;

            yield return CreateStreamEvent(
                providerId,
                "source-url",
                eventId,
                timestamp,
                new AISourceUrlEventData
                {
                    SourceId = url!,
                    Url = url!,
                    Title = title,
                    Type = "url",
                    ProviderMetadata = new Dictionary<string, Dictionary<string, object>>
                    {
                        [providerId] = new Dictionary<string, object>
                        {
                            ["source.url"] = url!,
                            ["source.title"] = title ?? string.Empty
                        }
                    }
                },
                metadata);
        }

        yield return CreateStreamEvent(
            providerId,
            "finish",
            eventId,
            timestamp,
            new AIFinishEventData
            {
                FinishReason = "stop",
                Model = response.Model?.ToModelId(GetIdentifier()),
                CompletedAt = timestamp.ToUnixTimeSeconds(),
                MessageMetadata = AIFinishMessageMetadata.Create(
                    model: response.Model?.ToModelId(GetIdentifier()) ?? NormalizeModelId(request.Model),
                    timestamp: timestamp,
                    usage: response.Usage,
                    temperature: request.Temperature)
            },
            metadata);
    }

    private static string BuildQuery(string scraper, string prompt, JsonElement providerOptions)
    {
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["prompt"] = prompt,
            ["country"] = "US"
        };

        if (providerOptions.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in providerOptions.EnumerateObject())
            {
                if (property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                    continue;

                var key = property.Name;
                if (string.Equals(key, "prompt", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(key, "api_key", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(key, "x-api-key", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (TryConvertQueryValue(property.Value, out var value))
                    query[key] = value!;
            }
        }

        if (string.Equals(scraper, "chatgpt", StringComparison.OrdinalIgnoreCase) && !query.ContainsKey("markdown_json"))
            query["markdown_json"] = "true";

        var builder = new StringBuilder();
        var first = true;
        foreach (var kvp in query)
        {
            if (!first)
                builder.Append('&');

            first = false;
            builder
                .Append(Uri.EscapeDataString(kvp.Key))
                .Append('=')
                .Append(Uri.EscapeDataString(kvp.Value));
        }

        return builder.ToString();
    }

    private static bool TryConvertQueryValue(JsonElement element, out string? value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                value = element.GetString();
                return !string.IsNullOrWhiteSpace(value);
            case JsonValueKind.Number:
                value = element.ToString();
                return true;
            case JsonValueKind.True:
            case JsonValueKind.False:
                value = element.GetBoolean() ? "true" : "false";
                return true;
            default:
                value = null;
                return false;
        }
    }

    private static string ResolveScraperEndpoint(string modelId)
    {
        var normalized = NormalizeModelId(modelId);
        var model = normalized.Contains('/', StringComparison.Ordinal)
            ? normalized.SplitModelId().Model
            : normalized;

        return model.ToLowerInvariant() switch
        {
            "chatgpt" => "chatgpt",
            "perplexity" => "perplexity",
            "grok" => "grok",
            "gemini" => "gemini",
            "copilot" => "copilot",
            "google-ai-mode" => "google_ai_mode",
            "google_ai_mode" => "google_ai_mode",
            "amazon-rufus" => "amazon_rufus",
            "amazon_rufus" => "amazon_rufus",
            _ => throw new InvalidOperationException($"Unsupported ScrapeLLM model '{modelId}'.")
        };
    }

    private static string NormalizeModelId(string? modelId)
        => string.IsNullOrWhiteSpace(modelId) ? string.Empty : modelId.Trim();

    private static string BuildPromptFromLatestUserMessage(AIRequest request)
    {
        if (request.Input?.Items is { Count: > 0 } items)
        {
            for (var i = items.Count - 1; i >= 0; i--)
            {
                var item = items[i];
                if (!string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))
                    continue;

                var textParts = (item.Content ?? [])
                    .OfType<AITextContentPart>()
                    .Select(part => part.Text)
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .ToList();

                if (textParts.Count > 0)
                    return string.Join("\n", textParts);
            }

            return string.Empty;
        }

        return string.Empty;
    }

    private static string BuildPreferredOutputText(string scraper, JsonElement root)
    {
        if (string.Equals(scraper, "amazon_rufus", StringComparison.OrdinalIgnoreCase))
            return BuildRufusMarkdownJsonCodeblock(root);

        var markdown = GetString(root, "result_markdown");
        if (!string.IsNullOrWhiteSpace(markdown))
            return markdown!;

        var plain = GetString(root, "result");
        return plain ?? string.Empty;
    }

    private static string BuildRufusMarkdownJsonCodeblock(JsonElement root)
    {
        JsonElement products = default;
        JsonElement relatedQuestions = default;

        if (root.TryGetProperty("products", out var productsEl) && productsEl.ValueKind == JsonValueKind.Array)
            products = productsEl.Clone();

        if (root.TryGetProperty("related_questions", out var relatedEl) && relatedEl.ValueKind == JsonValueKind.Array)
            relatedQuestions = relatedEl.Clone();

        var payload = new Dictionary<string, object?>
        {
            ["products"] = products.ValueKind == JsonValueKind.Array ? JsonSerializer.Deserialize<object>(products.GetRawText(), Json) : new List<object>(),
            ["related_questions"] = relatedQuestions.ValueKind == JsonValueKind.Array ? JsonSerializer.Deserialize<object>(relatedQuestions.GetRawText(), Json) : new List<object>()
        };

        var pretty = JsonSerializer.Serialize(payload, new JsonSerializerOptions(Json)
        {
            WriteIndented = true
        });

        return $"```json\n{pretty}\n```";
    }

    private static HttpRequestException CreateScrapeHttpException(System.Net.HttpStatusCode statusCode, string scraper, string responseBody)
    {
        var detail = TryExtractErrorDetail(responseBody) ?? responseBody;
        return new HttpRequestException(
            $"ScrapeLLM {scraper} failed ({(int)statusCode}): {detail}",
            null,
            statusCode);
    }

    private static string? TryExtractErrorDetail(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("detail", out var detailEl)
                && detailEl.ValueKind == JsonValueKind.String)
            {
                return detailEl.GetString();
            }
        }
        catch
        {
            // ignore parse failures
        }

        return null;
    }

    private static object BuildUsage(JsonElement root)
    {
        return new Dictionary<string, object?>
        {
            ["credits_used"] = TryGetInt(root, "credits_used"),
            ["elapsed_ms"] = TryGetDouble(root, "elapsed_ms")
        };
    }

    private static Dictionary<string, object?> BuildResponseMetadata(string scraper, AIRequest request, JsonElement root, string prompt)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["scrapellm.scraper"] = scraper,
            ["scrapellm.job_id"] = GetString(root, "job_id"),
            ["scrapellm.status"] = GetString(root, "status"),
            ["scrapellm.prompt"] = GetString(root, "prompt") ?? prompt,
            ["scrapellm.country"] = GetString(root, "country"),
            ["scrapellm.url"] = GetString(root, "url"),
            ["scrapellm.created_at"] = GetString(root, "created_at"),
            ["scrapellm.credits_used"] = TryGetInt(root, "credits_used"),
            ["scrapellm.elapsed_ms"] = TryGetDouble(root, "elapsed_ms"),
            ["scrapellm.cached"] = TryGetBool(root, "cached"),
            ["scrapellm.cached_at"] = GetString(root, "cached_at"),
            ["scrapellm.llm_model"] = GetString(root, "llm_model"),
            ["scrapellm.result"] = GetString(root, "result"),
            ["scrapellm.result_markdown"] = GetString(root, "result_markdown"),
            ["scrapellm.response.raw"] = root.Clone(),
            ["scrapellm.request.model"] = request.Model
        };

        return metadata;
    }

    private static IEnumerable<ScrapeSourceRecord> ExtractSourceRecords(string scraper, JsonElement root)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<ScrapeSourceRecord>();

        void Add(string? url, string? title, string type, Dictionary<string, object?>? metadata = null)
        {
            if (string.IsNullOrWhiteSpace(url) || !seen.Add(url))
                return;

            list.Add(new ScrapeSourceRecord(url!, title, type, metadata));
        }

        if (root.TryGetProperty("links", out var linksEl) && linksEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var link in linksEl.EnumerateArray())
            {
                if (link.ValueKind == JsonValueKind.String)
                {
                    Add(link.GetString(), null, "link");
                    continue;
                }

                Add(GetString(link, "url"), GetString(link, "text") ?? GetString(link, "title"), "link");
            }
        }

        Add(GetString(root, "url"), GetString(root, "scraper"), "conversation");

        AddObjectArraySources(root, "sources", "source", Add);
        AddObjectArraySources(root, "citations", "citation", Add);
        AddObjectArraySources(root, "web_sources", "web_source", Add);
        AddObjectArraySources(root, "search_results", "search_result", Add);

        if (root.TryGetProperty("products", out var productsEl) && productsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var product in productsEl.EnumerateArray())
            {
                var metadata = new Dictionary<string, object?>
                {
                    ["scrapellm.product.asin"] = GetString(product, "asin"),
                    ["scrapellm.product.rating"] = GetString(product, "rating"),
                    ["scrapellm.product.reviews"] = GetString(product, "reviews")
                };

                Add(GetString(product, "url"), GetString(product, "title"), "product", metadata);
            }
        }

        return list;
    }

    private static void AddObjectArraySources(
        JsonElement root,
        string property,
        string type,
        Action<string?, string?, string, Dictionary<string, object?>?> add)
    {
        if (!root.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return;

        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                add(item.GetString(), null, type, null);
                continue;
            }

            var meta = new Dictionary<string, object?>();
            if (item.TryGetProperty("snippet", out var snippet) && snippet.ValueKind == JsonValueKind.String)
                meta["source.snippet"] = snippet.GetString();
            if (item.TryGetProperty("website_name", out var website) && website.ValueKind == JsonValueKind.String)
                meta["source.website_name"] = website.GetString();

            add(GetString(item, "url"), GetString(item, "title") ?? GetString(item, "text"), type, meta.Count == 0 ? null : meta);
        }
    }

    private static AIOutputItem CreateSourceOutputItem(ScrapeSourceRecord source)
        => new()
        {
            Type = "source-url",
            Content =
            [
                new AITextContentPart
                {
                    Type = "text",
                    Text = source.Title ?? source.Url
                }
            ],
            Metadata = BuildSourceMetadata(source)
        };

    private static Dictionary<string, object?> BuildSourceMetadata(ScrapeSourceRecord source)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["chatcompletions.source.url"] = source.Url,
            ["chatcompletions.source.title"] = source.Title,
            ["messages.source.url"] = source.Url,
            ["messages.source.title"] = source.Title,
            ["scrapellm.source.type"] = source.Type
        };

        if (source.Metadata is not null)
        {
            foreach (var kvp in source.Metadata)
                metadata[kvp.Key] = kvp.Value;
        }

        return metadata;
    }

    private static AIStreamEvent CreateStreamEvent(
        string providerId,
        string type,
        string id,
        DateTimeOffset timestamp,
        object data,
        Dictionary<string, object?>? metadata = null)
        => new()
        {
            ProviderId = providerId,
            Event = new AIEventEnvelope
            {
                Type = type,
                Id = id,
                Timestamp = timestamp,
                Data = data,
                Metadata = metadata
            }
        };

    private static string? GetString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            return null;

        return value.GetString();
    }

    private static int? TryGetInt(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n))
            return n;

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        return null;
    }

    private static double? TryGetDouble(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n))
            return n;

        if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        return null;
    }

    private static bool? TryGetBool(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.True)
            return true;
        if (value.ValueKind == JsonValueKind.False)
            return false;
        if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed))
            return parsed;

        return null;
    }

    private sealed record ScrapeSourceRecord(string Url, string? Title, string Type, Dictionary<string, object?>? Metadata = null);
}
