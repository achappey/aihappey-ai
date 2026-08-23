using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.Zenlayer;

public partial class ZenlayerProvider
{
    private static readonly JsonSerializerOptions MediaJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static JsonObject CreateVercelPayload(
        Dictionary<string, JsonElement>? providerOptions,
        string providerId,
        params string[] reservedNames)
    {
        var payload = new JsonObject();
        if (providerOptions is null
            || !providerOptions.TryGetValue(providerId, out var raw)
            || raw.ValueKind != JsonValueKind.Object)
            return payload;

        CopyRawProperties(payload, raw.EnumerateObject().Select(property =>
            new KeyValuePair<string, JsonElement>(property.Name, property.Value)), reservedNames);
        return payload;
    }

    private static JsonObject CreateOpenAIPayload(
        Dictionary<string, JsonElement>? additionalProperties,
        params string[] reservedNames)
    {
        var payload = new JsonObject();
        CopyRawProperties(payload, additionalProperties ?? [], reservedNames);
        return payload;
    }

    private static void CopyRawProperties(
        JsonObject payload,
        IEnumerable<KeyValuePair<string, JsonElement>> properties,
        params string[] reservedNames)
    {
        var reserved = new HashSet<string>(reservedNames, StringComparer.OrdinalIgnoreCase);
        foreach (var property in properties)
            if (!reserved.Contains(property.Key))
                payload[property.Key] = JsonNode.Parse(property.Value.GetRawText());
    }

    private async Task<ZenlayerJsonResult> SendJsonAsync(
        HttpMethod method,
        string endpoint,
        JsonObject? payload,
        string operation,
        CancellationToken cancellationToken,
        bool googleApiKey = false)
    {
        using var request = new HttpRequestMessage(method, endpoint);
        if (payload is not null)
            request.Content = new StringContent(payload.ToJsonString(MediaJson), Encoding.UTF8, MediaTypeNames.Application.Json);

        if (googleApiKey)
        {
            var key = _keyResolver.Resolve(GetIdentifier());
            if (string.IsNullOrWhiteSpace(key))
                throw new InvalidOperationException($"No {nameof(Zenlayer)} API key.");
            request.Headers.TryAddWithoutValidation("x-goog-api-key", key);
        }
        else
        {
            ApplyAuthHeader();
        }

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Zenlayer {operation} failed ({(int)response.StatusCode}): {raw}");

        try
        {
            using var document = JsonDocument.Parse(raw);
            return new ZenlayerJsonResult(document.RootElement.Clone(), response.GetHeaders());
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Zenlayer {operation} returned invalid JSON: {raw}", exception);
        }
    }

    private async Task<ZenlayerJsonResult> SendMultipartJsonAsync(
        string endpoint,
        MultipartFormDataContent form,
        string operation,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var response = await _client.PostAsync(endpoint, form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Zenlayer {operation} failed ({(int)response.StatusCode}): {raw}");

        try
        {
            using var document = JsonDocument.Parse(raw);
            return new ZenlayerJsonResult(document.RootElement.Clone(), response.GetHeaders());
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Zenlayer {operation} returned invalid JSON: {raw}", exception);
        }
    }

    private static void AddFormValue(MultipartFormDataContent form, string name, object? value)
    {
        if (value is null) return;
        if (value is JsonElement element)
        {
            if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return;
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray()) AddFormValue(form, name.EndsWith("[]") ? name : name + "[]", item);
                return;
            }
            value = element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
        }
        if (value is IEnumerable<string> values && value is not string)
        {
            foreach (var item in values) AddFormValue(form, name.EndsWith("[]") ? name : name + "[]", item);
            return;
        }
        var text = value switch
        {
            bool boolean => boolean ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        };
        if (text is not null) form.Add(new StringContent(text), name);
    }

    private static void AddAdditionalFormValues(
        MultipartFormDataContent form,
        Dictionary<string, JsonElement>? properties,
        params string[] reservedNames)
    {
        var reserved = new HashSet<string>(reservedNames, StringComparer.OrdinalIgnoreCase);
        foreach (var property in properties ?? [])
            if (!reserved.Contains(property.Key)) AddFormValue(form, property.Key, property.Value);
    }

    private static void AddProviderFormValues(
        MultipartFormDataContent form,
        Dictionary<string, JsonElement>? providerOptions,
        string providerId,
        params string[] reservedNames)
    {
        if (providerOptions is null
            || !providerOptions.TryGetValue(providerId, out var raw)
            || raw.ValueKind != JsonValueKind.Object) return;
        var reserved = new HashSet<string>(reservedNames, StringComparer.OrdinalIgnoreCase);
        foreach (var property in raw.EnumerateObject())
            if (!reserved.Contains(property.Name)) AddFormValue(form, property.Name, property.Value);
    }

    private static void AddFile(MultipartFormDataContent form, string name, Stream stream, string fileName, string? contentType)
    {
        var content = new StreamContent(stream);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType ?? MediaTypeNames.Application.Octet);
        form.Add(content, name, fileName);
    }

    private async Task<(byte[] Bytes, string MediaType)> DownloadAsync(
        string url,
        CancellationToken cancellationToken,
        bool googleApiKey = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (googleApiKey)
        {
            var key = _keyResolver.Resolve(GetIdentifier());
            if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException($"No {nameof(Zenlayer)} API key.");
            request.Headers.TryAddWithoutValidation("x-goog-api-key", key);
        }
        else ApplyAuthHeader();
        using var response = await _client.SendAsync(request, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Zenlayer media download failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(bytes)}");
        return (bytes, response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream");
    }

    private static string? GetString(JsonElement element, params string[] path)
    {
        foreach (var segment in path)
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(segment, out element)) return null;
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.ToString(),
            _ => null
        };
    }

    private sealed record ZenlayerJsonResult(JsonElement Root, Dictionary<string, string> Headers);
}
