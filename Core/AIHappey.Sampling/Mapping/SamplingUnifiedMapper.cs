using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Sampling.Mapping;

public static class SamplingUnifiedMapper
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    public static AIRequest ToUnifiedRequest(this CreateMessageRequestParams request, string providerId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        var model = request.ModelPreferences?.Hints?.FirstOrDefault()?.Name;
        var modelId = model?.Contains('/')
            == true
                ? model.Split('/', 2)[1]
                : model;

        return new AIRequest
        {
            ProviderId = providerId,
            Model = modelId,
            Instructions = request.SystemPrompt,
            Temperature = request.Temperature,
            MaxOutputTokens = request.MaxTokens,
            Input = new AIInput
            {
                Items = request.Messages?.Select(ToUnifiedInputItem).ToList()
            },
            Metadata = request.Metadata?
                .ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value)
        };
    }

    public static CreateMessageRequestParams ToSamplingRequest(this AIRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = new CreateMessageRequestParams
        {
            SystemPrompt = request.Instructions,
            Temperature = request.Temperature,
            MaxTokens = request.MaxOutputTokens ?? int.MaxValue,
            Messages = [.. (request.Input?.Items ?? []).Select(ToSamplingMessage)],
            Metadata = BuildSamplingRequestMetadata(request.Metadata)
        };

        SetModelPreferenceByReflection(result, request.Model);
        return result;
    }

    public static AIResponse ToUnifiedResponse(this CreateMessageResult result, string providerId)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        var status = ToUnifiedStatus(result.StopReason);
        var output = new AIOutput
        {
            Items =
            [
                new AIOutputItem
                {
                    Type = "message",
                    Role = result.Role.ToString().ToLowerInvariant(),
                    Content = [.. (result.Content ?? [])
                        .Select(ToUnifiedContentPart)
                        .Where(a => a is not null)
                        .Select(a => a!)]
                }
            ]
        };

        return new AIResponse
        {
            ProviderId = providerId,
            Model = result.Model,
            Status = status,
            Output = output,
            Usage = BuildUsage(result.Meta),
            Metadata = BuildUnifiedResponseMetadata(result)
        };
    }

    public static CreateMessageResult ToSamplingResult(this AIResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var firstOutput = response.Output?.Items?.FirstOrDefault();

        var content = response.Output?.Items?
            .SelectMany(a => a.Content ?? [])
            .Select(ToSamplingContentBlock)
            .Where(a => a is not null)
            .Select(a => a!)
            .ToList() ?? [];

        if (content.Count == 0)
            content.Add(new TextContentBlock { Text = string.Empty });

        return new CreateMessageResult
        {
            Model = response.Model ?? string.Empty,
            StopReason = ToSamplingStopReason(response.Status),
            Role = ParseRole(firstOutput?.Role),
            Content = content,
            Meta = BuildSamplingResultMeta(response)
        };
    }

    private static AIInputItem ToUnifiedInputItem(SamplingMessage message)
    {
        var content = (message.Content ?? [])
            .Select(ToUnifiedContentPart)
            .Where(a => a is not null)
            .Select(a => a!)
            .ToList();

        return new AIInputItem
        {
            Type = "message",
            Role = message.Role.ToString().ToLowerInvariant(),
            Content = content,
            Metadata = new Dictionary<string, object?>
            {
                ["sampling.role"] = message.Role.ToString(),
                ["sampling.rawMessage"] = JsonSerializer.SerializeToElement(message, Json)
            }
        };
    }

    private static SamplingMessage ToSamplingMessage(AIInputItem item)
    {
        var raw = GetJsonElement(item.Metadata, "sampling.rawMessage");
        if (raw.HasValue)
        {
            try
            {
                var hydrated = raw.Value.Deserialize<SamplingMessage>(Json);
                if (hydrated is not null)
                    return hydrated;
            }
            catch
            {
            }
        }

        var blocks = (item.Content ?? [])
            .Select(ToSamplingContentBlock)
            .Where(a => a is not null)
            .Select(a => a!)
            .ToList();

        if (blocks.Count == 0)
            blocks.Add(new TextContentBlock { Text = string.Empty });

        return new SamplingMessage
        {
            Role = ParseRole(item.Role),
            Content = blocks
        };
    }

    private static AIContentPart? ToUnifiedContentPart(ContentBlock block)
    {
        switch (block)
        {
            case TextContentBlock text:
                return new AITextContentPart
                {
                    Type = "text",
                    Text = text.Text,
                    Metadata = new Dictionary<string, object?>
                    {
                        ["sampling.content.type"] = "text"
                    }
                };

            case ImageContentBlock image:
                return new AIFileContentPart
                {
                    Type = "file",
                    MediaType = image.MimeType,
                    Data = ToDataUrl(image.MimeType, NormalizeBinaryData(image.Data)),
                    Metadata = new Dictionary<string, object?>
                    {
                        ["sampling.content.type"] = "image"
                    }
                };

            case AudioContentBlock audio:
                return new AIFileContentPart
                {
                    Type = "file",
                    MediaType = audio.MimeType,
                    Data = ToDataUrl(audio.MimeType, NormalizeBinaryData(audio.Data)),
                    Metadata = new Dictionary<string, object?>
                    {
                        ["sampling.content.type"] = "audio"
                    }
                };

            case EmbeddedResourceBlock embedded:
                return ToUnifiedEmbeddedPart(embedded);

            default:
                return new AITextContentPart
                {
                    Type = "text",
                    Text = JsonSerializer.Serialize(block, block.GetType(), Json),
                    Metadata = new Dictionary<string, object?>
                    {
                        ["sampling.content.unmapped"] = true,
                        ["sampling.content.clr"] = block.GetType().Name
                    }
                };
        }
    }

    private static AIContentPart? ToUnifiedEmbeddedPart(EmbeddedResourceBlock embedded)
    {
        if (embedded.Resource is TextResourceContents text)
        {
            return new AIFileContentPart
            {
                Type = "file",
                MediaType = text.MimeType,
                Filename = TryGetFileName(text.Uri),
                Data = text.Text,
                Metadata = new Dictionary<string, object?>
                {
                    ["sampling.content.type"] = "embedded_text",
                    ["sampling.resource.uri"] = text.Uri
                }
            };
        }

        if (embedded.Resource is BlobResourceContents blob)
        {
            return new AIFileContentPart
            {
                Type = "file",
                MediaType = blob.MimeType,
                Filename = TryGetFileName(blob.Uri),
                Data = ToDataUrl(blob.MimeType, NormalizeBinaryData(blob.Blob)),
                Metadata = new Dictionary<string, object?>
                {
                    ["sampling.content.type"] = "embedded_blob",
                    ["sampling.resource.uri"] = blob.Uri
                }
            };
        }

        return null;
    }

    private static ContentBlock? ToSamplingContentBlock(AIContentPart part)
    {
        switch (part)
        {
            case AITextContentPart text:
                return string.IsNullOrWhiteSpace(text.Text)
                    ? null
                    : new TextContentBlock { Text = text.Text };

            case AIFileContentPart file:
                return ToSamplingFileContentBlock(file);

            default:
                return null;
        }
    }

    private static ContentBlock? ToSamplingFileContentBlock(AIFileContentPart file)
    {
        var kind = GetString(file.Metadata, "sampling.content.type")?.ToLowerInvariant();
        var mediaType = file.MediaType ?? "application/octet-stream";
        var data = file.Data?.ToString();

        if (string.Equals(kind, "audio", StringComparison.OrdinalIgnoreCase)
            || mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
        {
            if (TryExtractBytes(data, out var audioBytes))
            {
                return new AudioContentBlock
                {
                    MimeType = mediaType,
                    Data = audioBytes
                };
            }
        }

        if (string.Equals(kind, "image", StringComparison.OrdinalIgnoreCase)
            || mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            if (TryExtractBytes(data, out var imageBytes))
            {
                return ImageContentBlock.FromBytes(imageBytes, mediaType);
            }
        }

        var resourceUri = GetString(file.Metadata, "sampling.resource.uri")
            ?? file.Filename
            ?? "sampling://resource";

        if (string.Equals(kind, "embedded_text", StringComparison.OrdinalIgnoreCase)
            || mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase))
        {
            return new EmbeddedResourceBlock
            {
                Resource = new TextResourceContents
                {
                    Uri = resourceUri,
                    MimeType = mediaType,
                    Text = data ?? string.Empty
                }
            };
        }

        if (TryExtractBytes(data, out var blobBytes))
        {
            return new EmbeddedResourceBlock
            {
                Resource = new BlobResourceContents
                {
                    Uri = resourceUri,
                    MimeType = mediaType,
                    Blob = blobBytes
                }
            };
        }

        return new EmbeddedResourceBlock
        {
            Resource = new TextResourceContents
            {
                Uri = resourceUri,
                MimeType = "text/plain",
                Text = data ?? string.Empty
            }
        };
    }

    private static JsonObject? BuildSamplingRequestMetadata(Dictionary<string, object?>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
            return null;

        var json = new JsonObject();

        foreach (var (key, value) in metadata)
        {
            if (key.StartsWith("sampling.", StringComparison.OrdinalIgnoreCase))
                continue;

            json[key] = value is null
                ? null
                : JsonSerializer.SerializeToNode(value, Json);
        }

        return json.Count == 0 ? null : json;
    }

    private static Dictionary<string, object?> BuildUnifiedResponseMetadata(CreateMessageResult result)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["sampling.stop_reason"] = result.StopReason,
            ["sampling.role"] = result.Role.ToString(),
            ["sampling.rawResult"] = JsonSerializer.SerializeToElement(result, Json)
        };

        if (result.Meta is JsonObject meta)
        {
            if (meta.TryGetPropertyValue("metadata", out var nestedMetadata))
            {
                foreach (var property in EnumerateObjectProperties(nestedMetadata))
                {
                    if (metadata.ContainsKey(property.Name) || IsLegacyUsageKey(property.Name))
                        continue;

                    metadata[property.Name] = property.Value.Clone();
                }
            }

            foreach (var (key, value) in meta)
            {
                if (string.Equals(key, "metadata", StringComparison.OrdinalIgnoreCase)
                    || IsLegacyUsageKey(key))
                {
                    continue;
                }

                metadata[key] = value is null
                    ? null
                    : JsonSerializer.SerializeToElement(value, Json);
            }
        }

        return metadata;
    }

    private static object? BuildUsage(JsonObject? meta)
    {
        if (meta is null)
            return null;

        var promptTokens = ExtractUsageInt(meta, "promptTokens", "prompt_tokens", "inputTokens", "input_tokens");
        var completionTokens = ExtractUsageInt(meta, "completionTokens", "completion_tokens", "outputTokens", "output_tokens");
        var totalTokens = ExtractUsageInt(meta, "totalTokens", "total_tokens");

        if (promptTokens is null && completionTokens is null && totalTokens is null)
            return null;

        var usage = new Dictionary<string, object?>
        {
            ["promptTokens"] = promptTokens,
            ["completionTokens"] = completionTokens,
            ["totalTokens"] = totalTokens ?? ((promptTokens ?? 0) + (completionTokens ?? 0))
        };

        return usage.Count == 0 ? null : usage;
    }

    private static JsonObject? BuildSamplingResultMeta(AIResponse response)
    {
        var meta = new JsonObject();

        if (response.Metadata is not null)
        {
            foreach (var (key, value) in response.Metadata)
            {
                if (key.StartsWith("sampling.", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(key, "metadata", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(key, "usage", StringComparison.OrdinalIgnoreCase)
                    || IsLegacyUsageKey(key))
                {
                    continue;
                }

                meta[key] = value is null
                    ? null
                    : JsonSerializer.SerializeToNode(value, Json);
            }

            if (response.Metadata.TryGetValue("metadata", out var nestedMetadata))
            {
                foreach (var property in EnumerateObjectProperties(nestedMetadata))
                {
                    if (meta.ContainsKey(property.Name) || IsLegacyUsageKey(property.Name))
                        continue;

                    meta[property.Name] = JsonNode.Parse(property.Value.GetRawText());
                }
            }
        }

        var usage = BuildSamplingMetaUsage(response.Usage, response.Metadata);
        if (usage is not null)
        {
            meta["usage"] = usage;
        }

        meta.Remove("inputTokens");
        meta.Remove("outputTokens");
        meta.Remove("totalTokens");

        return meta.Count == 0 ? null : meta;
    }

    private static JsonObject? BuildSamplingMetaUsage(object? usage, Dictionary<string, object?>? metadata)
    {
        var promptTokens = ExtractUsageInt(usage, "promptTokens", "prompt_tokens", "inputTokens", "input_tokens")
            ?? ExtractMetadataInt(metadata, "promptTokens", "prompt_tokens", "inputTokens", "input_tokens");
        var completionTokens = ExtractUsageInt(usage, "completionTokens", "completion_tokens", "outputTokens", "output_tokens")
            ?? ExtractMetadataInt(metadata, "completionTokens", "completion_tokens", "outputTokens", "output_tokens");
        var totalTokens = ExtractUsageInt(usage, "totalTokens", "total_tokens")
            ?? ExtractMetadataInt(metadata, "totalTokens", "total_tokens");

        if (promptTokens is null && completionTokens is null && totalTokens is null)
            return null;

        return new JsonObject
        {
            ["promptTokens"] = promptTokens,
            ["completionTokens"] = completionTokens,
            ["totalTokens"] = totalTokens ?? ((promptTokens ?? 0) + (completionTokens ?? 0))
        };
    }

    private static int? ExtractMetadataInt(Dictionary<string, object?>? metadata, params string[] keys)
    {
        if (metadata is null || metadata.Count == 0)
            return null;

        foreach (var key in keys)
        {
            if (TryReadInt(metadata.TryGetValue(key, out var directValue) ? directValue : null, out var directInt))
                return directInt;
        }

        if (!metadata.TryGetValue("usage", out var rawUsage))
            rawUsage = null;

        var usageValue = ExtractUsageInt(rawUsage, keys);
        if (usageValue is not null)
            return usageValue;

        if (!metadata.TryGetValue("metadata", out var nestedMetadata))
            return null;

        foreach (var property in EnumerateObjectProperties(nestedMetadata))
        {
            if (!keys.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                continue;

            if (TryReadInt(property.Value, out var nestedInt))
                return nestedInt;
        }

        return null;
    }

    private static int? ExtractUsageInt(JsonObject? meta, params string[] keys)
    {
        if (meta is null)
            return null;

        if (meta.TryGetPropertyValue("usage", out var usageNode))
        {
            var usageValue = ExtractUsageInt(usageNode, keys);
            if (usageValue is not null)
                return usageValue;
        }

        foreach (var key in keys)
        {
            if (meta.TryGetPropertyValue(key, out var directNode)
                && TryReadInt(directNode, out var directInt))
            {
                return directInt;
            }
        }

        if (!meta.TryGetPropertyValue("metadata", out var nestedMetadata))
            return null;

        foreach (var property in EnumerateObjectProperties(nestedMetadata))
        {
            if (!keys.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                continue;

            if (TryReadInt(property.Value, out var nestedInt))
                return nestedInt;
        }

        return null;
    }

    private static int? ExtractUsageInt(object? usage, params string[] keys)
    {
        var json = SerializeToJsonElement(usage);
        if (json is not { ValueKind: JsonValueKind.Object })
            return null;

        foreach (var key in keys)
        {
            if (json.Value.TryGetProperty(key, out var value)
                && TryReadInt(value, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static IEnumerable<JsonProperty> EnumerateObjectProperties(object? value)
    {
        var json = SerializeToJsonElement(value);
        if (json is not { ValueKind: JsonValueKind.Object })
            return [];

        return json.Value.EnumerateObject().ToArray();
    }

    private static JsonElement? SerializeToJsonElement(object? value)
    {
        if (value is null)
            return null;

        if (value is JsonElement json)
            return json;

        try
        {
            return JsonSerializer.SerializeToElement(value, Json);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryReadInt(object? value, out int result)
    {
        result = 0;

        if (value is null)
            return false;

        if (value is JsonNode node)
            return TryReadInt(SerializeToJsonElement(node), out result);

        if (value is JsonElement json)
            return TryReadInt(json, out result);

        return value switch
        {
            int intValue => (result = intValue) == intValue,
            long longValue when longValue >= int.MinValue && longValue <= int.MaxValue => (result = (int)longValue) >= int.MinValue,
            string text when int.TryParse(text, out var parsed) => (result = parsed) == parsed,
            _ => int.TryParse(value.ToString(), out result)
        };
    }

    private static bool TryReadInt(JsonElement? value, out int result)
    {
        result = 0;

        if (value is null)
            return false;

        return TryReadInt(value.Value, out result);
    }

    private static bool TryReadInt(JsonElement value, out int result)
    {
        result = 0;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var intValue) => (result = intValue) == intValue,
            JsonValueKind.Number when value.TryGetInt64(out var longValue) && longValue >= int.MinValue && longValue <= int.MaxValue => (result = (int)longValue) >= int.MinValue,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => (result = parsed) == parsed,
            _ => false
        };
    }

    private static bool IsLegacyUsageKey(string key)
        => key is "inputTokens" or "outputTokens" or "totalTokens";

    private static string? GetString(Dictionary<string, object?>? metadata, string key)
    {
        if (metadata is null || !metadata.TryGetValue(key, out var value) || value is null)
            return null;

        if (value is string text)
            return text;

        if (value is JsonElement json && json.ValueKind == JsonValueKind.String)
            return json.GetString();

        return value.ToString();
    }

    private static JsonElement? GetJsonElement(Dictionary<string, object?>? metadata, string key)
    {
        if (metadata is null || !metadata.TryGetValue(key, out var value) || value is null)
            return null;

        if (value is JsonElement json)
            return json;

        try
        {
            return JsonSerializer.SerializeToElement(value, Json);
        }
        catch
        {
            return null;
        }
    }

    private static string ToUnifiedStatus(string? stopReason)
        => string.IsNullOrWhiteSpace(stopReason) ? "completed" : stopReason switch
        {
            "stop" => "completed",
            "endTurn" => "completed",
            "maxTokens" => "incomplete",
            "length" => "incomplete",
            "error" => "failed",
            "cancelled" => "cancelled",
            _ => stopReason
        };

    private static string ToSamplingStopReason(string? status)
        => string.IsNullOrWhiteSpace(status) ? "stop" : status switch
        {
            "completed" => "stop",
            "incomplete" => "maxTokens",
            "failed" => "error",
            "cancelled" => "cancelled",
            _ => status
        };

    private static Role ParseRole(string? role)
        => role?.Trim().ToLowerInvariant() switch
        {
            "assistant" => Role.Assistant,
            _ => Role.User
        };

    private static string ToDataUrl(string? mediaType, byte[] data)
    {
        var mime = string.IsNullOrWhiteSpace(mediaType) ? "application/octet-stream" : mediaType;
        return $"data:{mime};base64,{Convert.ToBase64String(data)}";
    }

    private static byte[] NormalizeBinaryData(ReadOnlyMemory<byte> data)
    {
        var bytes = data.ToArray();
        if (bytes.Length == 0 || !LooksLikeEncodedText(bytes))
            return bytes;

        var text = Encoding.UTF8.GetString(bytes).Trim();
        return TryExtractBytes(text, out var normalized) && normalized.Length > 0
            ? normalized
            : bytes;
    }

    private static bool LooksLikeEncodedText(byte[] bytes)
    {
        foreach (var value in bytes)
        {
            if (value is 9 or 10 or 13)
                continue;

            if (value < 32 || value > 126)
                return false;
        }

        return true;
    }

    private static bool TryExtractBytes(string? value, out byte[] bytes)
    {
        bytes = [];

        if (string.IsNullOrWhiteSpace(value))
            return false;

        const string marker = ";base64,";

        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var idx = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return false;

            var payload = value[(idx + marker.Length)..];

            try
            {
                bytes = Convert.FromBase64String(payload);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        try
        {
            bytes = Convert.FromBase64String(value);
            return true;
        }
        catch (FormatException)
        {
            bytes = Encoding.UTF8.GetBytes(value);
            return true;
        }
    }

    private static string? TryGetFileName(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return null;

        try
        {
            return Path.GetFileName(uri);
        }
        catch
        {
            return null;
        }
    }

    private static void SetModelPreferenceByReflection(CreateMessageRequestParams request, string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return;

        var requestType = request.GetType();
        var modelPreferencesProperty = requestType.GetProperty("ModelPreferences", BindingFlags.Public | BindingFlags.Instance);
        var modelPreferencesType = modelPreferencesProperty?.PropertyType;
        if (modelPreferencesProperty is null || modelPreferencesType is null)
            return;

        var modelPreferences = Activator.CreateInstance(modelPreferencesType);
        if (modelPreferences is null)
            return;

        var hintsProperty = modelPreferencesType.GetProperty("Hints", BindingFlags.Public | BindingFlags.Instance);
        var hintsType = hintsProperty?.PropertyType;
        if (hintsProperty is null || hintsType is null)
            return;

        var hintElementType = hintsType.IsArray
            ? hintsType.GetElementType()
            : hintsType.GetGenericArguments().FirstOrDefault();
        if (hintElementType is null)
            return;

        var hintInstance = Activator.CreateInstance(hintElementType);
        if (hintInstance is null)
            return;

        var nameProperty = hintElementType.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
        nameProperty?.SetValue(hintInstance, model);

        object? hintsValue;
        if (hintsType.IsArray)
        {
            var array = Array.CreateInstance(hintElementType, 1);
            array.SetValue(hintInstance, 0);
            hintsValue = array;
        }
        else
        {
            var listType = typeof(List<>).MakeGenericType(hintElementType);
            var list = Activator.CreateInstance(listType);
            listType.GetMethod("Add")?.Invoke(list, [hintInstance]);
            hintsValue = list;
        }

        hintsProperty.SetValue(modelPreferences, hintsValue);
        modelPreferencesProperty.SetValue(request, modelPreferences);
    }
}

