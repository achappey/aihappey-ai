using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.ChatCompletions.Models;
using AIHappey.Responses;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Valyu;

public partial class ValyuProvider
{
    private static readonly JsonSerializerOptions JsonWeb = JsonSerializerOptions.Web;

    private static bool IsAnswerModel(string? model)
        => !string.IsNullOrWhiteSpace(model)
           && model.StartsWith("answer/", StringComparison.OrdinalIgnoreCase);

    private static bool IsDeepResearchModel(string? model)
        => !string.IsNullOrWhiteSpace(model)
           && model.StartsWith("deepresearch/", StringComparison.OrdinalIgnoreCase);

    private static string ResolveAnswerSearchType(string? model)
    {
        var suffix = model?.Split('/').LastOrDefault()?.Trim().ToLowerInvariant();
        return suffix is "all" or "web" or "proprietary" or "news" ? suffix : "all";
    }

    private static string ResolveDeepResearchMode(string? model)
    {
        var suffix = model?.Split('/').LastOrDefault()?.Trim().ToLowerInvariant();
        return suffix is "fast" or "standard" or "heavy" or "max" ? suffix : "standard";
    }

    private async Task<ValyuAnswerResult> ExecuteAnswerAsync(
        string query,
        string searchType,
        Dictionary<string, object?>? passthrough,
        CancellationToken cancellationToken)
    {
        var fullText = new StringBuilder();
        var sources = new Dictionary<string, ValyuSource>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, object?>? lastMetadata = null;

        await foreach (var evt in StreamAnswerEventsAsync(query, searchType, passthrough, cancellationToken))
        {
            if (!string.IsNullOrWhiteSpace(evt.Delta))
                fullText.Append(evt.Delta);

            foreach (var source in evt.Sources.Where(s => !string.IsNullOrWhiteSpace(s.Url)))
                sources[source.Url!] = source;

            if (evt.Metadata is not null)
                lastMetadata = evt.Metadata;
        }

        return new ValyuAnswerResult
        {
            Text = fullText.ToString(),
            Sources = [.. sources.Values],
            Metadata = MergeMetadata(lastMetadata, new Dictionary<string, object?>
            {
                ["search_type"] = searchType,
                ["sources"] = sources.Values.ToArray()
            })
        };
    }

    private async IAsyncEnumerable<ValyuAnswerStreamEvent> StreamAnswerEventsAsync(
        string query,
        string searchType,
        Dictionary<string, object?>? passthrough,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["query"] = query,
            ["search_type"] = searchType
        };

