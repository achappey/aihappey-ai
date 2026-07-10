using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.WAYSCloud;

public partial class WAYSCloudProvider
{
    private static readonly JsonSerializerOptions WaysCloudJson = JsonSerializerOptions.Web;

    private async Task<AIResponse> ExecuteChatbotUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var botId = ResolveChatbotBotId(request.Model);
        var message = ExtractChatbotMessage(request);
        var sessionId = ExtractChatbotSessionId(request.Metadata, request.Input?.Metadata);
        var created = DateTimeOffset.UtcNow;

        var payload = new Dictionary<string, object?>
        {
            ["bot_id"] = botId,
            ["message"] = message
        };

        if (!string.IsNullOrWhiteSpace(sessionId))
            payload["session_id"] = sessionId;

        var requestBody = JsonSerializer.Serialize(payload, WaysCloudJson);

        ApplyAuthHeader();

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/chatbot/api/chat")
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"WAYSCloud chatbot failed ({(int)resp.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        return CreateChatbotUnifiedResponse(request, botId, doc.RootElement.Clone(), created, requestBody);
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamChatbotUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var providerId = GetIdentifier();
        var botId = ResolveChatbotBotId(request.Model);
        var message = ExtractChatbotMessage(request);
        var sessionId = ExtractChatbotSessionId(request.Metadata, request.Input?.Metadata);
        var eventId = request.Id ?? $"wayscloud-chatbot-{Guid.NewGuid():N}";
        var model = (ChatbotModelPrefix + botId).ToModelId(providerId);
        var metadata = CreateChatbotMetadata(botId, sessionId, null, null, null, null);
        var timestamp = DateTimeOffset.UtcNow;
        var textStarted = false;
        var lastSessionId = sessionId;
        JsonElement? lastCitations = null;

        var payload = new Dictionary<string, object?>
        {
            ["bot_id"] = botId,
            ["message"] = message
        };

        if (!string.IsNullOrWhiteSpace(sessionId))
            payload["session_id"] = sessionId;

        ApplyAuthHeader();

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/chatbot/api/chat/stream")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, WaysCloudJson), Encoding.UTF8, "application/json")
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var rawError = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"WAYSCloud chatbot stream failed ({(int)resp.StatusCode}): {rawError}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            var data = line[5..].Trim();
            if (string.IsNullOrWhiteSpace(data))
                continue;

            using var eventDoc = JsonDocument.Parse(data);
            var root = eventDoc.RootElement;
            var type = TryGetWaysCloudString(root, "type");

            if (string.Equals(type, "meta", StringComparison.OrdinalIgnoreCase))
            {
                var meta = root.TryGetProperty("data", out var metaEl) && metaEl.ValueKind == JsonValueKind.Object
                    ? metaEl
                    : root;
                lastSessionId = TryGetWaysCloudString(meta, "session_id") ?? lastSessionId;
                lastCitations = meta.TryGetProperty("citations", out var citationsEl) ? citationsEl.Clone() : lastCitations;
                metadata = CreateChatbotMetadata(botId, lastSessionId, null, lastCitations, null, root.Clone());
                continue;
            }

            if (string.Equals(type, "token", StringComparison.OrdinalIgnoreCase))
            {
                if (!textStarted)
                {
                    textStarted = true;
                    yield return CreateChatbotStreamEvent(providerId, eventId, "text-start", new AITextStartEventData(), timestamp, metadata);
                }

                yield return CreateChatbotStreamEvent(
                    providerId,
                    eventId,
                    "text-delta",
                    new AITextDeltaEventData { Delta = TryGetWaysCloudString(root, "content") ?? string.Empty },
                    DateTimeOffset.UtcNow,
                    metadata);
                continue;
            }

            if (string.Equals(type, "error", StringComparison.OrdinalIgnoreCase))
            {
                yield return CreateChatbotStreamEvent(
                    providerId,
                    eventId,
                    "error",
                    new AIErrorEventData { ErrorText = TryGetWaysCloudString(root, "content") ?? "WAYSCloud chatbot stream error." },
                    DateTimeOffset.UtcNow,
                    CreateChatbotMetadata(botId, lastSessionId, null, lastCitations, null, root.Clone()));
                yield break;
            }

