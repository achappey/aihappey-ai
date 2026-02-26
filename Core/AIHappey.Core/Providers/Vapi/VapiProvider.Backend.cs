using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.Vapi;

public partial class VapiProvider
{
    private Dictionary<string, object?> BuildResponsesPayload(
        ResponseRequest options,
        string normalizedModel,
        bool contentOnlyForAllRoles = false)
    {
        var assistant = BuildTransientAssistant(normalizedModel);
        var mappedInput = MapInputForVapiBackend(options.Input, contentOnlyForAllRoles);

        var payload = new Dictionary<string, object?>
        {
            ["assistant"] = assistant,
            ["input"] = mappedInput,
            ["temperature"] = options.Temperature,
            ["max_output_tokens"] = options.MaxOutputTokens,
            ["stream"] = options.Stream
        };

        return payload;
    }

    private object BuildTransientAssistant(string normalizedModel)
    {
        var (provider, model) = ResolveAssistantModel(normalizedModel);
        return new
        {
            model = new
            {
                provider,
                model
            }
        };
    }

    private (string Provider, string Model) ResolveAssistantModel(string normalizedModel)
    {
        if (string.IsNullOrWhiteSpace(normalizedModel))
            throw new InvalidOperationException("Model is required.");

        var trimmed = normalizedModel.Trim();

        if (trimmed.Contains('/', StringComparison.Ordinal))
        {
            var split = trimmed.Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
            if (split.Length == 2)
                return (NormalizeVapiModelProvider(split[0]), split[1]);
        }

        var catalogModel = GetIdentifier()
            .GetModels()
            .FirstOrDefault(m =>
                string.Equals(m.Type, "language", StringComparison.OrdinalIgnoreCase)
                && (
                    string.Equals(m.Name, trimmed, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(m.Id.SplitModelId().Model, trimmed, StringComparison.OrdinalIgnoreCase)
                ));

        if (catalogModel is not null)
            return (NormalizeVapiModelProvider(catalogModel.OwnedBy), catalogModel.Name);

        return ("openai", trimmed);
    }

    private static string NormalizeVapiModelProvider(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
            return "openai";

        return provider.Trim() switch
        {
            "Anthropic" => "anthropic",
            "AnthropicBedrock" => "anthropic-bedrock",
            "Anyscale" => "anyscale",
            "Cerebras" => "cerebras",
            "CustomLLM" => "custom-llm",
            "DeepInfra" => "deepinfra",
            "DeepSeek" => "deepseek",
            "Google" => "google",
            "Groq" => "groq",
            "InflectionAI" => "inflection-ai",
            "OpenAI" => "openai",
            "OpenRouter" => "openrouter",
            "PerplexityAI" => "perplexity-ai",
            "Together" => "together",
            "XAI" => "xai",
            _ => provider.Trim().ToLowerInvariant()
        };
    }

    private static object? MapInputForVapiBackend(ResponseInput? input, bool contentOnlyForAllRoles)
    {
        if (input is null)
            return null;

        if (input.IsText)
            return input.Text;

        if (input.Items is null || input.Items.Count == 0)
            return input;

        var mapped = new List<object>(input.Items.Count);
        double secondsFromStart = 0;

        foreach (var item in input.Items)
        {
            if (item is ResponseInputMessage message)
            {
                mapped.Add(MapMessageForVapiBackend(message, secondsFromStart, contentOnlyForAllRoles));
                secondsFromStart += 1;
                continue;
            }

            if (item is ResponseItemReference reference)
            {
                mapped.Add(new
                {
                    type = "item_reference",
                    id = reference.Id
                });
                continue;
            }

            mapped.Add(item);
        }

        return mapped;
    }

    private static object MapMessageForVapiBackend(
        ResponseInputMessage message,
        double secondsFromStart,
        bool contentOnlyForAllRoles)
    {
        var role = message.Role switch
        {
            ResponseRole.System => "system",
            ResponseRole.Assistant => "assistant",
            ResponseRole.Developer => "developer",
            _ => "user"
        };

        var text = FlattenMessageText(message.Content);

        if (!contentOnlyForAllRoles && string.Equals(role, "system", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                role,
                message = text,
                time = secondsFromStart,
                secondsFromStart
            };
        }

        if (!contentOnlyForAllRoles && string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                role,
                message = text,
                time = secondsFromStart,
                endTime = secondsFromStart,
                secondsFromStart
            };
        }

        return new
        {
            role,
            content = text
        };
    }

    private static string FlattenMessageText(ResponseMessageContent? content)
    {
        if (content is null)
            return string.Empty;

        if (content.IsText)
            return content.Text ?? string.Empty;

        if (content.Parts is null || content.Parts.Count == 0)
            return string.Empty;

        var chunks = new List<string>(content.Parts.Count);
        foreach (var part in content.Parts)
        {
            if (part is InputTextPart t && !string.IsNullOrWhiteSpace(t.Text))
                chunks.Add(t.Text);
            else if (part is InputImagePart i && !string.IsNullOrWhiteSpace(i.ImageUrl))
                chunks.Add(i.ImageUrl!);
        }

        return string.Join("\n", chunks);
    }

