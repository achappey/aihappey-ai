using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.EmpirioLabsAI;

public partial class EmpirioLabsAIProvider
{
    private static readonly JsonSerializerOptions EmpirioMediaJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    private static JsonObject CreateEmpirioVercelPayload(
        Dictionary<string, JsonElement>? providerOptions,
        params string[] reservedNames)
    {
        var payload = new JsonObject();
        if (providerOptions is null
            || !providerOptions.TryGetValue("empiriolabsai", out var options)
            || options.ValueKind != JsonValueKind.Object)
            return payload;

        CopyEmpirioProperties(payload, options.EnumerateObject()
            .Select(property => new KeyValuePair<string, JsonElement>(property.Name, property.Value)), reservedNames);
        return payload;
    }

    private static JsonObject CreateEmpirioOpenAIPayload(
        Dictionary<string, JsonElement>? properties,
        params string[] reservedNames)
    {
        var payload = new JsonObject();
        CopyEmpirioProperties(payload, properties ?? [], reservedNames);
        return payload;
    }

    private static void CopyEmpirioProperties(
        JsonObject payload,
        IEnumerable<KeyValuePair<string, JsonElement>> properties,
        params string[] reservedNames)
    {
        var reserved = new HashSet<string>(reservedNames, StringComparer.OrdinalIgnoreCase);
        foreach (var property in properties)
            if (!reserved.Contains(property.Key))
                payload[property.Key] = JsonNode.Parse(property.Value.GetRawText());
    }

    private async Task<EmpirioJsonResult> SendEmpirioJsonAsync(
        HttpMethod method,
        string endpoint,
        JsonObject? payload,
        string operation,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(method, endpoint);
        if (payload is not null)
            request.Content = new StringContent(payload.ToJsonString(EmpirioMediaJson), Encoding.UTF8, MediaTypeNames.Application.Json);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"EmpirioLabs {operation} failed ({(int)response.StatusCode}): {raw}");

        try
        {
            using var document = JsonDocument.Parse(raw);
            return new EmpirioJsonResult(document.RootElement.Clone(), response.GetHeaders());
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"EmpirioLabs {operation} returned invalid JSON: {raw}", exception);
        }
    }

    private async Task<EmpirioJsonResult> SendEmpirioMultipartAsync(
        string endpoint,
        MultipartFormDataContent form,
        string operation,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var response = await _client.PostAsync(endpoint, form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"EmpirioLabs {operation} failed ({(int)response.StatusCode}): {raw}");
        try
        {
            using var document = JsonDocument.Parse(raw);
            return new EmpirioJsonResult(document.RootElement.Clone(), response.GetHeaders());
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"EmpirioLabs {operation} returned invalid JSON: {raw}", exception);
        }
    }

    private async Task<EmpirioJsonResult> AwaitEmpirioJobAsync(
        EmpirioJsonResult result,
        string operation,
        CancellationToken cancellationToken)
    {
        var jobId = GetEmpirioString(result.Root, "job_id");
        if (string.IsNullOrWhiteSpace(jobId)) return result;

        while (true)
        {
            var poll = await SendEmpirioJsonAsync(HttpMethod.Get,
                $"v1/jobs/{Uri.EscapeDataString(jobId)}", null, operation, cancellationToken);
            var status = GetEmpirioString(poll.Root, "status");
            if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"EmpirioLabs {operation} failed: {GetEmpirioError(poll.Root)}");
            if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase))
                return poll;
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private static JsonElement GetEmpirioPayloadRoot(JsonElement root)
    {
        foreach (var name in new[] { "result", "output", "data" })
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty(name, out var nested)
                && nested.ValueKind == JsonValueKind.Object)
                return nested;
        return root;
    }

    private async Task<(byte[] Bytes, string MediaType)> DownloadEmpirioMediaAsync(
        string value,
        string fallbackMediaType,
        CancellationToken cancellationToken)
    {
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = value.IndexOf(',');
            if (comma < 0) throw new InvalidOperationException("EmpirioLabs returned an invalid data URL.");
            var header = value[5..comma];
            var mediaType = header.Split(';')[0];
            return (Convert.FromBase64String(value[(comma + 1)..]), string.IsNullOrWhiteSpace(mediaType) ? fallbackMediaType : mediaType);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, value);
        using var response = await _downloadClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"EmpirioLabs media download failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(bytes)}");
        return (bytes, response.Content.Headers.ContentType?.MediaType ?? fallbackMediaType);
    }

    private static void AddEmpirioFormValue(MultipartFormDataContent form, string name, object? value)
    {
        if (value is null) return;
        if (value is JsonElement element)
        {
            if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return;
            value = element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
        }
        if (value is IEnumerable<string> values && value is not string)
        {
            foreach (var item in values) AddEmpirioFormValue(form, name, item);
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

    private static void AddEmpirioProviderFormValues(
        MultipartFormDataContent form,
        Dictionary<string, JsonElement>? providerOptions,
        params string[] reservedNames)
    {
        if (providerOptions is null
            || !providerOptions.TryGetValue("empiriolabsai", out var options)
            || options.ValueKind != JsonValueKind.Object) return;
        var reserved = new HashSet<string>(reservedNames, StringComparer.OrdinalIgnoreCase);
        foreach (var property in options.EnumerateObject())
            if (!reserved.Contains(property.Name)) AddEmpirioFormValue(form, property.Name, property.Value);
    }

    private static void AddEmpirioAdditionalFormValues(
        MultipartFormDataContent form,
        Dictionary<string, JsonElement>? properties,
        params string[] reservedNames)
    {
        var reserved = new HashSet<string>(reservedNames, StringComparer.OrdinalIgnoreCase);
        foreach (var property in properties ?? [])
            if (!reserved.Contains(property.Key)) AddEmpirioFormValue(form, property.Key, property.Value);
    }

    private static void AddEmpirioFile(MultipartFormDataContent form, Stream stream, string fileName, string? contentType)
    {
        var content = new StreamContent(stream);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType ?? MediaTypeNames.Application.Octet);
        form.Add(content, "file", fileName);
    }

    private static string? GetEmpirioString(JsonElement element, params string[] path)
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

    private static string GetEmpirioError(JsonElement root)
        => GetEmpirioString(root, "error", "message")
            ?? GetEmpirioString(root, "error")
            ?? GetEmpirioString(root, "message")
            ?? "The provider job failed.";

    private static void SetEmpirio(JsonObject payload, string name, object? value)
    {
        if (value is not null) payload[name] = JsonValue.Create(value);
    }

    private sealed record EmpirioJsonResult(JsonElement Root, Dictionary<string, string> Headers);
}
