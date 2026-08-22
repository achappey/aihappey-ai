using System.Globalization;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.FastRouter;

public partial class FastRouterProvider
{
    private static readonly JsonSerializerOptions FastRouterJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };


    private static JsonObject CreateFastRouterPayload(
        Dictionary<string, JsonElement>? rawProperties,
        string providerId,
        params string[] reservedNames)
    {
        var reserved = new HashSet<string>(
            reservedNames,
            StringComparer.OrdinalIgnoreCase);

        var payload = new JsonObject();

        if (rawProperties == null ||
            !rawProperties.TryGetValue(providerId, out var providerProperties) ||
            providerProperties.ValueKind != JsonValueKind.Object)
        {
            return payload;
        }

        foreach (var property in providerProperties.EnumerateObject())
        {
            if (!reserved.Contains(property.Name))
            {
                payload[property.Name] =
                    JsonNode.Parse(property.Value.GetRawText());
            }
        }

        return payload;
    }

    private static JsonObject CreateFlatFastRouterPayload(
        Dictionary<string, JsonElement>? rawProperties,
        params string[] reservedNames)
    {
        var reserved = new HashSet<string>(reservedNames, StringComparer.OrdinalIgnoreCase);
        var payload = new JsonObject();

        foreach (var property in rawProperties ?? [])
        {
            if (!reserved.Contains(property.Key))
                payload[property.Key] = JsonNode.Parse(property.Value.GetRawText());
        }

        return payload;
    }

    private HttpRequestMessage CreateFastRouterJsonRequest(HttpMethod method, string endpoint, JsonObject payload)
        => new(method, endpoint)
        {
            Content = new StringContent(payload.ToJsonString(FastRouterJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

    private async Task<FastRouterJsonResult> SendFastRouterJsonAsync(
        HttpMethod method,
        string endpoint,
        JsonObject payload,
        string operation,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = CreateFastRouterJsonRequest(method, endpoint, payload);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return await ReadFastRouterJsonAsync(response, operation, cancellationToken);
    }

    private static async Task<FastRouterJsonResult> ReadFastRouterJsonAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"FastRouter {operation} failed ({(int)response.StatusCode}): {raw}");

        try
        {
            using var document = JsonDocument.Parse(raw);
            return new FastRouterJsonResult(document.RootElement.Clone(), response.GetHeaders());
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"FastRouter {operation} returned invalid JSON: {raw}", ex);
        }
    }

    private static string? GetFastRouterString(JsonElement element, params string[] path)
    {
        foreach (var segment in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(segment, out element))
                return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.ToString(),
            _ => null
        };
    }

    private static void AddFastRouterFormValue(MultipartFormDataContent form, string name, object? value)
    {
        if (value is null)
            return;

        if (value is JsonElement element)
        {
            if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return;

            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                    AddFastRouterFormValue(form, name, item);
                return;
            }

            value = element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
        }

        if (value is IEnumerable<string> values && value is not string)
        {
            foreach (var item in values)
                AddFastRouterFormValue(form, name, item);
            return;
        }

        var text = value switch
        {
            bool boolean => boolean ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        };

        if (text is not null)
            form.Add(new StringContent(text), name);
    }

    private static void AddFastRouterAdditionalFormValues(
        MultipartFormDataContent form,
        Dictionary<string, JsonElement>? properties,
        params string[] reservedNames)
    {
        var reserved = new HashSet<string>(reservedNames, StringComparer.OrdinalIgnoreCase);
        foreach (var property in properties ?? [])
        {
            if (!reserved.Contains(property.Key))
                AddFastRouterFormValue(form, property.Key, property.Value);
        }
    }

    private static (byte[] Bytes, string MediaType) DecodeFastRouterData(object data, string fallbackMediaType)
    {
        var value = data switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement element => element.GetRawText().Trim('"'),
            _ => data?.ToString()
        };

        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Audio data is required.", nameof(data));

        var mediaType = fallbackMediaType;
        var base64 = value;
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = value.IndexOf(',');
            if (comma < 0)
                throw new ArgumentException("The data URL is invalid.", nameof(data));

            var metadata = value[5..comma];
            var semicolon = metadata.IndexOf(';');
            mediaType = semicolon >= 0 ? metadata[..semicolon] : metadata;
            base64 = value[(comma + 1)..];
        }

        try
        {
            return (Convert.FromBase64String(base64), string.IsNullOrWhiteSpace(mediaType) ? fallbackMediaType : mediaType);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("The supplied data is not valid base64.", nameof(data), ex);
        }
    }

    private static string ResolveFastRouterAudioMimeType(string? format)
        => format?.Trim().ToLowerInvariant() switch
        {
            "mp3" or "mpeg" => "audio/mpeg",
            "wav" => "audio/wav",
            "opus" => "audio/opus",
            "aac" => "audio/aac",
            "flac" => "audio/flac",
            "pcm" => "audio/pcm",
            _ => "audio/wav"
        };

    private static string ResolveFastRouterAudioFormat(string? requestedFormat, string mimeType)
        => !string.IsNullOrWhiteSpace(requestedFormat)
            ? requestedFormat
            : mimeType.Split('/').LastOrDefault() switch
            {
                "mpeg" => "mp3",
                var value when !string.IsNullOrWhiteSpace(value) => value,
                _ => "wav"
            };

    private sealed record FastRouterJsonResult(JsonElement Root, Dictionary<string, string> Headers);
}
