using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.SandBase;

public partial class SandBaseProvider
{
    private static readonly TimeSpan RunPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RunPollTimeout = TimeSpan.FromMinutes(10);

    // Media downloads must never inherit the SandBase bearer token.
    private HttpClient _mediaClient = null!;

    private sealed record SandBaseRun(JsonElement Root, Dictionary<string, string> Headers);

    private Dictionary<string, object?> CreateRunPayload(Dictionary<string, JsonElement>? options, string model)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (options?.TryGetValue(GetIdentifier(), out var raw) == true && raw.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            if (raw.ValueKind != JsonValueKind.Object)
                throw new ArgumentException($"providerOptions.{GetIdentifier()} must be a JSON object.");
            foreach (var property in raw.EnumerateObject())
                payload[property.Name] = property.Value.Clone();
        }

        // A public URL or an already registered asset:// reference is accepted as-is.
        // /v1/assets cannot upload bytes: it only registers publicly accessible URLs.
        ValidateRunInputs(payload);
        payload["model"] = model;
        return payload;
    }

    private static void ValidateRunInputs(object? value)
    {
        if (value is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.String && element.GetString()!.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("SandBase media inputs require public http(s) URLs or existing asset:// references; inline/base64 data URLs are not supported.");
            if (element.ValueKind == JsonValueKind.Object)
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name is "b64_json" or "base64" or "audio_base64")
                        throw new ArgumentException("SandBase media inputs cannot contain inline base64; supply a public URL or existing asset:// reference.");
                    ValidateRunInputs(property.Value);
                }
            if (element.ValueKind == JsonValueKind.Array)
                foreach (var item in element.EnumerateArray()) ValidateRunInputs(item);
        }
    }

    private static string RequireMediaReference(string? value, string field)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme is "http" or "https" || uri.Scheme == "asset"))
            return value!;
        throw new ArgumentException($"SandBase {field} requires a public http(s) URL or existing asset:// reference. Inline/base64 uploads are not supported.");
    }

    private async Task<SandBaseRun> SendRunAsync(HttpMethod method, string path, object? payload, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(method, path);
        if (payload is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, MediaTypeNames.Application.Json);
        using var response = await _client.SendAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"SandBase run request failed ({(int)response.StatusCode}): {raw}");
        using var document = JsonDocument.Parse(raw);
        return new SandBaseRun(document.RootElement.Clone(), response.GetHeaders());
    }

    private Task<SandBaseRun> SubmitRunAsync(Dictionary<string, object?> payload, CancellationToken cancellationToken)
        => SendRunAsync(HttpMethod.Post, "v1/run", payload, cancellationToken);

    private Task<SandBaseRun> PollRunAsync(string id, CancellationToken cancellationToken)
        => SendRunAsync(HttpMethod.Get, $"v1/run/{Uri.EscapeDataString(id)}", null, cancellationToken);

    private async Task<SandBaseRun> AwaitRunAsync(SandBaseRun run, CancellationToken cancellationToken)
    {
        var status = RunStatus(run.Root);
        if (status is "completed" or "failed" or "timeout") return RequireCompletedRun(run);
        var id = RunString(run.Root, "id")
            ?? throw new InvalidOperationException("SandBase run response did not include an opaque id for polling.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RunPollTimeout);
        try
        {
            while (true)
            {
                await Task.Delay(RunPollInterval, timeout.Token);
                run = await PollRunAsync(id, timeout.Token);
                status = RunStatus(run.Root);
                if (status is "completed" or "failed" or "timeout") return RequireCompletedRun(run);
                if (status is not ("pending" or "running"))
                    throw new InvalidOperationException($"SandBase returned an unknown run status: {status ?? "(missing)"}.");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"SandBase run '{id}' did not finish within {RunPollTimeout}.");
        }
    }

    private static SandBaseRun RequireCompletedRun(SandBaseRun run)
    {
        if (RunStatus(run.Root) != "completed")
            throw new InvalidOperationException($"SandBase run {RunStatus(run.Root) ?? "(missing)"}: {RunError(run.Root)}");
        return run;
    }

    private static string? RunString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static string? RunStatus(JsonElement root) => RunString(root, "status")?.ToLowerInvariant();

    private static string RunError(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error))
            return RunString(error, "message") ?? (error.ValueKind == JsonValueKind.String ? error.GetString() : error.GetRawText()) ?? "Unknown error";
        return "Unknown error";
    }

    private Dictionary<string, JsonElement> RunMetadata(JsonElement root)
        => GetIdentifier().CreatePrimitiveProviderMetadata(root);

    private static IEnumerable<JsonElement> RunOutputs(JsonElement root)
    {
        if (!root.TryGetProperty("outputs", out var outputs)) yield break;
        if (outputs.ValueKind == JsonValueKind.Array)
            foreach (var output in outputs.EnumerateArray()) yield return output;
        else if (outputs.ValueKind is JsonValueKind.Object or JsonValueKind.String)
            yield return outputs;
    }

    private static string? FindMedia(JsonElement output, string kind)
    {
        if (output.ValueKind == JsonValueKind.String) return output.GetString();
        if (output.ValueKind != JsonValueKind.Object) return null;
        foreach (var field in new[] { $"{kind}_url", "output_url", "url", kind, "data" })
        {
            if (!output.TryGetProperty(field, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String) return value.GetString();
            if (value.ValueKind == JsonValueKind.Object)
            {
                var nested = FindMedia(value, kind);
                if (nested is not null) return nested;
            }
        }
        return null;
    }

    private async Task<(byte[] Bytes, string MimeType)> ReadRunMediaAsync(string url, string fallbackMimeType, CancellationToken cancellationToken)
    {
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = url.IndexOf(',');
            if (comma < 0 || !url[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SandBase returned an invalid media data URL.");
            var mime = url[5..url.IndexOf(';')];
            return (Convert.FromBase64String(url[(comma + 1)..]), mime);
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("SandBase returned a media output that is not a downloadable http(s) URL.");
        using var response = await _mediaClient.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var mimeType = response.Content.Headers.ContentType?.MediaType;
        return (bytes, string.IsNullOrWhiteSpace(mimeType) || mimeType == MediaTypeNames.Application.Octet
            ? MimeFromUrl(uri.AbsolutePath, fallbackMimeType) : mimeType);
    }

    private static string MimeFromUrl(string path, string fallback) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp",
        ".mp3" => "audio/mpeg", ".wav" => "audio/wav", ".ogg" => "audio/ogg", ".flac" => "audio/flac",
        ".webm" => "video/webm", ".mov" => "video/quicktime", ".mp4" => "video/mp4", _ => fallback
    };
}
