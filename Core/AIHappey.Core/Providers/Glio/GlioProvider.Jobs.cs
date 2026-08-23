using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.Glio;

public partial class GlioProvider
{
    private static readonly JsonSerializerOptions GlioJsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record GlioJobResult(
        string JobId,
        JsonElement Create,
        JsonElement Final,
        Dictionary<string, string> Headers,
        IReadOnlyList<string> Urls,
        JsonElement? Delete);

    private async Task<GlioJobResult> RunGlioJobAsync(
        Dictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "v1/jobs")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, GlioJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
        using var createResponse = await _client.SendAsync(createRequest, cancellationToken);
        var create = await ReadGlioJsonAsync(createResponse, "job creation", cancellationToken);
        var jobId = TryGetGlioString(create, "id")
            ?? throw new InvalidOperationException("Glio job creation returned no id.");

        var final = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: token => FetchGlioJobAsync(jobId, token),
            isTerminal: IsGlioTerminalJob,
            interval: TimeSpan.FromSeconds(3),
            timeout: TimeSpan.FromMinutes(30),
            maxAttempts: null,
            cancellationToken: cancellationToken);

        var status = TryGetGlioString(final, "status") ?? "unknown";
        if (!string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Glio job '{jobId}' failed with status '{status}': {GetGlioFailure(final)}");

        var urls = ExtractGlioResultUrls(final);
        if (urls.Count == 0)
            throw new InvalidOperationException($"Glio job '{jobId}' completed without output URLs.");

        return new GlioJobResult(jobId, create, final, createResponse.GetHeaders(), urls, null);
    }

    private async Task<JsonElement> FetchGlioJobAsync(string jobId, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync($"v1/jobs/{Uri.EscapeDataString(jobId)}", cancellationToken);
        return await ReadGlioJsonAsync(response, "job status", cancellationToken);
    }

    private async Task<JsonElement?> DeleteGlioJobAsync(string jobId, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"v1/jobs/{Uri.EscapeDataString(jobId)}");
            using var response = await _client.SendAsync(request, cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(raw))
                return JsonSerializer.SerializeToElement(new
                {
                    succeeded = response.IsSuccessStatusCode,
                    statusCode = (int)response.StatusCode,
                    error = raw
                }, GlioJsonOptions);

            using var document = JsonDocument.Parse(raw);
            return document.RootElement.Clone();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return JsonSerializer.SerializeToElement(new { succeeded = false, error = exception.Message }, GlioJsonOptions);
        }
    }

    private static async Task<JsonElement> ReadGlioJsonAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Glio {operation} failed ({(int)response.StatusCode}): {raw}");
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException($"Glio {operation} returned an empty response.");

        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private static bool IsGlioTerminalJob(JsonElement root)
    {
        var status = TryGetGlioString(root, "status");
        return string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> ExtractGlioResultUrls(JsonElement root)
    {
        var urls = new List<string>();
        foreach (var name in new[] { "final_result", "result" })
        {
            if (root.TryGetProperty(name, out var result))
                ExtractGlioResultUrls(result, urls);
        }

        return urls;
    }

    private static void ExtractGlioResultUrls(JsonElement result, List<string> urls)
    {
        if (result.ValueKind == JsonValueKind.String)
        {
            AddGlioUrl(urls, result.GetString());
            return;
        }

        if (result.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in result.EnumerateArray())
                ExtractGlioResultUrls(item, urls);
            return;
        }

        if (result.ValueKind != JsonValueKind.Object)
            return;

        AddGlioUrl(urls, TryGetGlioString(result, "audio_url"));
        AddGlioUrl(urls, TryGetGlioString(result, "audioUrl"));
        AddGlioUrl(urls, TryGetGlioString(result, "video_url"));
        AddGlioUrl(urls, TryGetGlioString(result, "videoUrl"));
        AddGlioUrl(urls, TryGetGlioString(result, "image_url"));
        AddGlioUrl(urls, TryGetGlioString(result, "imageUrl"));
        AddGlioUrl(urls, TryGetGlioString(result, "url"));

        foreach (var name in new[] { "urls", "tracks", "audios", "videos", "images", "outputs", "files" })
        {
            if (result.TryGetProperty(name, out var children))
                ExtractGlioResultUrls(children, urls);
        }
    }

    private static void AddGlioUrl(List<string> urls, string? url)
    {
        if (!string.IsNullOrWhiteSpace(url) && !urls.Contains(url, StringComparer.Ordinal))
            urls.Add(url);
    }

    private async Task<(byte[] Bytes, string MediaType)> DownloadGlioMediaAsync(
        string url,
        string fallbackMediaType,
        CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(url, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode || bytes.Length == 0)
            throw new InvalidOperationException($"Glio output download failed ({(int)response.StatusCode}) for '{url}'.");

        return (bytes, response.Content.Headers.ContentType?.MediaType ?? fallbackMediaType);
    }

    private static Dictionary<string, object?> CopyGlioRootOptions(JsonElement options)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (options.ValueKind != JsonValueKind.Object)
            return payload;

        foreach (var property in options.EnumerateObject())
            payload[property.Name] = property.Value.Clone();
        return payload;
    }

    private static Dictionary<string, object?> GetGlioParams(Dictionary<string, object?> payload)
    {
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (payload.TryGetValue("params", out var value))
        {
            var element = value is JsonElement json
                ? json
                : JsonSerializer.SerializeToElement(value, GlioJsonOptions);
            if (element.ValueKind == JsonValueKind.Object)
                foreach (var property in element.EnumerateObject())
                    parameters[property.Name] = property.Value.Clone();
        }

        payload["params"] = parameters;
        return parameters;
    }

    private static string ToGlioDataUrl(string data, string? mediaType)
    {
        if (data.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            || data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return data;
        return $"data:{(string.IsNullOrWhiteSpace(mediaType) ? MediaTypeNames.Image.Png : mediaType)};base64,{data}";
    }

    private static string? TryGetGlioString(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;

    private static string GetGlioFailure(JsonElement root)
    {
        foreach (var name in new[] { "error", "detail", "message", "reason" })
        {
            if (!root.TryGetProperty(name, out var value))
                continue;
            return value.ValueKind == JsonValueKind.String ? value.GetString() ?? value.GetRawText() : value.GetRawText();
        }
        return root.GetRawText();
    }

    private Dictionary<string, JsonElement> CreateGlioJobMetadata(GlioJobResult job)
        => GetIdentifier().CreatePrimitiveProviderMetadata(new
        {
            jobId = job.JobId,
            create = job.Create,
            final = job.Final,
            outputUrls = job.Urls,
            delete = job.Delete
        });
}
