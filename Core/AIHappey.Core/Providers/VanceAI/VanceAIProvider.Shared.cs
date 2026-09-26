using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.VanceAI;

public partial class VanceAIProvider
{
    private static readonly JsonSerializerOptions VanceAIJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly HashSet<string> VanceAITransportOptionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "config",
        "output_format",
        "outputFormat",
        "image_url",
        "imageUrl",
        "source_url",
        "sourceUrl",
        "upload_id",
        "uploadId",
        "file_name",
        "fileName",
        "content_type",
        "contentType"
    };

    private sealed record VanceAIJob(
        string JobId,
        string Status,
        string? Phase,
        int? Progress,
        string? Tool,
        string? Media,
        JsonElement? Error,
        JsonElement Root,
        Dictionary<string, string> Headers);

    private sealed record VanceAIResult(byte[] Bytes, string MediaType, Dictionary<string, string> Headers);

    private string NormalizeVanceAIModel(string? model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var prefix = GetIdentifier() + "/";
        return model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? model[prefix.Length..]
            : model;
    }

    private JsonElement? GetVanceAIOptions(Dictionary<string, JsonElement>? providerOptions)
    {
        if (providerOptions is null)
            return null;

        return providerOptions.TryGetValue(GetIdentifier(), out var options)
               && options.ValueKind == JsonValueKind.Object
            ? options
            : null;
    }

    private Dictionary<string, JsonElement> BuildVanceAIConfig(
        Dictionary<string, JsonElement>? providerOptions)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var options = GetVanceAIOptions(providerOptions);
        if (options is not { ValueKind: JsonValueKind.Object })
            return result;

        if (options.Value.TryGetProperty("config", out var nestedConfig)
            && nestedConfig.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in nestedConfig.EnumerateObject())
                result[property.Name] = property.Value.Clone();
        }

        foreach (var property in options.Value.EnumerateObject())
        {
            if (!VanceAITransportOptionNames.Contains(property.Name))
                result[property.Name] = property.Value.Clone();
        }

        return result;
    }

    private string? ReadVanceAIOptionString(
        Dictionary<string, JsonElement>? providerOptions,
        params string[] names)
    {
        var options = GetVanceAIOptions(providerOptions);
        if (options is not { ValueKind: JsonValueKind.Object })
            return null;

        foreach (var property in options.Value.EnumerateObject())
        {
            if (!names.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                continue;

            return property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()
                : property.Value.ToString();
        }

        return null;
    }

    private async Task<VanceAIJob> SendVanceAIJobRequestAsync(
        HttpRequestMessage request,
        string operation,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var response = await _client.SendAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw CreateVanceAIException(operation, response.StatusCode, raw);

        return ParseVanceAIJob(raw, response.GetHeaders(), operation);
    }

    private async Task<VanceAIJob> GetVanceAIJobAsync(string jobId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"v1/jobs/{Uri.EscapeDataString(jobId)}");
        return await SendVanceAIJobRequestAsync(request, "retrieve job", cancellationToken);
    }

    private async Task<VanceAIResult> DownloadVanceAIResultAsync(
        string jobId,
        string fallbackMediaType,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            ApplyAuthHeader();
            using var request = new HttpRequestMessage(HttpMethod.Get, $"v1/jobs/{Uri.EscapeDataString(jobId)}/result");
            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (response.StatusCode == HttpStatusCode.Accepted)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                continue;
            }

            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect
                or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect
                or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location
                    ?? throw new InvalidOperationException("VanceAI result redirect did not include a location.");
                using var redirected = await _transferClient.GetAsync(location, cancellationToken);
                var redirectedBytes = await redirected.Content.ReadAsByteArrayAsync(cancellationToken);
                if (!redirected.IsSuccessStatusCode)
                    throw CreateVanceAIException(
                        "download result",
                        redirected.StatusCode,
                        Encoding.UTF8.GetString(redirectedBytes));

                return new VanceAIResult(
                    redirectedBytes,
                    redirected.Content.Headers.ContentType?.MediaType ?? fallbackMediaType,
                    redirected.GetHeaders());
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw CreateVanceAIException("download result", response.StatusCode, Encoding.UTF8.GetString(bytes));

            return new VanceAIResult(
                bytes,
                response.Content.Headers.ContentType?.MediaType ?? fallbackMediaType,
                response.GetHeaders());
        }
    }

    private static VanceAIJob ParseVanceAIJob(
        string raw,
        Dictionary<string, string> headers,
        string operation)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException($"VanceAI {operation} returned an empty response.");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var jobId = ReadVanceAIString(root, "job_id")
            ?? throw new InvalidOperationException($"VanceAI {operation} returned no job_id.");
        var status = ReadVanceAIString(root, "status") ?? "unknown";
        int? progress = root.TryGetProperty("progress", out var progressElement)
                       && progressElement.TryGetInt32(out var progressValue)
            ? progressValue
            : null;
        JsonElement? error = root.TryGetProperty("error", out var errorElement)
                             && errorElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
            ? errorElement.Clone()
            : null;

        return new VanceAIJob(
            jobId,
            status,
            ReadVanceAIString(root, "phase"),
            progress,
            ReadVanceAIString(root, "tool"),
            ReadVanceAIString(root, "media"),
            error,
            root,
            headers);
    }

    private static InvalidOperationException CreateVanceAIException(
        string operation,
        HttpStatusCode statusCode,
        string raw)
    {
        var detail = TryReadVanceAIError(raw);
        return new InvalidOperationException(
            $"VanceAI {operation} failed ({(int)statusCode} {statusCode}){(string.IsNullOrWhiteSpace(detail) ? "." : $": {detail}")}");
    }

    private static string? TryReadVanceAIError(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                    return error.GetString();
                if (error.ValueKind == JsonValueKind.Object)
                {
                    var code = ReadVanceAIString(error, "code");
                    var message = ReadVanceAIString(error, "message");
                    return string.Join(": ", new[] { code, message }.Where(value => !string.IsNullOrWhiteSpace(value))!);
                }
            }
        }
        catch (JsonException)
        {
            // Return the provider's non-JSON response below.
        }

        return raw.Trim();
    }

    private static string? ReadVanceAIString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string ReadVanceAIJobError(VanceAIJob job)
    {
        if (job.Error is { ValueKind: JsonValueKind.Object } error)
        {
            var code = ReadVanceAIString(error, "code");
            var message = ReadVanceAIString(error, "message");
            var detail = string.Join(": ", new[] { code, message }.Where(value => !string.IsNullOrWhiteSpace(value))!);
            if (!string.IsNullOrWhiteSpace(detail))
                return detail;
        }

        return $"VanceAI job '{job.JobId}' ended with status '{job.Status}'.";
    }

    private static bool IsHttpUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
               || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));

    private static byte[] DecodeMediaData(string data, out string? dataUrlMediaType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(data);
        dataUrlMediaType = null;
        var payload = data.Trim();
        if (payload.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var marker = payload.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
                throw new ArgumentException("VanceAI input data URLs must be base64 encoded.", nameof(data));
            dataUrlMediaType = payload["data:".Length..marker];
            payload = payload[(marker + ";base64,".Length)..];
        }

        try
        {
            return Convert.FromBase64String(payload);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("VanceAI input media must be an HTTP(S) URL, raw base64, or a base64 data URL.", nameof(data), exception);
        }
    }

    private static string GuessExtension(string mediaType)
        => mediaType.ToLowerInvariant() switch
        {
            "video/webm" => ".webm",
            "video/quicktime" => ".mov",
            "video/x-matroska" => ".mkv",
            "video/x-msvideo" => ".avi",
            _ => ".mp4"
        };

    private static string EncodeVanceAIOperation(string jobId, string model)
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(model))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return $"{jobId}.{payload}";
    }

    private static (string JobId, string Model) DecodeVanceAIOperation(string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        var separator = operation.LastIndexOf('.');
        if (separator <= 0 || separator == operation.Length - 1)
            throw new ArgumentException("The VanceAI video operation is not a valid model-aware token.", nameof(operation));

        var payload = operation[(separator + 1)..].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
        try
        {
            return (operation[..separator], Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("The VanceAI video operation is not a valid model-aware token.", nameof(operation), exception);
        }
    }
}
