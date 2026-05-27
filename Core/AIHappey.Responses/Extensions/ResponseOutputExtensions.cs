using System.Text.Json;

namespace AIHappey.Responses;

public static class ResponseOutputExtensions
{
    public static IEnumerable<string> GetAssistantOutputTextItems(this IEnumerable<object>? output)
    {
        if (output is null)
            yield break;

        foreach (var item in output)
        {
            foreach (var text in GetAssistantOutputTextItems(item))
                yield return text;
        }
    }

    public static string GetAssistantOutputText(this IEnumerable<object>? output)
        => string.Concat(output.GetAssistantOutputTextItems());

    private static IEnumerable<string> GetAssistantOutputTextItems(object? item)
    {
        if (item is null)
            yield break;

        if (item is JsonElement element)
        {
            foreach (var text in FromJsonElement(element))
                yield return text;

            yield break;
        }

        var json = JsonSerializer.SerializeToElement(item);

        foreach (var text in FromJsonElement(json))
            yield return text;
    }

    private static IEnumerable<string> FromJsonElement(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            yield break;

        if (!HasString(item, "type", "message"))
            yield break;

        if (!HasString(item, "role", "assistant"))
            yield break;

        if (!item.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object)
                continue;

            if (!HasString(part, "type", "output_text"))
                continue;

            if (part.TryGetProperty("text", out var text) &&
                text.ValueKind == JsonValueKind.String)
            {
                yield return text.GetString() ?? string.Empty;
            }
        }
    }

    private static bool HasString(JsonElement element, string propertyName, string value)
        => element.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.String &&
           string.Equals(property.GetString(), value, StringComparison.Ordinal);
}