    private static bool TryParseResponseStreamPart(string? eventType, string payloadData, out ResponseStreamPart? part)
    {
        part = null;

        try
        {
            using var doc = JsonDocument.Parse(payloadData);
            var root = doc.RootElement;

            var type = root.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
                ? typeEl.GetString()
                : eventType;

            if (string.IsNullOrWhiteSpace(type))
                return false;

            if (string.Equals(type, "response.error", StringComparison.OrdinalIgnoreCase))
            {
                string message = "Unknown error";
                string param = string.Empty;
                string code = string.Empty;

                if (root.TryGetProperty("error", out var errorEl) && errorEl.ValueKind == JsonValueKind.Object)
                {
                    if (errorEl.TryGetProperty("message", out var messageEl) && messageEl.ValueKind == JsonValueKind.String)
                        message = messageEl.GetString() ?? message;

                    if (errorEl.TryGetProperty("param", out var paramEl) && paramEl.ValueKind == JsonValueKind.String)
                        param = paramEl.GetString() ?? string.Empty;

                    if (errorEl.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.String)
                        code = codeEl.GetString() ?? string.Empty;
                }

                part = new ResponseError
                {
                    Message = message,
                    Param = param,
                    Code = code
                };

                return true;
            }

            if (root.TryGetProperty("type", out _))
            {
                part = JsonSerializer.Deserialize<ResponseStreamPart>(payloadData, ResponseJson.Default);
                return part is not null;
            }

            part = type switch
            {
                "response.output_text.delta" => new ResponseOutputTextDelta
                {
                    Delta = TryGetStringOrEmpty(root, "delta"),
                    ItemId = TryGetStringOrEmpty(root, "item_id"),
                    ContentIndex = TryGetInt(root, "content_index"),
                    Outputindex = TryGetInt(root, "output_index"),
                    SequenceNumber = TryGetInt(root, "sequence_number")
                },
                "response.output_text.done" => new ResponseOutputTextDone
                {
                    Text = TryGetStringOrEmpty(root, "text"),
                    ItemId = TryGetStringOrEmpty(root, "item_id"),
                    ContentIndex = TryGetInt(root, "content_index"),
                    Outputindex = TryGetInt(root, "output_index"),
                    SequenceNumber = TryGetInt(root, "sequence_number")
                },
                _ => null
            };

            return part is not null;
        }
        catch
        {
            return false;
        }
    }

    private static string TryGetStringOrEmpty(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            return value.GetString() ?? string.Empty;

        return string.Empty;
    }

    private static int TryGetInt(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i))
            return i;

        return 0;
    }

    private static string? TryExtractChatId(string rawResponse)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(rawResponse);
            var root = doc.RootElement;

            if (TryGetString(root, "chat_id", out var chatId) || TryGetString(root, "chatId", out chatId))
                return chatId;

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("chat", out var chat)
                && chat.ValueKind == JsonValueKind.Object)
            {
                if (TryGetString(chat, "id", out var nestedId)
                    || TryGetString(chat, "chat_id", out nestedId)
                    || TryGetString(chat, "chatId", out nestedId))
                {
                    return nestedId;
                }
            }
        }
        catch
        {
            // no-op
        }

        return null;
    }

    private async Task<object?> TryDeleteChatAsync(string? chatId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(chatId))
            return null;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Delete, $"chat/{Uri.EscapeDataString(chatId)}");
            using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (resp.IsSuccessStatusCode)
                return null;

            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            return new
            {
                type = "cleanup_failed",
                feature = "chat.delete",
                details = $"DELETE /chat/{{id}} failed with {(int)resp.StatusCode} {resp.ReasonPhrase}",
                response = body
            };
        }
        catch (Exception ex)
        {
            return new
            {
                type = "cleanup_failed",
                feature = "chat.delete",
                details = ex.Message
            };
        }
    }

    private static void AddCleanupWarning(ResponseResult result, object warning)
    {
        result.Metadata ??= new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (!result.Metadata.TryGetValue("warnings", out var existing) || existing is null)
        {
            result.Metadata["warnings"] = new List<object> { warning };
            return;
        }

        if (existing is List<object> objectList)
        {
            objectList.Add(warning);
            return;
        }

        if (existing is object[] objectArray)
        {
            var merged = objectArray.ToList();
            merged.Add(warning);
            result.Metadata["warnings"] = merged;
            return;
        }

        if (existing is JsonElement json && json.ValueKind == JsonValueKind.Array)
        {
            var merged = new List<object>();
            foreach (var el in json.EnumerateArray())
                merged.Add(el.Clone());

            merged.Add(warning);
            result.Metadata["warnings"] = merged;
            return;
        }

        result.Metadata["warnings"] = new List<object> { existing, warning };
    }

    private static bool TryGetString(JsonElement obj, string propertyName, out string? value)
    {
        value = null;
        if (obj.ValueKind != JsonValueKind.Object)
            return false;

        if (!obj.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.String)
            return false;

        value = prop.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool ShouldRetryWithContentOnlyInput(string? rawError)
    {
        if (string.IsNullOrWhiteSpace(rawError))
            return false;

        return rawError.Contains("message should not exist", StringComparison.OrdinalIgnoreCase)
               || rawError.Contains("input.property message", StringComparison.OrdinalIgnoreCase);
    }
}