            if (string.Equals(type, "done", StringComparison.OrdinalIgnoreCase))
                break;
        }

        if (textStarted)
            yield return CreateChatbotStreamEvent(providerId, eventId, "text-end", new AITextEndEventData(), DateTimeOffset.UtcNow, metadata);

        yield return CreateChatbotStreamEvent(
            providerId,
            eventId,
            "finish",
            new AIFinishEventData
            {
                FinishReason = "stop",
                Model = model,
                CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                MessageMetadata = AIFinishMessageMetadata.Create(model, DateTimeOffset.UtcNow)
            },
            DateTimeOffset.UtcNow,
            metadata);
    }

    private AIResponse CreateChatbotUnifiedResponse(
        AIRequest request,
        string botId,
        JsonElement root,
        DateTimeOffset created,
        string requestBody)
    {
        var responseText = TryGetWaysCloudString(root, "response") ?? string.Empty;
        var sessionId = TryGetWaysCloudString(root, "session_id");
        var messageId = TryGetWaysCloudString(root, "message_id");
        var inputTokens = TryGetWaysCloudInt(root, "tokens_input");
        var outputTokens = TryGetWaysCloudInt(root, "tokens_output");
        var responseTimeMs = TryGetWaysCloudInt(root, "response_time_ms");
        var citations = root.TryGetProperty("citations", out var citationsEl) ? citationsEl.Clone() : (JsonElement?)null;
        var metadata = CreateChatbotMetadata(botId, sessionId, messageId, citations, responseTimeMs, root);
        metadata["wayscloud.chatbot.request.body"] = requestBody;
        var model = (ChatbotModelPrefix + botId).ToModelId(GetIdentifier());

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = model,
            Status = "completed",
            Usage = new Dictionary<string, object?>
            {
                ["input_tokens"] = inputTokens,
                ["output_tokens"] = outputTokens,
                ["total_tokens"] = (inputTokens ?? 0) + (outputTokens ?? 0)
            },
            Output = new AIOutput
            {
                Items = CreateChatbotOutputItems(responseText, citations),
                Metadata = metadata
            },
            Metadata = metadata
        };
    }

    private static List<AIOutputItem> CreateChatbotOutputItems(string responseText, JsonElement? citations)
    {
        var items = new List<AIOutputItem>
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
                        Text = responseText
                    }
                ]
            }
        };

        if (citations is { ValueKind: JsonValueKind.Array } citationsEl)
        {
            foreach (var citation in citationsEl.EnumerateArray())
            {
                var url = TryGetWaysCloudString(citation, "url");
                if (string.IsNullOrWhiteSpace(url))
                    continue;

                var title = TryGetWaysCloudString(citation, "title") ?? url;
                items.Add(new AIOutputItem
                {
                    Type = "source-url",
                    Content =
                    [
                        new AITextContentPart
                        {
                            Type = "text",
                            Text = title,
                            Metadata = new Dictionary<string, object?>
                            {
                                ["wayscloud.citation.url"] = url,
                                ["wayscloud.citation.raw"] = citation.Clone()
                            }
                        }
                    ],
                    Metadata = new Dictionary<string, object?>
                    {
                        ["wayscloud.citation.url"] = url,
                        ["wayscloud.citation.title"] = title,
                        ["wayscloud.citation.snippet"] = TryGetWaysCloudString(citation, "snippet"),
                        ["wayscloud.citation.raw"] = citation.Clone()
                    }
                });
            }
        }

        return items;
    }

    private static AIStreamEvent CreateChatbotStreamEvent(
        string providerId,
        string eventId,
        string type,
        object data,
        DateTimeOffset timestamp,
        Dictionary<string, object?>? metadata)
        => new()
        {
            ProviderId = providerId,
            Event = new AIEventEnvelope
            {
                Type = type,
                Id = eventId,
                Timestamp = timestamp,
                Data = data
            },
            Metadata = metadata
        };

    private static Dictionary<string, object?> CreateChatbotMetadata(
        string botId,
        string? sessionId,
        string? messageId,
        JsonElement? citations,
        int? responseTimeMs,
        JsonElement? raw)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["wayscloud.chatbot.bot_id"] = botId,
            ["wayscloud.chatbot.session_id"] = sessionId,
            ["wayscloud.chatbot.message_id"] = messageId,
            ["wayscloud.chatbot.response_time_ms"] = responseTimeMs
        };

        if (citations is not null)
            metadata["wayscloud.chatbot.citations"] = citations.Value;

        if (raw is not null)
            metadata["wayscloud.chatbot.raw"] = raw.Value;

        return metadata;
    }

    private static string ExtractChatbotMessage(AIRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Input?.Text))
            return request.Input.Text.Trim();

        var latestUser = request.Input?.Items?
            .Where(static item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))
            .Select(static item => ExtractChatbotText(item.Content))
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .LastOrDefault();

        if (!string.IsNullOrWhiteSpace(latestUser))
            return latestUser.Trim();

        var fallback = request.Input?.Items?
            .Select(static item => ExtractChatbotText(item.Content))
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .LastOrDefault();

        if (!string.IsNullOrWhiteSpace(fallback))
            return fallback.Trim();

        throw new ArgumentException("WAYSCloud chatbot requires at least one text input message.", nameof(request));
    }

    private static string ExtractChatbotText(List<AIContentPart>? parts)
        => parts is null
            ? string.Empty
            : string.Join("\n\n", parts.OfType<AITextContentPart>().Select(static part => part.Text).Where(static text => !string.IsNullOrWhiteSpace(text)));

    private static string? ExtractChatbotSessionId(params Dictionary<string, object?>?[] metadataSources)
    {
        foreach (var metadata in metadataSources)
        {
            if (metadata is null)
                continue;

            foreach (var key in new[] { "wayscloud.chatbot.session_id", "wayscloud.session_id", "session_id", "chatbot.session_id" })
            {
                if (!metadata.TryGetValue(key, out var value) || value is null)
                    continue;

                if (value is string str && !string.IsNullOrWhiteSpace(str))
                    return str;

                if (value is JsonElement json && json.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(json.GetString()))
                    return json.GetString();
            }
        }

        return null;
    }

    private string ResolveChatbotBotId(string? model)
    {
        var localModel = NormalizeWaysCloudLocalModelId(model);
        if (!localModel.StartsWith(ChatbotModelPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"WAYSCloud chatbot model must start with '{ChatbotModelPrefix}'.", nameof(model));

        var botId = localModel[ChatbotModelPrefix.Length..];
        if (string.IsNullOrWhiteSpace(botId))
            throw new ArgumentException("WAYSCloud chatbot model must include a bot id or slug.", nameof(model));

        return botId;
    }

    private bool IsChatbotModel(string? model)
        => NormalizeWaysCloudLocalModelId(model).StartsWith(ChatbotModelPrefix, StringComparison.OrdinalIgnoreCase);

    private string NormalizeWaysCloudLocalModelId(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return string.Empty;

        var providerPrefix = GetIdentifier() + "/";
        return model.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase)
            ? model[providerPrefix.Length..]
            : model;
    }

    private static string? TryGetWaysCloudString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? TryGetWaysCloudInt(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var intValue)
            ? intValue
            : null;

}
