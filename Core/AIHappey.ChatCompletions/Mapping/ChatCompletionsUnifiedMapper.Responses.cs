using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Unified.Models;

namespace AIHappey.ChatCompletions.Mapping;

public static partial class ChatCompletionsUnifiedMapper
{
    public static AIResponse ToUnifiedResponse(this ChatCompletion response, string providerId)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        var raw = JsonSerializer.SerializeToElement(response, Json);
        return raw.ToUnifiedResponse(providerId);
    }

    public static AIResponse ToUnifiedResponse(this JsonElement response, string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        if (response.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Chat completion response JSON must be an object.", nameof(response));

        var model = ExtractValue<string>(response, "model");
        var outputItems = ParseResponseOutputItems(response).ToList();
        AppendProviderSourceOutputItems(response, outputItems);
        var outputMetadata = BuildUnifiedOutputMetadata(response);

        return new AIResponse
        {
            ProviderId = providerId,
            Model = model,
            Status = InferStatus(response),
            Usage = response.TryGetProperty("usage", out var usage) ? usage.Clone() : null,
            Output = outputItems.Count > 0 || outputMetadata.Count > 0
                ? new AIOutput
                {
                    Items = outputItems.Count > 0 ? outputItems : null,
                    Metadata = outputMetadata.Count > 0 ? outputMetadata : null
                }
                : null,
            Metadata = BuildUnifiedResponseMetadata(response)
        };
    }

    public static ChatCompletion ToChatCompletion(this AIResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var metadata = response.Metadata;

        var id = ExtractMetadataValue<string>(metadata, "chatcompletions.response.id") ?? $"chatcmpl_{Guid.NewGuid():N}";
        var obj = ExtractMetadataValue<string>(metadata, "chatcompletions.response.object") ?? "chat.completion";
        var created = ExtractMetadataValue<long?>(metadata, "chatcompletions.response.created")
                      ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var model = response.Model
                    ?? ExtractMetadataValue<string>(metadata, "chatcompletions.response.model")
                    ?? "unknown";

        var choices = ExtractMetadataEnumerable(metadata, "chatcompletions.response.choices");
        if (choices.Count == 0)
            choices = BuildChoicesFromOutput(response.Output);

        return new ChatCompletion
        {
            Id = id,
            Object = obj,
            Created = created,
            Model = model,
            Choices = choices,
            Usage = response.Usage,
            AdditionalProperties = BuildChatCompletionAdditionalProperties(metadata)
        };
    }

    private static Dictionary<string, object?> BuildUnifiedResponseMetadata(JsonElement response)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["chatcompletions.response.raw"] = response.Clone()
        };

        foreach (var prop in response.EnumerateObject())
            metadata[$"chatcompletions.response.{prop.Name}"] = prop.Value.Clone();

        if (response.TryGetProperty("search_results", out var searchResults))
            metadata["chatcompletions.response.provider.search_results"] = searchResults.Clone();

        if (response.TryGetProperty("citations", out var citations))
            metadata["chatcompletions.response.provider.citations"] = citations.Clone();

        if (response.TryGetProperty("images", out var images))
            metadata["chatcompletions.response.provider.images"] = images.Clone();

        if (response.TryGetProperty("related_questions", out var relatedQuestions))
            metadata["chatcompletions.response.provider.related_questions"] = relatedQuestions.Clone();

        return metadata;
    }

    private static Dictionary<string, object?> BuildUnifiedOutputMetadata(JsonElement response)
    {
        var metadata = new Dictionary<string, object?>();

        if (response.TryGetProperty("search_results", out var searchResults))
            metadata["chatcompletions.output.provider.search_results"] = searchResults.Clone();

        if (response.TryGetProperty("citations", out var citations))
            metadata["chatcompletions.output.provider.citations"] = citations.Clone();

        if (response.TryGetProperty("images", out var images))
            metadata["chatcompletions.output.provider.images"] = images.Clone();

        if (response.TryGetProperty("related_questions", out var relatedQuestions))
            metadata["chatcompletions.output.provider.related_questions"] = relatedQuestions.Clone();

        return metadata;
    }

    private static void AppendProviderSourceOutputItems(JsonElement response, List<AIOutputItem> outputItems)
    {
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in outputItems)
        {
            if (item.Metadata is null)
                continue;

            if (ExtractMetadataValue<string>(item.Metadata, "chatcompletions.source.url") is { Length: > 0 } url)
                seenUrls.Add(url);
        }

        if (response.TryGetProperty("search_results", out var searchResults)
            && searchResults.ValueKind == JsonValueKind.Array)
        {
            foreach (var result in searchResults.EnumerateArray())
            {
                if (!TryBuildSourceOutputItem(result, "search_results", seenUrls, out var outputItem))
                    continue;

                outputItems.Add(outputItem!);
            }
        }

        if (response.TryGetProperty("citations", out var citations)
            && citations.ValueKind == JsonValueKind.Array)
        {
            foreach (var citation in citations.EnumerateArray())
            {
                if (!TryBuildSourceOutputItem(citation, "citations", seenUrls, out var outputItem))
                    continue;

                outputItems.Add(outputItem!);
            }
        }
    }

    private static bool TryBuildSourceOutputItem(
        JsonElement source,
        string sourceType,
        HashSet<string> seenUrls,
        out AIOutputItem? item)
    {
        item = null;

        string? url;
        string? title = null;
        string? date = null;
        string? lastUpdated = null;
        string? snippet = null;
        string? origin = null;

        if (source.ValueKind == JsonValueKind.Object)
        {
            url = ExtractValue<string>(source, "url")
                  ?? ExtractValue<string>(source, "origin_url")
                  ?? ExtractValue<string>(source, "image_url");
            title = ExtractValue<string>(source, "title");
            date = ExtractValue<string>(source, "date");
            lastUpdated = ExtractValue<string>(source, "last_updated");
            snippet = ExtractValue<string>(source, "snippet");
            origin = ExtractValue<string>(source, "source");
        }
        else if (source.ValueKind == JsonValueKind.String)
        {
            url = source.GetString();
        }
        else
        {
            url = null;
        }

        if (string.IsNullOrWhiteSpace(url) || !seenUrls.Add(url))
            return false;

        item = new AIOutputItem
        {
            Type = "source-url",
            Content =
            [
                new AITextContentPart
                {
                    Type = "text",
                    Text = title ?? url,
                    Metadata = new Dictionary<string, object?>
                    {
                        ["chatcompletions.source.url"] = url,
                        ["chatcompletions.source.title"] = title,
                        ["chatcompletions.source.type"] = sourceType,
                        ["chatcompletions.source.raw"] = source.Clone()
                    }
                }
            ],
            Metadata = new Dictionary<string, object?>
            {
                ["chatcompletions.source.url"] = url,
                ["chatcompletions.source.title"] = title,
                ["chatcompletions.source.source_type"] = sourceType,
                ["chatcompletions.source.date"] = date,
                ["chatcompletions.source.last_updated"] = lastUpdated,
                ["chatcompletions.source.snippet"] = snippet,
                ["chatcompletions.source.origin"] = origin,
                ["chatcompletions.source.raw"] = source.Clone()
            }
        };

        return true;
    }

    private static IEnumerable<AIOutputItem> ParseResponseOutputItems(JsonElement response)
    {
        if (!response.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            return Enumerable.Empty<AIOutputItem>();

        var list = new List<AIOutputItem>();
        foreach (var choice in choices.EnumerateArray())
        {
            if (choice.ValueKind != JsonValueKind.Object)
                continue;

            var message = choice.TryGetProperty("message", out var messageEl) && messageEl.ValueKind == JsonValueKind.Object
                ? messageEl
                : default;

            var role = ExtractValue<string>(message, "role") ?? "assistant";
            var content = message.TryGetProperty("content", out var contentEl)
                ? ParseContentParts(contentEl).ToList()
                : [];

            if (message.TryGetProperty("tool_calls", out var toolCallsEl) && toolCallsEl.ValueKind == JsonValueKind.Array)
                content.AddRange(ParseToolCallParts(toolCallsEl));

            var metadata = new Dictionary<string, object?>
            {
                ["chatcompletions.choice.raw"] = choice.Clone(),
                ["chatcompletions.choice.index"] = ExtractValue<int?>(choice, "index"),
                ["chatcompletions.choice.finish_reason"] = ExtractValue<string>(choice, "finish_reason"),
                ["chatcompletions.choice.logprobs"] = choice.TryGetProperty("logprobs", out var logprobs) ? logprobs.Clone() : null
            };

            if (message.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in message.EnumerateObject())
                {
                    if (prop.Name is "role" or "content")
                        continue;

                    metadata[$"chatcompletions.message.{prop.Name}"] = prop.Value.Clone();
                }
            }

            list.Add(new AIOutputItem
            {
                Type = "message",
                Role = role,
                Content = content,
                Metadata = metadata
            });
        }

        return list;
    }

    private static List<object> BuildChoicesFromOutput(AIOutput? output)
    {
        var items = output?.Items ?? [];
        var choices = new List<object>();

        var index = 0;
        foreach (var item in items)
        {
            if (!string.Equals(item.Type, "message", StringComparison.OrdinalIgnoreCase))
                continue;

            var role = item.Role ?? "assistant";
            var toolParts = (item.Content ?? []).OfType<AIToolCallContentPart>().ToList();
            var nonToolParts = (item.Content ?? []).Where(a => a is not AIToolCallContentPart).ToList();
            var toolCalls = BuildOutboundToolCalls(toolParts, item.Metadata);
            var content = string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)
                          && toolCalls is { Count: > 0 }
                          && nonToolParts.Count == 0
                ? SerializeJsonElement((object?)null)
                : ToChatMessageContent(nonToolParts, role);

            var message = new Dictionary<string, object?>
            {
                ["role"] = role,
                ["content"] = content
            };

            if (item.Metadata is not null)
            {
                foreach (var (key, value) in item.Metadata)
                {
                    if (!key.StartsWith("chatcompletions.message.", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var name = key["chatcompletions.message.".Length..];
                    if (name.Length == 0 || string.Equals(name, "tool_calls", StringComparison.OrdinalIgnoreCase))
                        continue;

                    message[name] = value;
                }
            }

            if (toolCalls is { Count: > 0 })
                message["tool_calls"] = toolCalls;

            var choice = new Dictionary<string, object?>
            {
                ["index"] = index++,
                ["message"] = message,
                ["finish_reason"] = item.Metadata is null
                    ? null
                    : ExtractMetadataValue<string>(item.Metadata, "chatcompletions.choice.finish_reason")
            };

            choices.Add(choice);
        }

        return choices;
    }

    private static string? InferStatus(JsonElement response)
    {
        if (!response.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            return "completed";

        var first = choices.EnumerateArray().FirstOrDefault();
        var finishReason = ExtractValue<string>(first, "finish_reason");

        return finishReason is null
            ? "in_progress"
            : finishReason.Equals("content_filter", StringComparison.OrdinalIgnoreCase)
                ? "filtered"
                : "completed";
    }
}
