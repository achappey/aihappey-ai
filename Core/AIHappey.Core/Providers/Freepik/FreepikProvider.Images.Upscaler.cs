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
    private const string ImageUpscalerCreativePath = "/v1/ai/image-upscaler";
    private const string ImageUpscalerPrecisionV1Path = "/v1/ai/image-upscaler-precision";
    private const string ImageUpscalerPrecisionV2Path = "/v1/ai/image-upscaler-precision-v2";

    private sealed record FreepikUpscalerTaskResult(string Status, List<string>? Generated, string Raw, string TaskId);

    public async Task<ImageResponse> UpscalerImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(imageRequest);
        if (string.IsNullOrWhiteSpace(imageRequest.Model))
            throw new ArgumentException("Model is required.", nameof(imageRequest));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        var (path, mode) = imageRequest.Model.ToLowerInvariant() switch
        {
            "image-upscaler" => (ImageUpscalerCreativePath, "creative"),
            "image-upscaler-precision" => (ImageUpscalerPrecisionV1Path, "precision"),
            "image-upscaler-precision-v2" => (ImageUpscalerPrecisionV2Path, "precision_v2"),
            _ => throw new NotSupportedException($"Freepik image model '{imageRequest.Model}' is not supported.")
        };

        // Compatibility warnings (match style of other Freepik handlers)
        if (imageRequest.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });
        if (imageRequest.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });
        if (!string.IsNullOrWhiteSpace(imageRequest.Size))
            warnings.Add(new { type = "unsupported", feature = "size" });
        if (!string.IsNullOrWhiteSpace(imageRequest.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspect_ratio" });
        if (imageRequest.N is not null && imageRequest.N.Value != 1)
            warnings.Add(new { type = "unsupported", feature = "n", details = "Freepik upscaler returns one image per request; generated a single image." });

        // Input image is required.
        var files = imageRequest.Files?.ToList();
        if (files is null || files.Count == 0)
            throw new ArgumentException("At least one input image is required in 'files'.", nameof(imageRequest));
        if (files.Count > 1)
            warnings.Add(new { type = "unsupported", feature = "files", details = "Freepik upscaler supports a single input image; extra images were ignored." });

        var firstFile = files[0];
        if (string.IsNullOrWhiteSpace(firstFile?.Data))
            throw new ArgumentException("files[0] must include 'data' (base64).", nameof(imageRequest));

        // Docs require raw base64 for Creative + Precision V1. Precision V2 accepts URL or base64.
        // We accept whatever the caller provides, including data URLs, and let upstream validate.
        var imageField = firstFile.Data;

        var metadata = imageRequest.GetImageProviderMetadata<FreepikImageProviderMetadata>(GetIdentifier());
        var upscaler = metadata?.Upscaler;

        var startPayload = mode switch
        {
            "creative" => BuildUpscalerCreativePayload(imageField, imageRequest.Prompt, upscaler?.Creative, warnings),
            "precision" => BuildUpscalerPrecisionV1Payload(imageField, upscaler?.Precision, warnings),
            "precision_v2" => BuildUpscalerPrecisionV2Payload(imageField, upscaler?.PrecisionV2, warnings),
            _ => throw new NotSupportedException($"Unsupported upscaler mode '{mode}'.")
        };

        var startJson = JsonSerializer.Serialize(startPayload, JsonOpts);
        using var startReq = new HttpRequestMessage(HttpMethod.Post, BaseUrl + path)
        {
            Content = new StringContent(startJson, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var startResp = await _client.SendAsync(startReq, cancellationToken);
        var startRaw = await startResp.Content.ReadAsStringAsync(cancellationToken);
        if (!startResp.IsSuccessStatusCode)
            throw new Exception($"Freepik {imageRequest.Model} start error: {(int)startResp.StatusCode} {startResp.ReasonPhrase}: {startRaw}");

        using var startDoc = JsonDocument.Parse(startRaw);
        var taskId = startDoc.RootElement.GetProperty("data").GetProperty("task_id").GetString();
        if (string.IsNullOrWhiteSpace(taskId))
            throw new Exception("Freepik response missing data.task_id");

        // Poll GET {path}/{task-id} until COMPLETED/FAILED.
        var final = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: ct => UpscalerPollAsync(path, taskId, ct),
            isTerminal: r => r.Status is "COMPLETED" or "FAILED",
            interval: TimeSpan.FromMilliseconds(800),
            timeout: TimeSpan.FromSeconds(90),
            maxAttempts: null,
            cancellationToken: cancellationToken);

        if (final.Status == "FAILED")
            throw new Exception($"Freepik {imageRequest.Model} task failed (task_id={taskId}).");

        var firstUrl = final.Generated?.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstUrl))
            throw new Exception($"Freepik {imageRequest.Model} task completed but returned no generated URLs (task_id={taskId}).");

        // Download final asset.
        using var fileResp = await _client.GetAsync(firstUrl, cancellationToken);
        var fileBytes = await fileResp.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!fileResp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(fileBytes);
            throw new Exception($"Freepik {imageRequest.Model} download error: {(int)fileResp.StatusCode} {fileResp.ReasonPhrase}: {err}");
        }

        var mime = fileResp.Content.Headers.ContentType?.MediaType;
        if (string.IsNullOrWhiteSpace(mime))
            mime = "image/png";

        var dataUrl = $"data:{mime};base64,{Convert.ToBase64String(fileBytes)}";

        using var finalDoc = JsonDocument.Parse(final.Raw);

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
                    status = final.Status,
                    generated = final.Generated
                }, JsonSerializerOptions.Web)
            }
        };
    }

    private async Task<FreepikUpscalerTaskResult> UpscalerPollAsync(string path, string taskId, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}{path}/{taskId}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);

        using var resp = await _client.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Freepik upscaler poll error: {(int)resp.StatusCode} {resp.ReasonPhrase}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        var data = doc.RootElement.GetProperty("data");
        var status = data.GetProperty("status").GetString() ?? "UNKNOWN";
        var returnedTaskId = data.GetProperty("task_id").GetString() ?? taskId;

        List<string>? generated = null;
        if (data.TryGetProperty("generated", out var genEl) && genEl.ValueKind == JsonValueKind.Array)
        {
            generated = [.. genEl.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .Where(s => !string.IsNullOrWhiteSpace(s))];
        }

        return new FreepikUpscalerTaskResult(status, generated, raw, returnedTaskId);
    }

    private static Dictionary<string, object?> BuildUpscalerCreativePayload(
        string image,
        string prompt,
        UpscalerCreative? cfg,
        List<object> warnings)
    {
        var payload = new Dictionary<string, object?>
        {
            ["image"] = image
            // webhook_url intentionally omitted (per user instruction: no webhooks)
        };

        if (!string.IsNullOrWhiteSpace(prompt))
            payload["prompt"] = prompt;

        if (!string.IsNullOrWhiteSpace(cfg?.ScaleFactor))
            payload["scale_factor"] = cfg.ScaleFactor;
        if (!string.IsNullOrWhiteSpace(cfg?.OptimizedFor))
            payload["optimized_for"] = cfg.OptimizedFor;
        if (cfg?.Creativity is { } creativity)
            payload["creativity"] = NormalizeMinus10To10(creativity, "creativity");
        if (cfg?.Hdr is { } hdr)
            payload["hdr"] = NormalizeMinus10To10(hdr, "hdr");
        if (cfg?.Resemblance is { } resemblance)
            payload["resemblance"] = NormalizeMinus10To10(resemblance, "resemblance");
        if (cfg?.Fractality is { } fractality)
            payload["fractality"] = NormalizeMinus10To10(fractality, "fractality");
        if (!string.IsNullOrWhiteSpace(cfg?.Engine))
            payload["engine"] = cfg.Engine;

        // If neither prompt nor any tuning controls were provided, surface a compatibility warning.
        if (cfg is null)
        {
            warnings.Add(new
            {
                type = "compatibility",
                feature = "upscaler",
                details = "No upscaler config provided; using API defaults."
            });
        }

        return payload;
    }

    private static Dictionary<string, object?> BuildUpscalerPrecisionV1Payload(
        string image,
        UpscalerPrecisionV1? cfg,
        List<object> warnings)
    {
        var payload = new Dictionary<string, object?>
        {
            ["image"] = image
            // webhook_url intentionally omitted
        };

        if (cfg?.Sharpen is { } sharpen)
            payload["sharpen"] = Normalize0To100(sharpen, "sharpen");
        if (cfg?.SmartGrain is { } smartGrain)
            payload["smart_grain"] = Normalize0To100(smartGrain, "smart_grain");
        if (cfg?.UltraDetail is { } ultraDetail)
            payload["ultra_detail"] = Normalize0To100(ultraDetail, "ultra_detail");

        if (cfg is null)
        {
            warnings.Add(new
            {
                type = "compatibility",
                feature = "upscaler",
                details = "No upscaler precision config provided; using API defaults."
            });
        }

        return payload;
    }

    private static Dictionary<string, object?> BuildUpscalerPrecisionV2Payload(
        string image,
        UpscalerPrecisionV2? cfg,
        List<object> warnings)
    {
        var payload = new Dictionary<string, object?>
        {
            ["image"] = image
            // webhook_url intentionally omitted
        };

        if (cfg?.Sharpen is { } sharpen)
            payload["sharpen"] = Normalize0To100(sharpen, "sharpen");
        if (cfg?.SmartGrain is { } smartGrain)
            payload["smart_grain"] = Normalize0To100(smartGrain, "smart_grain");
        if (cfg?.UltraDetail is { } ultraDetail)
            payload["ultra_detail"] = Normalize0To100(ultraDetail, "ultra_detail");
        if (!string.IsNullOrWhiteSpace(cfg?.Flavor))
            payload["flavor"] = cfg.Flavor;
        if (cfg?.ScaleFactor is { } scaleFactor)
            payload["scale_factor"] = Normalize2To16(scaleFactor, "scale_factor");

        if (cfg is null)
        {
            warnings.Add(new
            {
                type = "compatibility",
                feature = "upscaler",
                details = "No upscaler precision_v2 config provided; using API defaults."
            });
        }

        return payload;
    }

    private static int Normalize2To16(int value, string field)
    {
        if (value < 2 || value > 16)
            throw new ArgumentOutOfRangeException(field, $"{field} must be between 2 and 16.");
        return value;
    }

    private static int NormalizeMinus10To10(int value, string field)
    {
        if (value < -10 || value > 10)
            throw new ArgumentOutOfRangeException(field, $"{field} must be between -10 and 10.");
        return value;
    }
}

