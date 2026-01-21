using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.Freepik;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.Freepik;

public sealed partial class FreepikProvider
{
    private const string BaseUrl = "https://api.freepik.com";
    private const string TextToIconPath = "/v1/ai/text-to-icon";
    private const string TextToIconPreviewPath = "/v1/ai/text-to-icon/preview";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> IconGenerationImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(imageRequest);
        if (string.IsNullOrWhiteSpace(imageRequest.Model))
            throw new ArgumentException("Model is required.", nameof(imageRequest));
        if (string.IsNullOrWhiteSpace(imageRequest.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(imageRequest));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (imageRequest.Files?.Any() == true)
        {
            warnings.Add(new { type = "unsupported", feature = "files" });
        }
        if (imageRequest.Mask is not null)
        {
            warnings.Add(new { type = "unsupported", feature = "mask" });
        }
        if (imageRequest.Seed is not null)
        {
            warnings.Add(new { type = "unsupported", feature = "seed" });
        }
        if (imageRequest.N is not null && imageRequest.N.Value != 1)
        {
            warnings.Add(new { type = "unsupported", feature = "n", details = "Freepik text-to-icon currently supports single result per request." });
        }

        var isPreview = imageRequest.Model.Equals("text-to-icon/preview", StringComparison.OrdinalIgnoreCase);
        var endpoint = isPreview ? TextToIconPreviewPath : TextToIconPath;

        if (!isPreview && !imageRequest.Model.Equals("text-to-icon", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Freepik image model '{imageRequest.Model}' is not supported.");

        var metadata = imageRequest.GetImageProviderMetadata<FreepikImageProviderMetadata>(GetIdentifier());
        var icon = metadata?.IconGeneration;

        // Preview endpoint ignores output format per user instruction.
        var requestedFormat = isPreview ? "png" : NormalizeFormat(icon?.Format) ?? "png";
        if (isPreview && !string.IsNullOrWhiteSpace(icon?.Format))
        {
            warnings.Add(new { type = "compatibility", feature = "format", details = "Freepik preview endpoint ignores output format; using png." });
        }

        // 1) Start async generation.
        var startPayload = BuildTextToIconPayload(imageRequest.Prompt, icon, includeFormat: !isPreview);
        var startJson = JsonSerializer.Serialize(startPayload, JsonOpts);

        using var startReq = new HttpRequestMessage(HttpMethod.Post, BaseUrl + endpoint)
        {
            Content = new StringContent(startJson, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var startResp = await _client.SendAsync(startReq, cancellationToken);
        var startRaw = await startResp.Content.ReadAsStringAsync(cancellationToken);
        if (!startResp.IsSuccessStatusCode)
            throw new Exception($"Freepik start error: {(int)startResp.StatusCode} {startResp.ReasonPhrase}: {startRaw}");

        using var startDoc = JsonDocument.Parse(startRaw);
        var taskId = startDoc.RootElement.GetProperty("data").GetProperty("task_id").GetString();
        if (string.IsNullOrWhiteSpace(taskId))
            throw new Exception("Freepik response missing data.task_id");

        // 2) Poll via render endpoint until COMPLETED/FAILED.
        var interval = TimeSpan.FromMilliseconds(800);
        var timeout = TimeSpan.FromSeconds(60);

        var finalJson = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: ct => RenderPollAsync(taskId, requestedFormat, ct),
            isTerminal: r => r.Status is "COMPLETED" or "FAILED",
            interval: interval,
            timeout: timeout,
            maxAttempts: null,
            cancellationToken: cancellationToken);

        if (finalJson.Status == "FAILED")
            throw new Exception($"Freepik task failed (task_id={taskId}).");

        var firstUrl = finalJson.Generated?.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstUrl))
            throw new Exception($"Freepik task completed but returned no generated URLs (task_id={taskId}).");

        // 3) Download final asset.
        using var fileResp = await _client.GetAsync(firstUrl, cancellationToken);
        var fileBytes = await fileResp.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!fileResp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(fileBytes);
            throw new Exception($"Freepik download error: {(int)fileResp.StatusCode} {fileResp.ReasonPhrase}: {err}");
        }

        var mime = requestedFormat.Equals("svg", StringComparison.OrdinalIgnoreCase)
            ? "image/svg+xml"
            : "image/png";

        var dataUrl = $"data:{mime};base64,{Convert.ToBase64String(fileBytes)}";

        using var finalDoc = JsonDocument.Parse(finalJson.Raw);

        return new ImageResponse
        {
            Images = [dataUrl],
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = imageRequest.Model,
                Body = finalDoc.RootElement.Clone()
            },
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                ["freepik"] = JsonSerializer.SerializeToElement(new
                {
                    task_id = taskId,
                    status = finalJson.Status,
                    generated = finalJson.Generated
                }, JsonSerializerOptions.Web)
            }
        };
    }

    private static Dictionary<string, object?> BuildTextToIconPayload(
        string prompt,
        IconGeneration? icon,
        bool includeFormat)
    {
        var payload = new Dictionary<string, object?>
        {
            ["prompt"] = prompt,
            // webhook_url intentionally omitted (per user instruction: no webhooks)
        };

        // style
        if (!string.IsNullOrWhiteSpace(icon?.Style))
            payload["style"] = icon!.Style;

        // num_inference_steps (10-50)
        if (icon?.NumInferenceSteps is { } steps && steps >= 10 && steps <= 50)
            payload["num_inference_steps"] = steps;

        // guidance_scale (0-10)
        if (icon?.GuidanceScale is { } gs && gs >= 0 && gs <= 10)
            payload["guidance_scale"] = gs;

        if (includeFormat)
        {
            var format = NormalizeFormat(icon?.Format);
            if (format is not null)
                payload["format"] = format;
        }

        return payload;
    }

    private static string? NormalizeFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return null;

        var f = format.Trim().ToLowerInvariant();
        return f is "png" or "svg" ? f : null;
    }

    private sealed record FreepikRenderResult(string Status, List<string>? Generated, string Raw);

    private async Task<FreepikRenderResult> RenderPollAsync(string taskId, string format, CancellationToken cancellationToken)
    {
        // Docs: POST /v1/ai/text-to-icon/{task-id}/render/{format}
        var url = $"{BaseUrl}{TextToIconPath}/{taskId}/render/{format}";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);

        using var resp = await _client.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Freepik render error: {(int)resp.StatusCode} {resp.ReasonPhrase}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        var data = doc.RootElement.GetProperty("data");
        var status = data.GetProperty("status").GetString() ?? "UNKNOWN";

        List<string>? generated = null;
        if (data.TryGetProperty("generated", out var genEl) && genEl.ValueKind == JsonValueKind.Array)
        {
            generated = [.. genEl.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .Where(s => !string.IsNullOrWhiteSpace(s))];
        }

        return new FreepikRenderResult(status, generated, raw);
    }
}

