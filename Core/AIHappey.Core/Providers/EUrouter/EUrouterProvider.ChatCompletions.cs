using System.Net.Http.Headers;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Core.ModelProviders;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHappey.Core.Providers.EUrouter;

public partial class EUrouterProvider : IModelProvider
{
    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(options);

        if (options.Stream == true)
            throw new ArgumentException("Use CompleteChatStreamingAsync for stream=true.", nameof(options));

        var payload = BuildEurouterChatPayload(options, stream: false);
        var json = JsonSerializer.Serialize(payload, EurouterJsonOptions);

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"EUrouter error: {(int)resp.StatusCode} {resp.ReasonPhrase}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var errorEl) && errorEl.ValueKind != JsonValueKind.Null)
            throw new InvalidOperationException($"EUrouter error: {errorEl.GetRawText()}");

        var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        var model = root.TryGetProperty("model", out var mEl) ? mEl.GetString() : null;
        var created = root.TryGetProperty("created", out var cEl) && cEl.TryGetInt64(out var epoch)
            ? epoch
            : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        object? usage = null;
        if (root.TryGetProperty("usage", out var uEl))
            usage = JsonSerializer.Deserialize<object>(uEl.GetRawText(), EurouterJsonOptions);

        object[] choices = [];
        if (root.TryGetProperty("choices", out var chEl) && chEl.ValueKind == JsonValueKind.Array)
            choices = JsonSerializer.Deserialize<object[]>(chEl.GetRawText(), EurouterJsonOptions) ?? [];

        return new ChatCompletion
        {
            Id = id ?? Guid.NewGuid().ToString("n"),
            Created = created,
            Model = model ?? options.Model,
            Choices = choices,
            Usage = usage
        };
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(options);

        var payload = BuildEurouterChatPayload(options, stream: true);
        var json = JsonSerializer.Serialize(payload, EurouterJsonOptions);

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"EUrouter stream error: {(int)resp.StatusCode} {resp.ReasonPhrase}: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                yield break;

            if (line.Length == 0 || line.StartsWith(':'))
                continue;

            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            var data = line["data:".Length..].Trim();
            if (data.Length == 0)
                continue;

            if (data is "[DONE]" or "[done]")
                yield break;

            ChatCompletionUpdate? update;
            try
            {
                update = JsonSerializer.Deserialize<ChatCompletionUpdate>(data, EurouterJsonOptions);
            }
            catch
            {
                JsonNode? root = JsonNode.Parse(data);
                var inner = root?["data"];
                update = inner is null
                    ? null
                    : inner.Deserialize<ChatCompletionUpdate>(EurouterJsonOptions);
            }

