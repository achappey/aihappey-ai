using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.DocsRouter;

public partial class DocsRouterProvider
{
    private const string NativeOcrEndpoint = "v1/ocr";

    private static readonly string[] NativeOcrAliases =
        ["ocr", "quality", "accuracy", "speed", "cost", "balanced"];

    private static readonly JsonSerializerOptions DocsRouterJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static bool IsNativeOcrModel(string? model)
        => TryGetNativeOcrStrategy(model, out _);

    private static bool TryGetNativeOcrStrategy(string? model, out string strategy)
    {
        var normalized = model?.Trim() ?? string.Empty;
        if (normalized.StartsWith("docsrouter/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized["docsrouter/".Length..];

        if (!NativeOcrAliases.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            strategy = string.Empty;
            return false;
        }

        strategy = string.Equals(normalized, "ocr", StringComparison.OrdinalIgnoreCase)
            ? "balanced"
            : normalized.ToLowerInvariant();
        return true;
    }

    private async Task<AIResponse> ExecuteOcrUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TryGetNativeOcrStrategy(request.Model, out var strategy))
            throw new ArgumentException($"Unsupported DocsRouter OCR model '{request.Model}'.", nameof(request));

        var files = GetLatestUserOcrFiles(request);
        var output = new List<AIOutputItem>(files.Count);
        var aggregateUsage = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        ApplyAuthHeader();
        for (var index = 0; index < files.Count; index++)
        {
            var file = NormalizeOcrFile(files[index], index);
            var payload = new JsonObject
            {
                [file.IsRemoteUrl ? "url" : "base64"] = file.Value,
                ["mime_type"] = file.IsRemoteUrl ? null : file.MediaType,
                ["strategy"] = strategy,
                ["options"] = new JsonObject { ["output_format"] = "markdown" }
            };

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, NativeOcrEndpoint)
            {
                Content = new StringContent(
                    payload.ToJsonString(DocsRouterJsonOptions),
                    Encoding.UTF8,
                    MediaTypeNames.Application.Json)
            };
            using var response = await _client.SendAsync(httpRequest, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"DocsRouter OCR failed for '{file.Filename}' ({(int)response.StatusCode}): {body}");

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var markdown = root.TryGetProperty("result", out var result)
                && result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String
                    ? text.GetString() ?? string.Empty
                    : string.Empty;

            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                AddOcrUsage(aggregateUsage, usage);

            var resolvedModel = root.TryGetProperty("model", out var modelElement)
                && modelElement.ValueKind == JsonValueKind.String
                    ? modelElement.GetString()
                    : null;
            var requestId = root.TryGetProperty("id", out var idElement)
                && idElement.ValueKind == JsonValueKind.String
                    ? idElement.GetString()
                    : null;

            output.Add(new AIOutputItem
            {
                Type = "message",
                Role = "assistant",
                Metadata = new Dictionary<string, object?>
                {
                    ["filename"] = file.Filename,
                    ["mediaType"] = file.MediaType,
                    ["fileIndex"] = index,
                    ["strategy"] = strategy,
                    ["requestId"] = requestId,
                    ["resolvedModel"] = resolvedModel
                },
                Content = [new AITextContentPart { Type = "text", Text = markdown }]
            });
        }

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = request.Model,
            Status = "completed",
            Output = new AIOutput { Items = output },
            Usage = aggregateUsage,
            Metadata = new Dictionary<string, object?>
            {
                ["finishReason"] = "stop",
                ["fileCount"] = files.Count,
                ["strategy"] = strategy,
                ["outputFormat"] = "markdown"
            }
        };
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamDocsRouterUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = await ExecuteUnifiedAsync(request, cancellationToken);
        await foreach (var streamEvent in StreamCompletedResponseAsync(response, request, cancellationToken))
            yield return streamEvent;
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamCompletedResponseAsync(
        AIResponse response,
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var item in response.Output?.Items ?? [])
        {
            foreach (var text in (item.Content ?? []).OfType<AITextContentPart>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var eventId = Guid.NewGuid().ToString("n");
                var timestamp = DateTimeOffset.UtcNow;
                yield return CreateDocsRouterStreamEvent(eventId, "text-start", new AITextStartEventData(), timestamp, item.Metadata);
                if (!string.IsNullOrEmpty(text.Text))
                    yield return CreateDocsRouterStreamEvent(
                        eventId,
                        "text-delta",
                        new AITextDeltaEventData { Delta = text.Text },
                        timestamp,
                        item.Metadata);
                yield return CreateDocsRouterStreamEvent(eventId, "text-end", new AITextEndEventData(), timestamp, item.Metadata);
            }
        }

        var completedAt = DateTimeOffset.UtcNow;
        yield return CreateDocsRouterStreamEvent(
            request.Id ?? Guid.NewGuid().ToString("n"),
            "finish",
            new AIFinishEventData
            {
                FinishReason = "stop",
                Model = response.Model,
                CompletedAt = completedAt.ToUnixTimeSeconds(),
                MessageMetadata = AIFinishMessageMetadata.Create(
                    response.Model ?? request.Model ?? string.Empty,
                    completedAt,
                    response.Usage)
            },
            completedAt,
            response.Metadata,
            response.Output);

        await Task.CompletedTask;
    }

    private AIStreamEvent CreateDocsRouterStreamEvent(
        string id,
        string type,
        object data,
        DateTimeOffset timestamp,
        Dictionary<string, object?>? metadata,
        AIOutput? output = null)
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
                Metadata = metadata,
                Output = output
            }
        };

    private static List<AIFileContentPart> GetLatestUserOcrFiles(AIRequest request)
    {
        var latestUserMessage = request.Input?.Items?
            .LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));
        var files = latestUserMessage?.Content?.OfType<AIFileContentPart>().ToList() ?? [];
        if (files.Count == 0)
            throw new ArgumentException(
                "DocsRouter OCR requires at least one file in the latest user message.",
                nameof(request));
        return files;
    }

    private static NormalizedDocsRouterOcrFile NormalizeOcrFile(AIFileContentPart file, int index)
    {
        var value = file.Data switch
        {
            string text => text.Trim(),
            JsonElement json when json.ValueKind == JsonValueKind.String => json.GetString()?.Trim(),
            _ => null
        };
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"DocsRouter OCR file {index + 1} is empty.", nameof(file));

        var filename = string.IsNullOrWhiteSpace(file.Filename) ? $"document-{index + 1}" : file.Filename!;
        var mediaType = string.IsNullOrWhiteSpace(file.MediaType) ? "application/octet-stream" : file.MediaType!;
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return new NormalizedDocsRouterOcrFile(filename, mediaType, value, true);

        var base64 = value;
        if (base64.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = base64.IndexOf(',');
            if (comma < 0 || !base64[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    $"DocsRouter OCR file {index + 1} must use a base64 data URL.",
                    nameof(file));
            var header = base64[5..comma];
            var separator = header.IndexOf(';');
            if (separator > 0)
                mediaType = header[..separator];
            base64 = base64[(comma + 1)..];
        }

        try
        {
            _ = Convert.FromBase64String(base64);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException(
                $"DocsRouter OCR file {index + 1} contains invalid base64 data.",
                nameof(file),
                exception);
        }

        return new NormalizedDocsRouterOcrFile(filename, mediaType, base64, false);
    }

    private static void AddOcrUsage(Dictionary<string, object?> aggregate, JsonElement usage)
    {
        foreach (var property in usage.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Number
                && property.Value.TryGetDecimal(out var number))
            {
                var current = aggregate.TryGetValue(property.Name, out var existing)
                    && existing is decimal decimalValue
                        ? decimalValue
                        : 0m;
                aggregate[property.Name] = current + number;
            }
            else if (!aggregate.ContainsKey(property.Name))
            {
                aggregate[property.Name] = property.Value.Clone();
            }
        }
    }

    private sealed record NormalizedDocsRouterOcrFile(
        string Filename,
        string MediaType,
        string Value,
        bool IsRemoteUrl);
}
