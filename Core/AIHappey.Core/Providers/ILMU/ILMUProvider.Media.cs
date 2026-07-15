using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.ILMU;

public partial class ILMUProvider
{
    private static readonly JsonSerializerOptions ILMUJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static Dictionary<string, object?> ILMUJsonObjectToDictionary(JsonElement metadata)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (metadata.ValueKind != JsonValueKind.Object)
            return payload;

        foreach (var property in metadata.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                continue;

            payload[property.Name] = ILMUJsonElementToObject(property.Value);
        }

        return payload;
    }

    private static object? ILMUJsonElementToObject(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number when element.TryGetDouble(out var doubleValue) => doubleValue,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => element.Clone()
        };

    private static void AddILMUMultipartProviderOptions(
        MultipartFormDataContent form,
        JsonElement metadata,
        Dictionary<string, object?> requestFields,
        params string[] excludedProperties)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return;

        var excluded = new HashSet<string>(excludedProperties, StringComparer.OrdinalIgnoreCase);

        foreach (var property in metadata.EnumerateObject())
        {
            if (excluded.Contains(property.Name))
                continue;

            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                var values = property.Value.EnumerateArray()
                    .Select(ILMUJsonElementToFormValue)
                    .Where(value => value is not null)
                    .Cast<string>()
                    .ToList();

                if (values.Count == 0)
                    continue;

                requestFields[property.Name] = values;
                foreach (var value in values)
                    form.Add(new StringContent(value, Encoding.UTF8), property.Name);

                continue;
            }

            var formValue = ILMUJsonElementToFormValue(property.Value);
            if (formValue is null)
                continue;

            requestFields[property.Name] = formValue;
            form.Add(new StringContent(formValue, Encoding.UTF8), property.Name);
        }
    }

    private static string? ILMUJsonElementToFormValue(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => element.GetRawText()
        };

    private static bool ILMUTryParseJson(string raw, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(raw);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            document = null!;
            return false;
        }
    }

    private static string? ILMUTryGetString(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var property))
                continue;

            if (property.ValueKind == JsonValueKind.String)
                return property.GetString();

            if (property.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                return property.ToString();
        }

        return null;
    }

    private static int? ILMUTryGetInt(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var property))
                continue;

            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number))
                return number;

            if (property.ValueKind == JsonValueKind.String
                && int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                return number;
        }

        return null;
    }

    private static float? ILMUTryGetFloat(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var property))
                continue;

            if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var number))
                return (float)number;

            if (property.ValueKind == JsonValueKind.String
                && float.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }

        return null;
    }

    private static string? ILMUAudioFormatFromContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return null;

        if (contentType.Contains("mpeg", StringComparison.OrdinalIgnoreCase)) return "mp3";
        if (contentType.Contains("mp3", StringComparison.OrdinalIgnoreCase)) return "mp3";
        if (contentType.Contains("wav", StringComparison.OrdinalIgnoreCase) || contentType.Contains("wave", StringComparison.OrdinalIgnoreCase)) return "wav";
        if (contentType.Contains("opus", StringComparison.OrdinalIgnoreCase)) return "opus";
        if (contentType.Contains("flac", StringComparison.OrdinalIgnoreCase)) return "flac";
        if (contentType.Contains("pcm", StringComparison.OrdinalIgnoreCase)) return "pcm";

        return null;
    }
}
