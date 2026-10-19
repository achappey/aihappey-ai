using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.Coze;

public sealed partial class CozeProvider
{
    private sealed record BotRoute(string WorkspaceId, string BotId);

    private sealed record CozeEvent(
        string Name,
        JsonElement Data,
        JsonElement Raw,
        string? Content = null,
        string? MessageId = null,
        string? ChatId = null,
        long? CreatedAt = null,
        JsonElement? Usage = null);

    public async Task<ChatCompletion> CompleteChatAsync(
        ChatCompletionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var text = new StringBuilder();
        var id = $"chatcmpl-coze-{Guid.NewGuid():N}";
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        object? usage = null;
        var model = options.Model;

        await foreach (var update in CompleteChatStreamingAsync(options, cancellationToken))
        {
            id = update.Id;
            created = update.Created;
            if (update.Usage is not null)
                usage = update.Usage;

            foreach (var choice in update.Choices.OfType<Dictionary<string, object?>>())
            {
                if (choice.GetValueOrDefault("delta") is not Dictionary<string, object?> delta ||
                    delta.GetValueOrDefault("content") is not string content)
                    continue;

                text.Append(content);
            }
        }

        return new ChatCompletion
        {
            Id = id,
            Model = model,
            Created = created,
            Choices =
            [
                new Dictionary<string, object?>
                {
                    ["index"] = 0,
                    ["message"] = new { role = "assistant", content = text.ToString() },
                    ["finish_reason"] = "stop"
                }
            ],
            Usage = usage
        };
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("No Coze API key.");

        var route = ParseRoute(options.Model);
        var request = CreateChatRequest(options, route, apiKey);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

        foreach (var (name, value) in (options.Headers ?? [])
                     .Select(header => new KeyValuePair<string, string?>(header.Key, header.Value))
                     .GetProviderPassthroughHeaders(GetIdentifier()))
            request.Headers.TryAddWithoutValidation(name, value);

        using (request)
        using (var response = await _client.SendAsync(
                   request,
                   HttpCompletionOption.ResponseHeadersRead,
                   cancellationToken))
        {
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException(
                    $"Coze chat failed with status {(int)response.StatusCode}: {error}",
                    null,
                    response.StatusCode);
            }

            var id = $"chatcmpl-coze-{Guid.NewGuid():N}";
            var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var completed = false;
            var streamedMessages = new HashSet<string>(StringComparer.Ordinal);

            await foreach (var item in ReadEventsAsync(response, cancellationToken))
            {
                if (item.ChatId is not null)
                    id = item.ChatId;
                if (item.CreatedAt is not null)
                    created = item.CreatedAt.Value;

                if (item.Name is "error" or "conversation.chat.failed")
                {
                    var error = GetErrorMessage(item.Data);
                    throw new InvalidOperationException($"Coze chat failed: {error}");
                }

                if (item.Name == "conversation.chat.requires_action")
                    throw new NotSupportedException(
                        "Coze chat requires provider-native tool output submission, which is not supported.");

                if (item.Name == "conversation.message.delta" &&
                    IsAssistantText(item.Data) &&
                    item.Content is { Length: > 0 } delta)
                {
                    if (item.MessageId is not null)
                        streamedMessages.Add(item.MessageId);
                    yield return CreateUpdate(id, options.Model, created, delta, item.Raw);
                }
                else if (item.Name == "conversation.message.completed" &&
                         IsAssistantText(item.Data) &&
                         item.Content is { Length: > 0 } finalContent &&
                         (item.MessageId is null || streamedMessages.Add(item.MessageId)))
                {
                    yield return CreateUpdate(id, options.Model, created, finalContent, item.Raw);
                }
                else if (item.Name == "conversation.chat.completed")
                {
                    completed = true;
                    if (item.Usage is { } nativeUsage && TryNormalizeUsage(nativeUsage, out var normalizedUsage))
                    {
                        yield return new ChatCompletionUpdate
                        {
                            Id = id,
                            Model = options.Model,
                            Created = created,
                            Usage = normalizedUsage,
                            Choices = [],
                            AdditionalProperties = NativeEvent(item.Raw)
                        };
                    }
                }
                else if (item.Name == "done")
                {
                    break;
                }
            }

            if (!completed)
                throw new IOException("Coze SSE stream ended before the chat completed.");

            yield return new ChatCompletionUpdate
            {
                Id = id,
                Model = options.Model,
                Created = created,
                Choices =
                [
                    new Dictionary<string, object?>
                    {
                        ["index"] = 0,
                        ["delta"] = new Dictionary<string, object?>(),
                        ["finish_reason"] = "stop"
                    }
                ]
            };
        }
    }

    private static ChatCompletionUpdate CreateUpdate(
        string id,
        string model,
        long created,
        string content,
        JsonElement nativeEvent)
        => new()
        {
            Id = id,
            Model = model,
            Created = created,
            Choices =
            [
                new Dictionary<string, object?>
                {
                    ["index"] = 0,
                    ["delta"] = new Dictionary<string, object?> { ["content"] = content },
                    ["finish_reason"] = null
                }
            ],
            AdditionalProperties = NativeEvent(nativeEvent)
        };

    private static Dictionary<string, JsonElement> NativeEvent(JsonElement nativeEvent)
        => new() { ["coze_event"] = nativeEvent.Clone() };

    private static BotRoute ParseRoute(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var prefix = "coze/";
        if (!model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Coze model IDs must be coze/{workspace_id}/{bot_id}.", nameof(model));

        var segments = model[prefix.Length..].Split('/', 2);
        if (segments.Length != 2 ||
            string.IsNullOrWhiteSpace(segments[0]) ||
            string.IsNullOrWhiteSpace(segments[1]))
            throw new ArgumentException("Coze model IDs must be coze/{workspace_id}/{bot_id}.", nameof(model));

        return new BotRoute(segments[0], segments[1]);
    }

    private static HttpRequestMessage CreateChatRequest(
        ChatCompletionOptions options,
        BotRoute route,
        string apiKey)
    {
        var body = new JsonObject();
        string? conversationId = null;
        string? userId = null;

        if (options.AdditionalProperties is not null)
        {
            foreach (var (name, value) in options.AdditionalProperties)
            {
                if (name.Equals("conversation_id", StringComparison.OrdinalIgnoreCase))
                {
                    conversationId = GetNativeString(value, name);
                    continue;
                }

                if (name.Equals("user_id", StringComparison.OrdinalIgnoreCase))
                {
                    userId = GetNativeString(value, name);
                    continue;
                }

                if (name.Equals("bot_id", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("additional_messages", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("stream", StringComparison.OrdinalIgnoreCase))
                    continue;

                body[name] = JsonNode.Parse(value.GetRawText());
            }
        }

        body["bot_id"] = route.BotId;
        body["user_id"] = string.IsNullOrWhiteSpace(userId)
            ? Guid.NewGuid().ToString("N")
            : userId;
        body["additional_messages"] = BuildMessages(options.Messages);
        body["stream"] = true;

        var path = "v3/chat";
        if (!string.IsNullOrWhiteSpace(conversationId))
            path += $"?conversation_id={Uri.EscapeDataString(conversationId)}";

        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }

    private static JsonArray BuildMessages(IEnumerable<ChatMessage> messages)
    {
        var result = new JsonArray();
        foreach (var message in messages)
        {
            var role = message.Role?.ToLowerInvariant();
            if (role is not ("user" or "assistant"))
                throw new NotSupportedException(
                    "Coze supports only user and assistant messages; configure system instructions on the Coze bot.");

            var content = GetTextContent(message.Content);
            result.Add(new JsonObject
            {
                ["role"] = role,
                ["content"] = content,
                ["content_type"] = "text",
                ["type"] = role == "user" ? "question" : "answer"
            });
        }

        return result;
    }

    private static string? GetTextContent(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString();

        if (content.ValueKind == JsonValueKind.Null)
            return null;

        if (content.ValueKind == JsonValueKind.Array)
        {
            var builder = new StringBuilder();
            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object ||
                    !part.TryGetProperty("type", out var type) ||
                    type.GetString() != "text")
                    throw new NotSupportedException("Coze chat currently accepts text-only messages.");

                if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    builder.Append(text.GetString());
            }

            return builder.ToString();
        }

        throw new NotSupportedException("Coze chat currently accepts text-only messages.");
    }

    private static string? GetNativeString(JsonElement value, string propertyName)
    {
        if (value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind == JsonValueKind.String)
            return value.GetString();

        throw new ArgumentException($"Coze native option '{propertyName}' must be a string.");
    }

    private static bool IsAssistantText(JsonElement data)
        => GetString(data, "role") == "assistant" &&
           GetString(data, "type") == "answer" &&
           GetString(data, "content_type") == "text";

    private static string? GetErrorMessage(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Object)
        {
            if (data.TryGetProperty("msg", out var msg) && msg.ValueKind == JsonValueKind.String)
                return msg.GetString();
            if (data.TryGetProperty("last_error", out var lastError) &&
                lastError.ValueKind == JsonValueKind.Object &&
                lastError.TryGetProperty("msg", out msg) &&
                msg.ValueKind == JsonValueKind.String)
                return msg.GetString();
        }

        return data.ValueKind == JsonValueKind.String ? data.GetString() : "the provider returned an error event.";
    }

    private static string? GetString(JsonElement item, string propertyName)
        => item.ValueKind == JsonValueKind.Object &&
           item.TryGetProperty(propertyName, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryNormalizeUsage(JsonElement usage, out Dictionary<string, int> normalized)
    {
        normalized = [];
        var input = GetInt32(usage, "input_count");
        var output = GetInt32(usage, "output_count");
        var total = GetInt32(usage, "token_count");
        if (input is null && output is null && total is null)
            return false;

        normalized["prompt_tokens"] = input ?? 0;
        normalized["completion_tokens"] = output ?? 0;
        normalized["total_tokens"] = total ?? normalized["prompt_tokens"] + normalized["completion_tokens"];
        return true;
    }

    private static int? GetInt32(JsonElement item, string propertyName)
        => item.ValueKind == JsonValueKind.Object &&
           item.TryGetProperty(propertyName, out var value) &&
           value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt32(out var result)
            ? result
            : null;

    private async IAsyncEnumerable<CozeEvent> ReadEventsAsync(
        HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        string? eventName = null;
        var data = new StringBuilder();

        async IAsyncEnumerable<CozeEvent> Dispatch()
        {
            if (string.IsNullOrWhiteSpace(eventName))
                yield break;

            var name = eventName;
            var payload = data.ToString();
            eventName = null;
            data.Clear();

            if (name == "done")
            {
                yield return new CozeEvent(name, default, default);
                yield break;
            }

            JsonDocument? document = null;
            try
            {
                document = JsonDocument.Parse(payload);
            }
            catch (JsonException)
            {
                // An API error can be plain text rather than a JSON object.
            }

            if (document is null)
            {
                if (name == "error")
                {
                    using var rawError = JsonDocument.Parse(JsonSerializer.Serialize(payload));
                    yield return new CozeEvent(name, rawError.RootElement.Clone(), rawError.RootElement.Clone());
                    yield break;
                }

                throw new InvalidOperationException($"Coze returned invalid data for SSE event '{name}'.");
            }

            using (document)
            {
                var raw = document.RootElement.Clone();
                var content = GetString(raw, "content");
                var isChatEvent = name.StartsWith("conversation.chat.", StringComparison.Ordinal);
                var messageId = name.StartsWith("conversation.message.", StringComparison.Ordinal)
                    ? GetString(raw, "id")
                    : null;
                var chatId = isChatEvent ? GetString(raw, "id") : null;
                long? createdAt = null;
                JsonElement? usage = null;

                if (isChatEvent)
                {
                    if (raw.TryGetProperty("created_at", out var created) &&
                        created.ValueKind == JsonValueKind.Number &&
                        created.TryGetInt64(out var time))
                        createdAt = time;

                    if (raw.TryGetProperty("usage", out var nativeUsage) &&
                        nativeUsage.ValueKind == JsonValueKind.Object)
                        usage = nativeUsage.Clone();
                }

                yield return new CozeEvent(
                    name,
                    raw,
                    raw,
                    content,
                    messageId,
                    chatId,
                    createdAt,
                    usage);
            }
        }

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                await foreach (var item in Dispatch())
                    yield return item;
                continue;
            }

            if (line.StartsWith(':'))
                continue;

            var separator = line.IndexOf(':');
            var field = separator < 0 ? line : line[..separator];
            var value = separator < 0 ? string.Empty : line[(separator + 1)..];
            if (value.StartsWith(' '))
                value = value[1..];

            if (field == "event")
                eventName = value;
            else if (field == "data")
            {
                if (data.Length > 0)
                    data.Append('\n');
                data.Append(value);
            }
        }

        await foreach (var item in Dispatch())
            yield return item;
    }
}