        if (passthrough is not null)
        {
            foreach (var kvp in passthrough)
            {
                if (string.Equals(kvp.Key, "query", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(kvp.Key, "search_type", StringComparison.OrdinalIgnoreCase))
                    continue;

                payload[kvp.Key] = kvp.Value;
            }
        }

        var json = JsonSerializer.Serialize(payload, JsonWeb);
        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/answer")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Valyu answer failed ({(int)resp.StatusCode}): {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

         string? line;
        while (!cancellationToken.IsCancellationRequested &&
               (line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            if (line is null)
                yield break;

            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            var data = line["data:".Length..].Trim();
            if (data.Length == 0)
                continue;

            if (data.Equals("[DONE]", StringComparison.OrdinalIgnoreCase)
                || data.Equals("[done]", StringComparison.OrdinalIgnoreCase))
                yield break;

            ValyuAnswerStreamEvent? evt;
            try
            {
                evt = ParseAnswerStreamEvent(data);
            }
            catch
            {
                continue;
            }

            if (evt is not null)
                yield return evt;
        }
    }

    private static ValyuAnswerStreamEvent? ParseAnswerStreamEvent(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var evt = new ValyuAnswerStreamEvent();

        if (root.TryGetProperty("search_results", out var resultsEl) && resultsEl.ValueKind == JsonValueKind.Array)
            evt.Sources.AddRange(ParseSources(resultsEl));

        if (root.TryGetProperty("choices", out var choicesEl)
            && choicesEl.ValueKind == JsonValueKind.Array
            && choicesEl.GetArrayLength() > 0)
        {
            var choice = choicesEl[0];
            if (choice.ValueKind == JsonValueKind.Object
                && choice.TryGetProperty("delta", out var deltaEl)
                && deltaEl.ValueKind == JsonValueKind.Object
                && deltaEl.TryGetProperty("content", out var contentEl)
                && contentEl.ValueKind == JsonValueKind.String)
            {
                evt.Delta = contentEl.GetString();
            }
        }

        if (root.TryGetProperty("success", out _)
            || root.TryGetProperty("metadata", out _)
            || root.TryGetProperty("cost", out _)
            || root.TryGetProperty("ai_usage", out _)
            || root.TryGetProperty("search_metadata", out _))
        {
            evt.Metadata = JsonSerializer.Deserialize<Dictionary<string, object?>>(root.GetRawText(), JsonWeb);
        }

        return string.IsNullOrWhiteSpace(evt.Delta) && evt.Sources.Count == 0 && evt.Metadata is null
            ? null
            : evt;
    }

    private async Task<ValyuDeepResearchExecutionResult> ExecuteDeepResearchAsync(
        string query,
        string mode,
        Dictionary<string, object?>? passthrough,
        bool downloadArtifacts,
        CancellationToken cancellationToken)
    {
        string? taskId = null;

        try
        {
            var created = await CreateDeepResearchTaskAsync(query, mode, passthrough, cancellationToken);
            taskId = created.DeepResearchId;

            ValyuDeepResearchStatusResponse? last = null;
            while (!cancellationToken.IsCancellationRequested)
            {
                last = await GetDeepResearchStatusAsync(taskId, cancellationToken);
                if (IsDeepResearchTerminalStatus(last.Status))
                    break;

                await Task.Delay(1200, cancellationToken);
            }

            if (last is null)
                throw new InvalidOperationException("Valyu deep research returned no status.");

            var files = downloadArtifacts
                ? await DownloadDeepResearchFilePartsAsync(last, cancellationToken)
                : [];

            return new ValyuDeepResearchExecutionResult
            {
                IsSuccess = string.Equals(last.Status, "completed", StringComparison.OrdinalIgnoreCase),
                Text = ToOutputText(last.OutputType, last.Output),
                Sources = last.Sources ?? [],
                Files = files,
                Error = last.Error,
                Metadata = BuildDeepResearchMetadata(last)
            };
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(taskId))
                await TryDeleteDeepResearchTaskAsync(taskId!, cancellationToken);
        }
    }

    private async Task<ValyuDeepResearchCreateResponse> CreateDeepResearchTaskAsync(
        string query,
        string mode,
        Dictionary<string, object?>? passthrough,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["query"] = query,
            ["mode"] = mode
        };

        if (passthrough is not null)
        {
            foreach (var kvp in passthrough)
            {
                if (string.Equals(kvp.Key, "query", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(kvp.Key, "input", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(kvp.Key, "mode", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(kvp.Key, "model", StringComparison.OrdinalIgnoreCase))
                    continue;

                payload[kvp.Key] = kvp.Value;
            }
        }

        var json = JsonSerializer.Serialize(payload, JsonWeb);
        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/deepresearch/tasks")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Valyu deep research create failed ({(int)resp.StatusCode}): {raw}");

        var result = JsonSerializer.Deserialize<ValyuDeepResearchCreateResponse>(raw, JsonWeb)
                     ?? throw new InvalidOperationException("Valyu deep research create returned empty response.");

        if (string.IsNullOrWhiteSpace(result.DeepResearchId))
            throw new InvalidOperationException("Valyu deep research create response missing deepresearch_id.");

        return result;
    }

    private async Task<ValyuDeepResearchStatusResponse> GetDeepResearchStatusAsync(string taskId, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"v1/deepresearch/tasks/{Uri.EscapeDataString(taskId)}/status");
        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Valyu deep research status failed ({(int)resp.StatusCode}): {raw}");

        return JsonSerializer.Deserialize<ValyuDeepResearchStatusResponse>(raw, JsonWeb)
               ?? throw new InvalidOperationException("Valyu deep research status returned empty response.");
    }

    private async Task TryDeleteDeepResearchTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Delete, $"v1/deepresearch/tasks/{Uri.EscapeDataString(taskId)}/delete");
            using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            _ = await resp.Content.ReadAsStringAsync(cancellationToken);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private async Task<List<FileUIPart>> DownloadDeepResearchFilePartsAsync(
        ValyuDeepResearchStatusResponse status,
        CancellationToken cancellationToken)
    {
        var files = new List<FileUIPart>();

        if (!string.IsNullOrWhiteSpace(status.PdfUrl))
        {
            var pdf = await DownloadFilePartAsync(status.PdfUrl!, fallbackMime: MediaTypeNames.Application.Pdf, cancellationToken);
            if (pdf is not null)
                files.Add(pdf);
        }

        foreach (var deliverable in status.Deliverables ?? [])
        {
            if (string.IsNullOrWhiteSpace(deliverable.Url))
                continue;

            var file = await DownloadFilePartAsync(deliverable.Url!, GuessDeliverableMime(deliverable.Type), cancellationToken);
            if (file is not null)
                files.Add(file);
        }

        foreach (var image in status.Images ?? [])
        {
            if (string.IsNullOrWhiteSpace(image.ImageUrl))
                continue;

            var file = await DownloadFilePartAsync(image.ImageUrl!, MediaTypeNames.Image.Png, cancellationToken);
            if (file is not null)
                files.Add(file);
        }

        return files;
    }

    private async Task<FileUIPart?> DownloadFilePartAsync(string url, string? fallbackMime, CancellationToken cancellationToken)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!resp.IsSuccessStatusCode)
                return null;

            var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);
            var mediaType = resp.Content.Headers.ContentType?.MediaType
                            ?? fallbackMime
                            ?? GuessMimeTypeFromUrl(url);

            return bytes.ToFileUIPart(mediaType);
        }
        catch
        {
            return null;
        }
    }

    private static string GuessDeliverableMime(string? type)
        => (type ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "csv" => "text/csv",
            "xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            "docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "pdf" => MediaTypeNames.Application.Pdf,
            _ => MediaTypeNames.Application.Octet
        };

    private static string GuessMimeTypeFromUrl(string url)
    {
        var ext = Path.GetExtension(url.Split('?')[0]).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => MediaTypeNames.Application.Pdf,
            ".png" => MediaTypeNames.Image.Png,
            ".jpg" or ".jpeg" => MediaTypeNames.Image.Jpeg,
            ".gif" => MediaTypeNames.Image.Gif,
            ".webp" => "image/webp",
            ".csv" => "text/csv",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            _ => MediaTypeNames.Application.Octet
        };
    }

    private static bool IsDeepResearchTerminalStatus(string? status)
        => string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase);

    private static string ToOutputText(string? outputType, JsonElement output)
    {
        if (output.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return string.Empty;

        if (output.ValueKind == JsonValueKind.String)
            return output.GetString() ?? string.Empty;

        if (string.Equals(outputType, "json", StringComparison.OrdinalIgnoreCase)
            || output.ValueKind == JsonValueKind.Object
            || output.ValueKind == JsonValueKind.Array)
        {
            return output.GetRawText();
        }

        return output.ToString();
    }

    private static List<ValyuSource> ParseSources(JsonElement resultsEl)
    {
        var sources = new List<ValyuSource>();
        foreach (var item in resultsEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var url = item.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String
                ? urlEl.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(url))
                continue;

            sources.Add(new ValyuSource
            {
                Url = url,
                Title = item.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String
                    ? titleEl.GetString()
                    : null,
                Snippet = item.TryGetProperty("snippet", out var snipEl) && snipEl.ValueKind == JsonValueKind.String
                    ? snipEl.GetString()
                    : (item.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String
                        ? contentEl.GetString()
                        : null),
                Source = item.TryGetProperty("source", out var sourceEl) && sourceEl.ValueKind == JsonValueKind.String
                    ? sourceEl.GetString()
                    : null
            });
        }

        return sources;
    }

    private static Dictionary<string, object?> BuildDeepResearchMetadata(ValyuDeepResearchStatusResponse status)
        => new()
        {
            ["deepresearch_id"] = status.DeepResearchId,
            ["status"] = status.Status,
            ["mode"] = status.Mode,
            ["query"] = status.Query,
            ["cost"] = status.Cost,
            ["output_type"] = status.OutputType,
            ["pdf_url"] = status.PdfUrl,
            ["sources"] = status.Sources ?? [],
            ["deliverables"] = status.Deliverables ?? [],
            ["images"] = status.Images ?? [],
            ["error"] = status.Error
        };

    private static UsageInfo ExtractUsageFromMetadata(Dictionary<string, object?>? metadata)
    {
        if (metadata is null)
            return default;

        if (!metadata.TryGetValue("ai_usage", out var aiUsage) || aiUsage is null)
            return default;

        if (aiUsage is JsonElement aiEl && aiEl.ValueKind == JsonValueKind.Object)
        {
            var input = aiEl.TryGetProperty("input_tokens", out var inEl) && inEl.TryGetInt32(out var i) ? i : 0;
            var output = aiEl.TryGetProperty("output_tokens", out var outEl) && outEl.TryGetInt32(out var o) ? o : 0;
            return new UsageInfo(input, output, input + output);
        }

        if (aiUsage is Dictionary<string, object?> dict)
        {
            var input = dict.TryGetValue("input_tokens", out var iObj) ? ToInt(iObj) : 0;
            var output = dict.TryGetValue("output_tokens", out var oObj) ? ToInt(oObj) : 0;
            return new UsageInfo(input, output, input + output);
        }

        return default;
    }

    private static int ToInt(object? value)
        => value switch
        {
            null => 0,
            int i => i,
            long l => (int)l,
            double d => (int)d,
            float f => (int)f,
            JsonElement je when je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out var n) => n,
            _ => 0
        };

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
            var serialized = JsonSerializer.SerializeToElement(providerRaw, JsonWeb);
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
            result[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText(), JsonWeb);

        return result;
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

    private static long UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private sealed class ValyuAnswerStreamEvent
    {
        public string? Delta { get; set; }
        public List<ValyuSource> Sources { get; } = [];
        public Dictionary<string, object?>? Metadata { get; set; }
    }

    private sealed class ValyuAnswerResult
    {
        public string Text { get; init; } = string.Empty;
        public List<ValyuSource> Sources { get; init; } = [];
        public Dictionary<string, object?>? Metadata { get; init; }
    }

    private sealed class ValyuDeepResearchExecutionResult
    {
        public bool IsSuccess { get; init; }
        public string Text { get; init; } = string.Empty;
        public List<ValyuSource> Sources { get; init; } = [];
        public List<FileUIPart> Files { get; init; } = [];
        public string? Error { get; init; }
        public Dictionary<string, object?>? Metadata { get; init; }
    }

    private readonly record struct UsageInfo(int InputTokens, int OutputTokens, int TotalTokens);

    private sealed class ValyuSource
    {
        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("url")]
        public string? Url { get; init; }

        [JsonPropertyName("snippet")]
        public string? Snippet { get; init; }

        [JsonPropertyName("source")]
        public string? Source { get; init; }
    }

    private sealed class ValyuDeepResearchCreateResponse
    {
        [JsonPropertyName("deepresearch_id")]
        public string DeepResearchId { get; init; } = string.Empty;

        [JsonPropertyName("status")]
        public string? Status { get; init; }
    }

    private sealed class ValyuDeepResearchStatusResponse
    {
        [JsonPropertyName("deepresearch_id")]
        public string DeepResearchId { get; init; } = string.Empty;

        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("query")]
        public string? Query { get; init; }

        [JsonPropertyName("mode")]
        public string? Mode { get; init; }

        [JsonPropertyName("output_type")]
        public string? OutputType { get; init; }

        [JsonPropertyName("output")]
        public JsonElement Output { get; init; }

        [JsonPropertyName("sources")]
        public List<ValyuSource>? Sources { get; init; }

        [JsonPropertyName("cost")]
        public double? Cost { get; init; }

        [JsonPropertyName("pdf_url")]
        public string? PdfUrl { get; init; }

        [JsonPropertyName("images")]
        public List<ValyuDeepResearchImage>? Images { get; init; }

        [JsonPropertyName("deliverables")]
        public List<ValyuDeepResearchDeliverable>? Deliverables { get; init; }

        [JsonPropertyName("error")]
        public string? Error { get; init; }
    }

    private sealed class ValyuDeepResearchImage
    {
        [JsonPropertyName("image_url")]
        public string? ImageUrl { get; init; }
    }

    private sealed class ValyuDeepResearchDeliverable
    {
        [JsonPropertyName("type")]
        public string? Type { get; init; }

        [JsonPropertyName("url")]
        public string? Url { get; init; }
    }
}

