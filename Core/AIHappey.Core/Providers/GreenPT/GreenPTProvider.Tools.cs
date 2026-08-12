using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.GreenPT;

public partial class GreenPTProvider
{
    private const string GreenPtOcrModel = "green-ocr";
    private const string GreenPtWebSearchModel = "green-web-search";
    private const string GreenPtOcrEndpoint = "v1/tools/documents/convert/file";
    private const string GreenPtWebSearchEndpoint = "v1/tools/websearch";

    private async Task<AIResponse> ExecuteOcrUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken)
    {
        var files = GetLatestUserFiles(request, "GreenPT OCR");
        ApplyAuthHeader();

        using var form = new MultipartFormDataContent();
        var contents = new List<HttpContent>(files.Count);
        try
        {
            for (var index = 0; index < files.Count; index++)
            {
                var file = files[index];
                var bytes = DecodeFile(file, index, "GreenPT OCR");
                var filename = string.IsNullOrWhiteSpace(file.Filename) ? $"document-{index + 1}" : file.Filename!;
                var mediaType = string.IsNullOrWhiteSpace(file.MediaType) ? "application/octet-stream" : file.MediaType!;
                var content = new ByteArrayContent(bytes);
                contents.Add(content);
                content.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
                form.Add(content, "files", filename);
            }

            // Keep the public contract deliberately small and deterministic.
            form.Add(new StringContent("md"), "to_formats");
            form.Add(new StringContent("true"), "do_ocr");

            using var response = await _client.PostAsync(GreenPtOcrEndpoint, form, cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"GreenPT OCR failed ({(int)response.StatusCode}): {raw}");

            using var document = JsonDocument.Parse(raw);
            var documents = ReadConvertedDocuments(document.RootElement).ToList();
            if (documents.Count == 0)
                throw new InvalidOperationException("GreenPT OCR response did not contain document Markdown.");

            var output = documents.Select((converted, index) => new AIOutputItem
            {
                Type = "message",
                Role = "assistant",
                Metadata = new Dictionary<string, object?>
                {
                    ["filename"] = converted.Filename ?? files.ElementAtOrDefault(index)?.Filename,
                    ["fileIndex"] = index
                },
                Content = [new AITextContentPart {
                     Type = "text",
                    Text = converted.Markdown }]
            }).ToList();

            return new AIResponse
            {
                ProviderId = GetIdentifier(),
                Model = GreenPtOcrModel,
                Status = "completed",
                Output = new AIOutput { Items = output },
                Usage = new Dictionary<string, object?>(),
                Metadata = new Dictionary<string, object?>
                {
                    ["finishReason"] = "stop",
                    ["fileCount"] = files.Count,
                    ["response"] = document.RootElement.Clone()
                }
            };
        }
        finally
        {
            // MultipartFormDataContent owns these parts; this list merely keeps
            // their lifetime explicit while the request is active.
            contents.Clear();
        }
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamOcrUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = await ExecuteOcrUnifiedAsync(request, cancellationToken);
        await foreach (var item in StreamTextResponse(response, request.Id, cancellationToken))
            yield return item;
    }

    private async Task<AIResponse> ExecuteWebSearchUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken)
    {
        var query = GetLatestUserText(request);
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("GreenPT web search requires text in the latest user message.", nameof(request));

        ApplyAuthHeader();
        var payload = JsonSerializer.Serialize(new { query }, JsonSerializerOptions.Web);
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync(GreenPtWebSearchEndpoint, content, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GreenPT web search failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        var note = ReadString(root, "note") ?? string.Empty;
        var results = ReadSearchResults(root).ToList();
        var text = BuildWebSearchText(note, results);

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = GreenPtWebSearchModel,
            Status = "completed",
            Output = new AIOutput
            {
                Items =
                [
                    new AIOutputItem
                    {
                        Type = "message",
                        Role = "assistant",
                        Content = [new AITextContentPart {
                            Type = "text",
                            Text = text }],
                        Metadata = new Dictionary<string, object?>
                        {
                            ["sources"] = results.Select(result => new
                            {
                                url = result.Link,
                                title = result.Title
                            }).ToList()
                        }
                    }
                ]
            },
            Usage = new Dictionary<string, object?>(),
            Metadata = new Dictionary<string, object?>
            {
                ["finishReason"] = "stop",
                ["query"] = query,
                ["note"] = note,
                ["resultCount"] = results.Count,
                ["response"] = root.Clone()
            }
        };
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamWebSearchUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = await ExecuteWebSearchUnifiedAsync(request, cancellationToken);
        var eventId = request.Id ?? Guid.NewGuid().ToString("n");
        var timestamp = DateTimeOffset.UtcNow;

        if (response.Metadata?.TryGetValue("response", out var raw) == true && raw is JsonElement root)
        {
            var index = 0;
            foreach (var result in ReadSearchResults(root))
            {
                if (string.IsNullOrWhiteSpace(result.Link))
                    continue;

                yield return CreateToolStreamEvent(
                    $"{eventId}:source:{index++}",
                    "source-url",
                    new AISourceUrlEventData
                    {
                        SourceId = result.Link,
                        Url = result.Link,
                        Title = result.Title,
                        Type = "search_result"
                    },
                    timestamp,
                    response.Metadata);
            }
        }

        await foreach (var item in StreamTextResponse(response, eventId, cancellationToken))
            yield return item;
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamTextResponse(
        AIResponse response,
        string? requestedId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var output in response.Output?.Items ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = requestedId ?? Guid.NewGuid().ToString("n");
            var timestamp = DateTimeOffset.UtcNow;
            yield return CreateToolStreamEvent(id, "text-start", new AITextStartEventData(), timestamp, output.Metadata);

            foreach (var text in (output.Content ?? []).OfType<AITextContentPart>())
                if (!string.IsNullOrEmpty(text.Text))
                    yield return CreateToolStreamEvent(id, "text-delta", new AITextDeltaEventData { Delta = text.Text }, timestamp, output.Metadata);

            yield return CreateToolStreamEvent(id, "text-end", new AITextEndEventData(), timestamp, output.Metadata);
        }

        var completedAt = DateTimeOffset.UtcNow;
        yield return CreateToolStreamEvent(
            requestedId ?? Guid.NewGuid().ToString("n"),
            "finish",
            new AIFinishEventData
            {
                FinishReason = "stop",
                Model = response.Model,
                CompletedAt = completedAt.ToUnixTimeSeconds(),
                MessageMetadata = AIFinishMessageMetadata.Create(response.Model ?? string.Empty, completedAt, response.Usage)
            },
            completedAt,
            response.Metadata);
    }

    private AIStreamEvent CreateToolStreamEvent(
        string id,
        string type,
        object data,
        DateTimeOffset timestamp,
        Dictionary<string, object?>? metadata)
        => new()
        {
            ProviderId = GetIdentifier(),
            Metadata = metadata,
            Event = new AIEventEnvelope
            {
                Id = id,
                Type = type,
                Timestamp = timestamp,
                Data = data,
                Metadata = metadata
            }
        };

    private static List<AIFileContentPart> GetLatestUserFiles(AIRequest request, string feature)
    {
        var message = request.Input?.Items?.LastOrDefault(item =>
            string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));
        var files = message?.Content?.OfType<AIFileContentPart>().ToList() ?? [];
        if (files.Count == 0)
            throw new ArgumentException($"{feature} requires at least one attachment in the latest user message.", nameof(request));
        return files;
    }

    private static byte[] DecodeFile(AIFileContentPart file, int index, string feature)
    {
        var value = file.Data switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
            _ => null
        };
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{feature} attachment {index + 1} must contain base64 data.", nameof(file));
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{feature} attachment {index + 1} cannot be a remote URL.", nameof(file));
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = value.IndexOf(',');
            if (comma < 0 || !value[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"{feature} attachment {index + 1} must use a base64 data URL.", nameof(file));
            value = value[(comma + 1)..];
        }

        try { return Convert.FromBase64String(value.Trim()); }
        catch (FormatException exception)
        {
            throw new ArgumentException($"{feature} attachment {index + 1} contains invalid base64 data.", nameof(file), exception);
        }
    }

    private static string? GetLatestUserText(AIRequest request)
    {
        var message = request.Input?.Items?.LastOrDefault(item =>
            string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));
        var text = message?.Content?.OfType<AITextContentPart>()
            .Select(part => part.Text)
            .Where(value => !string.IsNullOrWhiteSpace(value));
        var joined = text is null ? null : string.Join("\n", text);
        return !string.IsNullOrWhiteSpace(joined) ? joined : request.Input?.Text;
    }

    private static IEnumerable<ConvertedDocument> ReadConvertedDocuments(JsonElement root)
    {
        if (root.TryGetProperty("documents", out var documents) && documents.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in documents.EnumerateArray())
            {
                var document = item.TryGetProperty("document", out var nested) ? nested : item;
                var markdown = ReadString(document, "md_content");
                if (markdown is not null)
                    yield return new ConvertedDocument(ReadString(document, "filename"), markdown);
            }
            yield break;
        }

        var single = root.TryGetProperty("document", out var documentElement) ? documentElement : root;
        var singleMarkdown = ReadString(single, "md_content");
        if (singleMarkdown is not null)
            yield return new ConvertedDocument(ReadString(single, "filename"), singleMarkdown);
    }

    private static IEnumerable<SearchResult> ReadSearchResults(JsonElement root)
    {
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var item in results.EnumerateArray())
            yield return new SearchResult(
                ReadString(item, "title"),
                ReadString(item, "link") ?? string.Empty,
                ReadString(item, "snippet"),
                ReadString(item, "relevant_content"));
    }

    private static string BuildWebSearchText(string note, IReadOnlyList<SearchResult> results)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(note))
            builder.Append(note.Trim());
        foreach (var result in results)
        {
            var content = !string.IsNullOrWhiteSpace(result.RelevantContent) ? result.RelevantContent : result.Snippet;
            if (string.IsNullOrWhiteSpace(content))
                continue;
            if (builder.Length > 0) builder.Append("\n\n");
            if (!string.IsNullOrWhiteSpace(result.Title)) builder.Append("## ").Append(result.Title).Append('\n');
            builder.Append(content.Trim());
        }
        return builder.ToString();
    }

    private static string? ReadString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed record ConvertedDocument(string? Filename, string Markdown);
    private sealed record SearchResult(string? Title, string Link, string? Snippet, string? RelevantContent);
}
