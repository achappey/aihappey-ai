using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Responses;

namespace AIHappey.Core.Providers.Exa;

public partial class ExaProvider
{
    private const string AnswerModelId = "answer";
    private const string DefaultSearchType = "auto";
    private static readonly HashSet<string> SearchTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "auto",
        "neural",
        "fast",
        "deep-lite",
        "deep",
        "deep-reasoning",
        "instant"
    };

    private static readonly JsonSerializerOptions JsonWeb = JsonSerializerOptions.Web;

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Exa)} API key.");

        _client.DefaultRequestHeaders.Authorization = null;
        _client.DefaultRequestHeaders.Remove("x-api-key");
        _client.DefaultRequestHeaders.Add("x-api-key", key);
    }

    private static bool IsAnswerModel(string? model)
        => string.Equals(ResolveLocalModelIdStatic(model), AnswerModelId, StringComparison.OrdinalIgnoreCase)
           || string.Equals(ResolveLocalModelIdStatic(model), "exa", StringComparison.OrdinalIgnoreCase);

    private static bool IsSearchModel(string? model)
        => SearchTypes.Contains(ResolveSearchType(model));

    private static ExaBackendTarget ResolveBackendTarget(string? model)
    {
        var local = ResolveLocalModelIdStatic(model);
        if (string.IsNullOrWhiteSpace(local) || string.Equals(local, "exa", StringComparison.OrdinalIgnoreCase))
            local = DefaultSearchType;

        if (string.Equals(local, AnswerModelId, StringComparison.OrdinalIgnoreCase))
            return new ExaBackendTarget("answer", AnswerModelId, "answer");

        if (SearchTypes.Contains(local))
            return new ExaBackendTarget("search", local, local);

        throw new NotSupportedException($"Unsupported Exa model '{model}'. Supported models: exa/answer, {string.Join(", ", SearchTypes.Select(t => $"exa/{t}"))}.");
    }

    private static string ResolveSearchType(string? model)
    {
        var local = ResolveLocalModelIdStatic(model);
        return string.IsNullOrWhiteSpace(local) || string.Equals(local, "exa", StringComparison.OrdinalIgnoreCase)
            ? DefaultSearchType
            : local;
    }

    private static string ResolveLocalModelIdStatic(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return string.Empty;

        var trimmed = model.Trim();
        const string providerPrefix = "exa/";
        if (trimmed.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            return trimmed[providerPrefix.Length..];

        return trimmed;
    }

    private static string ToProviderModelId(string? model)
    {
        var local = ResolveLocalModelIdStatic(model);
        if (string.IsNullOrWhiteSpace(local))
            local = DefaultSearchType;

        return local.StartsWith("exa/", StringComparison.OrdinalIgnoreCase)
            ? local
            : $"exa/{local}";
    }

    private static string BuildPromptFromCompletionMessages(IEnumerable<ChatMessage> messages)
    {
        var all = messages?.ToList() ?? [];
        if (all.Count == 0)
            return string.Empty;

        var lines = new List<string>();
        var system = new List<string>();

        foreach (var msg in all)
        {
            var role = (msg.Role ?? string.Empty).Trim().ToLowerInvariant();
            var text = ExtractCompletionMessageText(msg.Content);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            if (role == "system")
            {
                system.Add(text!);
                continue;
            }

            if (role is not ("user" or "assistant"))
                continue;

            lines.Add($"{role}: {text}");
        }

        if (system.Count > 0)
            lines.Insert(0, $"system: {string.Join("\n\n", system)}");

        return string.Join("\n\n", lines);
    }

    private static string? ExtractCompletionMessageText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString();

        if (content.ValueKind == JsonValueKind.Array)
        {
            var builder = new StringBuilder();
            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.String)
                {
                    builder.Append(part.GetString());
                    continue;
                }

                if (part.ValueKind != JsonValueKind.Object)
                    continue;

                if (part.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                    builder.Append(textEl.GetString());
                else if (part.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
                    builder.Append(contentEl.GetString());
            }

            var value = builder.ToString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        if (content.ValueKind == JsonValueKind.Object
            && content.TryGetProperty("text", out var objectText)
            && objectText.ValueKind == JsonValueKind.String)
        {
            return objectText.GetString();
        }

        return content.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? null
            : content.GetRawText();
    }

    private static object? TryExtractOutputSchema(object? format)
    {
        if (format is null)
            return null;

        var schema = format.GetJSONSchema();
        if (schema?.JsonSchema is not null)
        {
            var element = schema.JsonSchema.Schema;
            if (element.ValueKind != JsonValueKind.Undefined && element.ValueKind != JsonValueKind.Null)
                return JsonSerializer.Deserialize<object>(element.GetRawText(), JsonWeb);
        }

        try
        {
            var raw = JsonSerializer.SerializeToElement(format, JsonWeb);

            if (raw.ValueKind == JsonValueKind.Object
                && raw.TryGetProperty("json_schema", out var jsonSchema)
                && jsonSchema.ValueKind == JsonValueKind.Object
                && jsonSchema.TryGetProperty("schema", out var schemaEl)
                && schemaEl.ValueKind == JsonValueKind.Object)
            {
                return JsonSerializer.Deserialize<object>(schemaEl.GetRawText(), JsonWeb);
            }

            if (raw.ValueKind == JsonValueKind.Object
                && raw.TryGetProperty("schema", out var directSchema)
                && directSchema.ValueKind == JsonValueKind.Object)
            {
                return JsonSerializer.Deserialize<object>(directSchema.GetRawText(), JsonWeb);
            }
        }
        catch
        {
            // ignore schema extraction failures
        }

        return null;
    }

    private static string ToOutputText(object? content)
    {
        if (content is null)
            return string.Empty;

        if (content is string text)
            return text;

        if (content is JsonElement el)
        {
            return el.ValueKind == JsonValueKind.String
                ? el.GetString() ?? string.Empty
                : el.GetRawText();
        }

        return JsonSerializer.Serialize(content, JsonWeb);
    }

    private static ResponseInput BuildResponseInputFromSampling(ModelContextProtocol.Protocol.CreateMessageRequestParams chatRequest)
    {
        var items = new List<ResponseInputItem>();

        foreach (var msg in chatRequest.Messages)
        {
            var parts = msg.Content
                .OfType<ModelContextProtocol.Protocol.TextContentBlock>()
                .Select(a => new InputTextPart(a.Text))
                .Cast<ResponseContentPart>()
                .ToList();

            if (parts.Count == 0)
            {
                var text = msg.ToText();
                if (!string.IsNullOrWhiteSpace(text))
                    parts.Add(new InputTextPart(text));
            }

            if (parts.Count == 0)
                continue;

            var role = msg.Role == ModelContextProtocol.Protocol.Role.Assistant
                ? ResponseRole.Assistant
                : ResponseRole.User;

            items.Add(new ResponseInputMessage
            {
                Role = role,
                Content = new ResponseMessageContent(parts)
            });
        }

        return new ResponseInput(items);
    }

    private static Dictionary<string, object?>? JsonElementObjectToDictionary(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in element.EnumerateObject())
            result[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText(), JsonWeb);

        return result;
    }

    private static JsonObject? JsonElementObjectToJsonObject(JsonElement element)
        => element.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(element.GetRawText()) as JsonObject
            : null;

    private static JsonElement? CloneProperty(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var property)
            ? property.Clone()
            : null;

    private sealed record ExaBackendTarget(string Backend, string LocalModel, string NativeType);
}
