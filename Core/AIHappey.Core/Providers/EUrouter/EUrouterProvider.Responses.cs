using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.EUrouter;

public partial class EUrouterProvider
{
    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(options);

        var payload = BuildEurouterResponsesPayload(options, stream: false);
        var json = JsonSerializer.Serialize(payload, EurouterJsonOptions);

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/responses")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"EUrouter responses error: {(int)resp.StatusCode} {resp.ReasonPhrase}: {ExtractErrorMessage(raw)}");

        var parsed = JsonSerializer.Deserialize<ResponseResult>(raw, ResponseJson.Default)
                     ?? throw new InvalidOperationException("EUrouter responses payload could not be deserialized.");

        if (string.IsNullOrWhiteSpace(parsed.Model))
            parsed.Model = options.Model ?? string.Empty;

        return parsed;
    }

    public async IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(
        ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(options);

        var payload = BuildEurouterResponsesPayload(options, stream: true);
        var json = JsonSerializer.Serialize(payload, EurouterJsonOptions);

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/responses")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"EUrouter responses stream error: {(int)resp.StatusCode} {resp.ReasonPhrase}: {ExtractErrorMessage(err)}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        string? eventType = null;
        var dataBuilder = new StringBuilder();

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                break;

            if (line.Length == 0)
            {
                var data = dataBuilder.ToString().Trim();
                dataBuilder.Clear();

                if (string.IsNullOrWhiteSpace(data))
                {
                    eventType = null;
                    continue;
                }

                if (IsDoneSignal(data))
                    yield break;

                if (TryParseStreamPart(data, eventType, out var part) && part is not null)
                    yield return part;

                eventType = null;
                continue;
            }

            if (line.StartsWith(':'))
                continue;

            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                eventType = line["event:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                dataBuilder.AppendLine(line["data:".Length..].Trim());
            }
        }

        var tailData = dataBuilder.ToString().Trim();
        if (tailData.Length == 0 || IsDoneSignal(tailData))
            yield break;

        if (TryParseStreamPart(tailData, eventType, out var tailPart) && tailPart is not null)
            yield return tailPart;
    }

    private object BuildEurouterResponsesPayload(ResponseRequest options, bool stream)
    {
        var extensionMetadata = options.Metadata is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(options.Metadata, StringComparer.OrdinalIgnoreCase);

        var (providerFromModel, model) = ResolveProviderAndModel(options.Model ?? string.Empty);

        var input = options.Input?.IsText == true
            ? options.Input.Text ?? string.Empty
            : options.Input?.IsItems == true
                ? CloneAsUntyped(options.Input.Items, ResponseJson.Default)
                : null;

        var payload = new Dictionary<string, object?>
        {
            ["model"] = string.IsNullOrWhiteSpace(model) ? options.Model : model,
            ["input"] = input,
            ["instructions"] = options.Instructions,
            ["stream"] = stream,
            ["temperature"] = options.Temperature,
            ["top_p"] = options.TopP,
            ["truncation"] = MapTruncation(options.Truncation),
            ["max_output_tokens"] = options.MaxOutputTokens,
            ["top_logprobs"] = options.TopLogprobs,
            ["parallel_tool_calls"] = options.ParallelToolCalls,
            ["tool_choice"] = CloneAsUntyped(options.ToolChoice),
            ["tools"] = NormalizeResponseTools(options.Tools),
            ["text"] = CloneAsUntyped(options.Text),
            ["store"] = options.Store,
            ["include"] = options.Include,
            ["service_tier"] = options.ServiceTier
        };

        // EUrouter/OpenAI-compatible extensions sourced from metadata.
        payload["previous_response_id"] = TakeString(extensionMetadata, "previous_response_id", "eurouter_previous_response_id");
        payload["user"] = TakeString(extensionMetadata, "user", "eurouter_user");
        payload["reasoning"] = TakeObject(extensionMetadata, "reasoning", "eurouter_reasoning");
        payload["stream_options"] = TakeObject(extensionMetadata, "stream_options", "eurouter_stream_options");
        payload["max_tool_calls"] = TakeInt(extensionMetadata, "max_tool_calls", "eurouter_max_tool_calls");
        payload["background"] = TakeBool(extensionMetadata, "background", "eurouter_background");

        payload["rule_id"] = TakeString(extensionMetadata, "rule_id", "eurouter_rule_id");
        payload["rule_name"] = TakeString(extensionMetadata, "rule_name", "eurouter_rule_name");
        payload["models"] = TakeStringArray(extensionMetadata, "models", "eurouter_models");

        var providerExtension = TakeObject(extensionMetadata, "provider", "eurouter_provider");
        if (providerExtension is not null)
        {
            payload["provider"] = providerExtension;
        }
        else if (!string.IsNullOrWhiteSpace(providerFromModel))
        {
            payload["provider"] = new
            {
                order = new[] { providerFromModel },
                allow_fallbacks = false
            };
        }

        payload["metadata"] = extensionMetadata.Count > 0 ? extensionMetadata : options.Metadata;

        return payload;
    }

    private static IEnumerable<object>? NormalizeResponseTools(IEnumerable<ResponseToolDefinition>? tools)
    {
        if (tools is null)
            return null;

        var normalized = new List<object>();

        foreach (var tool in tools)
        {
            if (string.IsNullOrWhiteSpace(tool.Type))
                continue;

            var node = new Dictionary<string, object?>
            {
                ["type"] = tool.Type
            };

            if (tool.Extra is not null)
            {
                foreach (var (key, value) in tool.Extra)
                {
                    if (string.IsNullOrWhiteSpace(key))
                        continue;

                    node[key] = JsonElementToObject(value);
                }
            }

            normalized.Add(node);
        }

        return normalized.Count == 0 ? null : normalized;
    }

    private static bool TryParseStreamPart(string data, string? eventType, out ResponseStreamPart? part)
    {
        part = null;

        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(data);
            var root = doc.RootElement;

            if (TryDeserializeStreamPart(root.GetRawText(), out part))
                return true;

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("data", out var dataNode) &&
                dataNode.ValueKind == JsonValueKind.Object)
            {
                if (TryDeserializeStreamPart(dataNode.GetRawText(), out part))
                    return true;

                if (!string.IsNullOrWhiteSpace(eventType) &&
                    TryDeserializeStreamPart(InjectType(dataNode.GetRawText(), eventType), out part))
                    return true;
            }

            if (!string.IsNullOrWhiteSpace(eventType) &&
                root.ValueKind == JsonValueKind.Object &&
                TryDeserializeStreamPart(InjectType(root.GetRawText(), eventType), out part))
                return true;

            if (TryBuildErrorPart(root, out part))
                return true;
        }
        catch
        {
            // Intentionally ignore malformed chunks and continue streaming.
        }
        finally
        {
            doc?.Dispose();
        }

        return false;
    }

    private static bool TryDeserializeStreamPart(string json, out ResponseStreamPart? part)
    {
        part = null;

        try
        {
            part = JsonSerializer.Deserialize<ResponseStreamPart>(json, ResponseJson.Default);
            return part is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryBuildErrorPart(JsonElement root, out ResponseStreamPart? part)
    {
        part = null;

        if (root.ValueKind != JsonValueKind.Object)
            return false;

        JsonElement errorNode;

        if (root.TryGetProperty("error", out var nestedError) && nestedError.ValueKind == JsonValueKind.Object)
        {
            errorNode = nestedError;
        }
        else
        {
            errorNode = root;
        }

        var message = errorNode.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String
            ? msg.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(message))
            return false;

        var code = errorNode.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.String
            ? codeEl.GetString()
            : "eurouter_error";

        var param = errorNode.TryGetProperty("param", out var paramEl) && paramEl.ValueKind == JsonValueKind.String
            ? paramEl.GetString()
            : "response";

        var seq = root.TryGetProperty("sequence_number", out var seqEl) && seqEl.TryGetInt32(out var parsedSeq)
            ? parsedSeq
            : 0;

        part = new ResponseError
        {
            SequenceNumber = seq,
            Code = code ?? "eurouter_error",
            Message = message,
            Param = param ?? "response"
        };

        return true;
    }

    private static string ExtractErrorMessage(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("error", out var errorEl))
            {
                if (errorEl.ValueKind == JsonValueKind.String)
                    return errorEl.GetString() ?? raw;

                if (errorEl.ValueKind == JsonValueKind.Object &&
                    errorEl.TryGetProperty("message", out var msgEl) &&
                    msgEl.ValueKind == JsonValueKind.String)
                    return msgEl.GetString() ?? raw;

                return errorEl.GetRawText();
            }
        }
        catch
        {
            // Fall back to raw payload.
        }

        return raw;
    }

    private static object? CloneAsUntyped(object? value, JsonSerializerOptions? sourceOptions = null)
    {
        if (value is null)
            return null;

        var json = JsonSerializer.Serialize(value, sourceOptions ?? EurouterJsonOptions);
        return JsonSerializer.Deserialize<object>(json, EurouterJsonOptions);
    }

    private static object? JsonElementToObject(JsonElement element)
        => JsonSerializer.Deserialize<object>(element.GetRawText(), EurouterJsonOptions);

    private static string? MapTruncation(TruncationStrategy? truncation)
        => truncation switch
        {
            TruncationStrategy.Auto => "auto",
            TruncationStrategy.Disabled => "disabled",
            _ => null
        };

    private static bool IsDoneSignal(string data)
        => data.Equals("[DONE]", StringComparison.OrdinalIgnoreCase)
           || data.Equals("[done]", StringComparison.OrdinalIgnoreCase);

    private static string InjectType(string rawObjectJson, string type)
    {
        var node = JsonNode.Parse(rawObjectJson) as JsonObject;
        if (node is null)
            return rawObjectJson;

        node["type"] = type;
        return node.ToJsonString(EurouterJsonOptions);
    }

    private static object? TakeObject(IDictionary<string, object?> source, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!source.TryGetValue(key, out var value))
                continue;

            source.Remove(key);
            return ConvertObjectValue(value);
        }

        return null;
    }

    private static string? TakeString(IDictionary<string, object?> source, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!source.TryGetValue(key, out var value))
                continue;

            source.Remove(key);

            if (value is string s)
                return s;

            if (value is JsonElement el && el.ValueKind == JsonValueKind.String)
                return el.GetString();

            var asObject = ConvertObjectValue(value);
            return asObject?.ToString();
        }

        return null;
    }

    private static bool? TakeBool(IDictionary<string, object?> source, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!source.TryGetValue(key, out var value))
                continue;

            source.Remove(key);

            if (value is bool b)
                return b;

            if (value is JsonElement el)
            {
                if (el.ValueKind == JsonValueKind.True)
                    return true;
                if (el.ValueKind == JsonValueKind.False)
                    return false;
                if (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out var fromString))
                    return fromString;
            }

            if (value is string s && bool.TryParse(s, out var parsed))
                return parsed;
        }

        return null;
    }

    private static int? TakeInt(IDictionary<string, object?> source, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!source.TryGetValue(key, out var value))
                continue;

            source.Remove(key);

            if (value is int i)
                return i;

            if (value is long l && l >= int.MinValue && l <= int.MaxValue)
                return (int)l;

            if (value is JsonElement el)
            {
                if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n))
                    return n;

                if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var sn))
                    return sn;
            }

            if (value is string s && int.TryParse(s, out var parsed))
                return parsed;
        }

        return null;
    }

    private static string[]? TakeStringArray(IDictionary<string, object?> source, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!source.TryGetValue(key, out var value))
                continue;

            source.Remove(key);

            if (value is IEnumerable<string> strings)
            {
                var arr = strings.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
                return arr.Length == 0 ? null : arr;
            }

            if (value is string s)
            {
                var split = s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return split.Length == 0 ? null : split;
            }

            if (value is JsonElement el)
            {
                if (el.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<string>();
                    foreach (var item in el.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            var itemString = item.GetString();
                            if (!string.IsNullOrWhiteSpace(itemString))
                                list.Add(itemString);
                        }
                    }

                    return list.Count == 0 ? null : [.. list];
                }

                if (el.ValueKind == JsonValueKind.String)
                {
                    var valueString = el.GetString();
                    if (string.IsNullOrWhiteSpace(valueString))
                        return null;

                    var split = valueString.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    return split.Length == 0 ? null : split;
                }
            }
        }

        return null;
    }

    private static object? ConvertObjectValue(object? value)
    {
        if (value is null)
            return null;

        if (value is JsonElement el)
            return JsonElementToObject(el);

        if (value is JsonNode node)
            return JsonSerializer.Deserialize<object>(node.ToJsonString(), EurouterJsonOptions);

        return CloneAsUntyped(value);
    }
}
