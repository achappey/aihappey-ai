using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.LLMLayer;

public partial class LLMLayerProvider
{
    public async Task<AIResponse> ExecuteUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateUnifiedRequest(request, streaming: false);
        ApplyAuthHeader();

        var payload = BuildUnifiedPayload(request);
        var answer = await ExecuteAnswerAsync(payload, cancellationToken);

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = request.Model,
            Status = "completed",
            Usage = BuildUsage(answer),
            Output = new AIOutput { Items = BuildUnifiedOutputItems(answer) },
            Metadata = BuildUnifiedMetadata(request.Metadata, answer)
        };
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateUnifiedRequest(request, streaming: true);
        ApplyAuthHeader();

        var providerId = GetIdentifier();
        var eventId = request.Id ?? $"llmlayer_{Guid.NewGuid():N}";
        var payload = BuildUnifiedPayload(request);
        var timestamp = DateTimeOffset.UtcNow;
        var textStarted = false;
        var completed = false;
        int? inputTokens = null;
        int? outputTokens = null;
        decimal? modelCost = null;
        decimal? llmlayerCost = null;
        string? responseTime = null;

        await foreach (var evt in ExecuteAnswerStreamingAsync(payload, cancellationToken))
        {
            timestamp = DateTimeOffset.UtcNow;

            switch (evt.Type)
            {
                case "sources":
                    if (evt.Root.TryGetProperty("data", out var sources) && sources.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var source in sources.EnumerateArray())
                        {
                            var url = GetString(source, "link");
                            if (string.IsNullOrWhiteSpace(url))
                                continue;

                            yield return CreateUnifiedEvent(
                                "source-url",
                                $"{eventId}_source_{Guid.NewGuid():N}",
                                timestamp,
                                new AISourceUrlEventData
                                {
                                    SourceId = url,
                                    Url = url,
                                    Title = GetString(source, "title"),
                                    Type = "url_citation",
                                    ProviderMetadata = CreateScopedMetadata(new Dictionary<string, object?>
                                    {
                                        ["snippet"] = GetString(source, "snippet")
                                    })
                                });
                        }
                    }
                    break;

                case "images":
                    if (evt.Root.TryGetProperty("data", out var images) && images.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var image in images.EnumerateArray())
                        {
                            var url = GetString(image, "imageUrl");
                            if (string.IsNullOrWhiteSpace(url))
                                continue;

                            yield return CreateUnifiedEvent(
                                "file",
                                $"{eventId}_image_{Guid.NewGuid():N}",
                                timestamp,
                                new AIFileEventData
                                {
                                    MediaType = GuessImageMediaType(url),
                                    Filename = GuessImageFilename(url),
                                    Url = url,
                                    ProviderMetadata = CreateScopedMetadata(ToDictionary(image))
                                });
                        }
                    }
                    break;

                case "answer":
                    var delta = GetString(evt.Root, "content");
                    if (string.IsNullOrEmpty(delta))
                        break;

                    if (!textStarted)
                    {
                        textStarted = true;
                        yield return CreateUnifiedEvent("text-start", eventId, timestamp, new AITextStartEventData());
                    }

                    yield return CreateUnifiedEvent("text-delta", eventId, timestamp, new AITextDeltaEventData { Delta = delta });
                    break;

                case "usage":
                    inputTokens = TryGetInt32(evt.Root, "input_tokens") ?? inputTokens;
                    outputTokens = TryGetInt32(evt.Root, "output_tokens") ?? outputTokens;
                    modelCost = TryGetDecimal(evt.Root, "model_cost") ?? modelCost;
                    llmlayerCost = TryGetDecimal(evt.Root, "llmlayer_cost") ?? llmlayerCost;
                    break;

                case "error":
                    if (textStarted)
                        yield return CreateUnifiedEvent("text-end", eventId, timestamp, new AITextEndEventData());

                    yield return CreateUnifiedEvent(
                        "error",
                        eventId,
                        timestamp,
                        new AIErrorEventData
                        {
                            ErrorText = GetString(evt.Root, "message") ?? "LLMLayer stream returned an error event."
                        });
                    yield break;

                case "done":
                    responseTime = GetString(evt.Root, "response_time");
                    completed = true;
                    break;
            }

            if (completed)
                break;
        }

        timestamp = DateTimeOffset.UtcNow;
        if (textStarted)
            yield return CreateUnifiedEvent("text-end", eventId, timestamp, new AITextEndEventData());

        var usage = new Dictionary<string, object?>
        {
            ["prompt_tokens"] = inputTokens ?? 0,
            ["completion_tokens"] = outputTokens ?? 0,
            ["total_tokens"] = (inputTokens ?? 0) + (outputTokens ?? 0),
            ["model_cost"] = modelCost,
            ["llmlayer_cost"] = llmlayerCost,
            ["response_time"] = responseTime
        };

        yield return CreateUnifiedEvent(
            "finish",
            eventId,
            timestamp,
            new AIFinishEventData
            {
                FinishReason = "stop",
                Model = request.Model ?? string.Empty,
                CompletedAt = timestamp.ToUnixTimeSeconds(),
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                TotalTokens = (inputTokens ?? 0) + (outputTokens ?? 0),
                MessageMetadata = AIFinishMessageMetadata.Create(
                    request.Model ?? string.Empty,
                    timestamp,
                    usage,
                    outputTokens,
                    inputTokens,
                    (inputTokens ?? 0) + (outputTokens ?? 0),
                    request.Temperature,
                    additionalProperties: new Dictionary<string, object?>
                    {
                        ["model_cost"] = modelCost,
                        ["llmlayer_cost"] = llmlayerCost,
                        ["response_time"] = responseTime
                    })
            });
    }

    private Dictionary<string, object?> BuildUnifiedPayload(AIRequest request)
    {
        var payload = BuildAnswerPayload(
            BuildPromptFromUnifiedRequest(request),
            request.Model ?? string.Empty,
            request.Temperature,
            request.MaxOutputTokens,
            ExtractLlmlayerMetadata(request.Metadata));

        var systemPrompt = BuildSystemPromptFromUnifiedRequest(request);
        if (!string.IsNullOrWhiteSpace(systemPrompt) && !payload.ContainsKey("system_prompt"))
            payload["system_prompt"] = systemPrompt;

        ApplyStructuredOutputIfAny(payload, request.ResponseFormat);
        return payload;
    }

    private static void ValidateUnifiedRequest(AIRequest request, bool streaming)
    {
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new InvalidOperationException("LLMLayer requires a model.");

        if (request.Tools is { Count: > 0 } || request.ToolChoice is not null)
            throw new NotSupportedException("LLMLayer Answer API does not support tools or tool choice.");

        var unsupported = request.Input?.Items?
            .SelectMany(item => item.Content ?? [])
            .Any(part => part is not AITextContentPart) == true;
        if (unsupported)
            throw new NotSupportedException("LLMLayer Answer API supports text input only.");

        if (string.IsNullOrWhiteSpace(BuildPromptFromUnifiedRequest(request)))
            throw new InvalidOperationException("LLMLayer requires non-empty text input.");

        if (streaming && TryExtractStructuredOutputSchemaString(request.ResponseFormat) is not null)
            throw new NotSupportedException("LLMLayer streaming does not support structured JSON output.");
    }

    private static string BuildPromptFromUnifiedRequest(AIRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Input?.Text))
            return request.Input.Text!;

        return string.Join("\n\n", (request.Input?.Items ?? [])
            .Where(item => !string.Equals(item.Role, "system", StringComparison.OrdinalIgnoreCase))
            .Select(item => new
            {
                Role = string.IsNullOrWhiteSpace(item.Role) ? "user" : item.Role!.ToLowerInvariant(),
                Text = string.Join("\n", (item.Content ?? []).OfType<AITextContentPart>()
                    .Select(part => part.Text).Where(text => !string.IsNullOrWhiteSpace(text)))
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Text))
            .Select(item => $"{item.Role}: {item.Text}"));
    }

    private static string? BuildSystemPromptFromUnifiedRequest(AIRequest request)
    {
        var values = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            values.Add(request.Instructions!);

        values.AddRange((request.Input?.Items ?? [])
            .Where(item => string.Equals(item.Role, "system", StringComparison.OrdinalIgnoreCase))
            .SelectMany(item => item.Content ?? [])
            .OfType<AITextContentPart>()
            .Select(part => part.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text)));

        return values.Count == 0 ? null : string.Join("\n\n", values);
    }

    private static List<AIOutputItem> BuildUnifiedOutputItems(LLMLayerAnswerResponse answer)
    {
        var items = new List<AIOutputItem>
        {
            new()
            {
                Type = "message",
                Role = "assistant",
                Content = [new AITextContentPart {
                    Type ="text",
                     Text = AnswerToText(answer.Answer) }]
            }
        };

        if (answer.Sources.ValueKind == JsonValueKind.Array)
        {
            foreach (var source in answer.Sources.EnumerateArray())
            {
                var url = GetString(source, "link");
                if (string.IsNullOrWhiteSpace(url))
                    continue;

                items.Add(new AIOutputItem
                {
                    Type = "source-url",
                    Content = [new AITextContentPart { 
                        Type ="text",
                        Text = GetString(source, "title") ?? url }],
                    Metadata = new Dictionary<string, object?>
                    {
                        ["chatcompletions.source.url"] = url,
                        ["chatcompletions.source.title"] = GetString(source, "title"),
                        ["messages.source.url"] = url,
                        ["messages.source.title"] = GetString(source, "title"),
                        ["llmlayer.source"] = ToDictionary(source)
                    }
                });
            }
        }

        if (answer.Images.ValueKind == JsonValueKind.Array)
        {
            foreach (var image in answer.Images.EnumerateArray())
            {
                var url = GetString(image, "imageUrl");
                if (string.IsNullOrWhiteSpace(url))
                    continue;

                items.Add(new AIOutputItem
                {
                    Type = "message",
                    Role = "assistant",
                    Content =
                    [
                        new AIFileContentPart
                        {
                            MediaType = GuessImageMediaType(url),
                            Type ="file",
                            Filename = GuessImageFilename(url),
                            Data = url,
                            Metadata = new Dictionary<string, object?> { ["llmlayer.image"] = ToDictionary(image) }
                        }
                    ]
                });
            }
        }

        return items;
    }

    private static Dictionary<string, object?> BuildUnifiedMetadata(
        Dictionary<string, object?>? current,
        LLMLayerAnswerResponse answer)
    {
        var metadata = current is null ? [] : new Dictionary<string, object?>(current);
        metadata["llmlayer.response_time"] = answer.ResponseTime;
        metadata["llmlayer.model_cost"] = answer.ModelCost;
        metadata["llmlayer.cost"] = answer.LlmlayerCost;
        if (answer.Sources.ValueKind == JsonValueKind.Array)
            metadata["llmlayer.sources"] = ToPlainObject(answer.Sources);
        if (answer.Images.ValueKind == JsonValueKind.Array)
            metadata["llmlayer.images"] = ToPlainObject(answer.Images);
        return metadata;
    }

    private AIStreamEvent CreateUnifiedEvent(string type, string id, DateTimeOffset timestamp, object data)
        => new()
        {
            ProviderId = GetIdentifier(),
            Event = new AIEventEnvelope { Type = type, Id = id, Timestamp = timestamp, Data = data }
        };

    private Dictionary<string, Dictionary<string, object>> CreateScopedMetadata(Dictionary<string, object?> metadata)
        => new()
        {
            [GetIdentifier()] = metadata
                .Where(entry => entry.Value is not null)
                .ToDictionary(entry => entry.Key, entry => entry.Value!)
        };

    private static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static Dictionary<string, object?> ToDictionary(JsonElement element)
        => JsonSerializer.Deserialize<Dictionary<string, object?>>(element.GetRawText(), JsonWeb) ?? [];

    private static object? ToPlainObject(JsonElement element)
        => JsonSerializer.Deserialize<object>(element.GetRawText(), JsonWeb);

    private static string GuessImageMediaType(string url)
        => Path.GetExtension(Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".avif" => "image/avif",
            _ => "image/png"
        };

    private static string GuessImageFilename(string url)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        var filename = Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(filename) ? $"llmlayer-image-{Guid.NewGuid():N}" : filename;
    }
}