            if (update is not null)
                yield return update;
        }
    }

    private object BuildEurouterChatPayload(ChatCompletionOptions options, bool stream)
    {
        var (provider, model) = ResolveProviderAndModel(options.Model);
        var messages = MapEurouterMessages(options.Messages).ToList();
        var tools = options.Tools?.Any() == true ? NormalizeToolsForRequest(options.Tools) : null;

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["stream"] = stream,
            ["temperature"] = options.Temperature,
            ["tools"] = tools,
            ["tool_choice"] = options.ToolChoice,
            ["response_format"] = options.ResponseFormat
        };

        if (!string.IsNullOrWhiteSpace(provider))
        {
            payload["provider"] = new
            {
                order = new[] { provider },
                allow_fallbacks = false
            };
        }

        return payload;
    }

    private (string? Provider, string Model) ResolveProviderAndModel(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            throw new ArgumentException("Model is required.", nameof(modelId));

        var normalized = modelId.Trim();
        var eurouterPrefix = $"{GetIdentifier()}/";

        if (normalized.StartsWith(eurouterPrefix, StringComparison.OrdinalIgnoreCase))
            normalized = normalized[eurouterPrefix.Length..];

        var slashIndex = normalized.IndexOf('/');
        if (slashIndex <= 0 || slashIndex == normalized.Length - 1)
            return (null, normalized);

        var provider = normalized[..slashIndex].Trim();
        var model = normalized[(slashIndex + 1)..].Trim();

        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model))
            return (null, normalized);

        return (provider, model);
    }

    private static IEnumerable<object> MapEurouterMessages(IEnumerable<ChatMessage> messages)
    {
        foreach (var msg in messages)
        {
            var role = NormalizeRole(msg.Role);

            // EUrouter validates role=tool requires tool_call_id.
            if (role == "tool" && string.IsNullOrWhiteSpace(msg.ToolCallId))
                role = "assistant";

            var hasToolCalls = msg.ToolCalls?.Any() == true;
            var content = NormalizeChatMessageContent(msg.Content, hasToolCalls);

            if (role == "tool")
            {
                yield return new
                {
                    role,
                    content,
                    tool_call_id = msg.ToolCallId
                };
                continue;
            }

            yield return new
            {
                role,
                content,
                tool_calls = hasToolCalls ? NormalizeToolCallsForRequest(msg.ToolCalls!) : null
            };
        }
    }

    private static object? NormalizeChatMessageContent(JsonElement content, bool hasToolCalls)
    {
        return content.ValueKind switch
        {
            JsonValueKind.String => content.GetString() ?? string.Empty,
            JsonValueKind.Array => JsonSerializer.Deserialize<object>(content.GetRawText(), EurouterJsonOptions),
            JsonValueKind.Null or JsonValueKind.Undefined => hasToolCalls ? string.Empty : string.Empty,
            _ => ChatMessageContentExtensions.ToText(content) ?? content.GetRawText()
        };
    }

    private static string NormalizeRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
            return "user";

        return role.Trim().ToLowerInvariant() switch
        {
            "developer" => "system",
            "system" => "system",
            "user" => "user",
            "assistant" => "assistant",
            "tool" => "tool",
            _ => "user"
        };
    }

    private static IEnumerable<object> NormalizeToolsForRequest(IEnumerable<object> tools)
    {
        foreach (var tool in tools)
        {
            var el = JsonSerializer.SerializeToElement(tool, EurouterJsonOptions);

            var name = el.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(name))
                continue;

            var description = el.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString()
                : null;

            object? parameters = null;
            if (el.TryGetProperty("inputSchema", out var schema) && schema.ValueKind == JsonValueKind.Object)
                parameters = JsonSerializer.Deserialize<object>(schema.GetRawText(), EurouterJsonOptions);

            yield return new
            {
                type = "function",
                function = new
                {
                    name,
                    description,
                    parameters
                }
            };
        }
    }

    private static object NormalizeToolCallsForRequest(IEnumerable<object> toolCalls)
    {
        var list = new List<object>();

        foreach (var tc in toolCalls)
        {
            var el = JsonSerializer.SerializeToElement(tc, EurouterJsonOptions);

            var id = el.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString()
                : null;

            var type = el.TryGetProperty("type", out var tEl) && tEl.ValueKind == JsonValueKind.String
                ? tEl.GetString()
                : "function";

            if (!el.TryGetProperty("function", out var fnEl) || fnEl.ValueKind != JsonValueKind.Object)
                continue;

            var name = fnEl.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String
                ? nEl.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(name))
                continue;

            string argsJson = "{}";
            if (fnEl.TryGetProperty("arguments", out var aEl))
            {
                argsJson = aEl.ValueKind switch
                {
                    JsonValueKind.String => aEl.GetString() ?? "{}",
                    JsonValueKind.Object or JsonValueKind.Array => aEl.GetRawText(),
                    JsonValueKind.Null or JsonValueKind.Undefined => "{}",
                    _ => aEl.GetRawText()
                };
            }

            list.Add(new
            {
                id,
                type,
                function = new
                {
                    name,
                    arguments = argsJson
                }
            });
        }

        return list;
    }
}
