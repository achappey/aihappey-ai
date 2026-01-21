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
    private const string SkinEnhancerCreativePath = "/v1/ai/skin-enhancer/creative";
    private const string SkinEnhancerFaithfulPath = "/v1/ai/skin-enhancer/faithful";
    private const string SkinEnhancerFlexiblePath = "/v1/ai/skin-enhancer/flexible";
    private const string SkinEnhancerTaskPath = "/v1/ai/skin-enhancer";

    private sealed record FreepikSkinEnhancerTaskResult(string Status, List<string>? Generated, string Raw, string TaskId);

    public async Task<ImageResponse> SkinEnhancerImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(imageRequest);
        if (string.IsNullOrWhiteSpace(imageRequest.Model))
            throw new ArgumentException("Model is required.", nameof(imageRequest));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        // prompt is irrelevant for skin enhancer; ignore but keep contract surface stable.
        if (!string.IsNullOrWhiteSpace(imageRequest.Prompt))
            warnings.Add(new { type = "compatibility", feature = "prompt", details = "Freepik skin enhancer does not use prompt; value was ignored." });

        if (imageRequest.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });
        if (imageRequest.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });
        if (imageRequest.N is not null && imageRequest.N.Value != 1)
            warnings.Add(new { type = "unsupported", feature = "n", details = "Freepik skin enhancer returns one image per request; generated a single image." });

        var files = imageRequest.Files?.ToList();
        if (files is null || files.Count == 0)
            throw new ArgumentException("At least one input image is required in 'files'.", nameof(imageRequest));
        if (files.Count > 1)
        {
            warnings.Add(new { type = "unsupported", feature = "files", details = "Freepik skin enhancer supports a single input image; extra images were ignored." });
        }

        var firstFile = files[0];
        if (string.IsNullOrWhiteSpace(firstFile?.Data) || string.IsNullOrWhiteSpace(firstFile.MediaType))
            throw new ArgumentException("files[0] must include 'data' (base64) and 'mediaType'.", nameof(imageRequest));

        var inputImage = firstFile.Data.ToDataUrl(firstFile.MediaType);

        var metadata = imageRequest.GetImageProviderMetadata<FreepikImageProviderMetadata>(GetIdentifier());
        var se = metadata?.SkinEnhancer;

        var (endpointPath, payload) = BuildSkinEnhancerPayload(imageRequest.Model, inputImage, se, warnings);
        var startJson = JsonSerializer.Serialize(payload, JsonOpts);

        using var startReq = new HttpRequestMessage(HttpMethod.Post, BaseUrl + endpointPath)
        {
            Content = new StringContent(startJson, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var startResp = await _client.SendAsync(startReq, cancellationToken);
        var startRaw = await startResp.Content.ReadAsStringAsync(cancellationToken);
        if (!startResp.IsSuccessStatusCode)
            throw new Exception($"Freepik skin-enhancer start error: {(int)startResp.StatusCode} {startResp.ReasonPhrase}: {startRaw}");

        using var startDoc = JsonDocument.Parse(startRaw);
        var taskId = startDoc.RootElement.GetProperty("data").GetProperty("task_id").GetString();
        if (string.IsNullOrWhiteSpace(taskId))
            throw new Exception("Freepik response missing data.task_id");

        // Poll GET /v1/ai/skin-enhancer/{task-id} until COMPLETED/FAILED.
        var final = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: ct => SkinEnhancerPollAsync(taskId, ct),
            isTerminal: r => r.Status is "COMPLETED" or "FAILED",
            interval: TimeSpan.FromMilliseconds(800),
            timeout: TimeSpan.FromSeconds(90),
            maxAttempts: null,
            cancellationToken: cancellationToken);

        if (final.Status == "FAILED")
            throw new Exception($"Freepik skin-enhancer task failed (task_id={taskId}).");

        var firstUrl = final.Generated?.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstUrl))
            throw new Exception($"Freepik skin-enhancer task completed but returned no generated URLs (task_id={taskId}).");

        // Download final asset.
        using var fileResp = await _client.GetAsync(firstUrl, cancellationToken);
        var fileBytes = await fileResp.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!fileResp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(fileBytes);
            throw new Exception($"Freepik skin-enhancer download error: {(int)fileResp.StatusCode} {fileResp.ReasonPhrase}: {err}");
        }

        // Default to PNG if content-type is unknown.
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

    private static (string endpointPath, Dictionary<string, object?> payload) BuildSkinEnhancerPayload(
        string model,
        string inputImage,
        SkinEnhancer? skinEnhancer,
        List<object> warnings)
    {
        string endpoint = model.ToLowerInvariant() switch
        {
            "skin-enhancer/creative" => SkinEnhancerCreativePath,
            "skin-enhancer/faithful" => SkinEnhancerFaithfulPath,
            "skin-enhancer/flexible" => SkinEnhancerFlexiblePath,
            _ => throw new NotSupportedException($"Freepik image model '{model}' is not supported.")
        };

        var payload = new Dictionary<string, object?>
        {
            ["image"] = inputImage
            // webhook_url intentionally omitted (per user instruction: polling)
        };

        SkinEnhancerBase? opts = model.ToLowerInvariant() switch
        {
            "skin-enhancer/creative" => skinEnhancer?.Creative,
            "skin-enhancer/faithful" => skinEnhancer?.Faithful,
            "skin-enhancer/flexible" => skinEnhancer?.Flexible,
            _ => null
        };

        // Shared options (0-100)
        if (opts?.Sharpen is { } sharpen)
            payload["sharpen"] = Normalize0To100(sharpen, "sharpen");
        if (opts?.SmartGrain is { } sg)
            payload["smart_grain"] = Normalize0To100(sg, "smart_grain");

        // Faithful-only
        if (model.Equals("skin-enhancer/faithful", StringComparison.OrdinalIgnoreCase))
        {
            var faithful = skinEnhancer?.Faithful;
            if (faithful?.SkinDetail is { } sd)
                payload["skin_detail"] = Normalize0To100(sd, "skin_detail");
        }

        // Flexible-only
        if (model.Equals("skin-enhancer/flexible", StringComparison.OrdinalIgnoreCase))
        {
            var flexible = skinEnhancer?.Flexible;
            var optimized = flexible?.OptimizedFor?.Trim();
            if (!string.IsNullOrWhiteSpace(optimized))
            {
                var normalized = optimized.ToLowerInvariant();
                if (normalized is "enhance_skin" or "improve_lighting" or "enhance_everything" or "transform_to_real" or "no_make_up")
                {
                    payload["optimized_for"] = normalized;
                }
                else
                {
                    warnings.Add(new { type = "compatibility", feature = "optimized_for", details = $"Unknown optimized_for '{optimized}'. Using API default." });
                }
            }
        }

        return (endpoint, payload);
    }

    private static int Normalize0To100(int value, string field)
    {
        if (value < 0 || value > 100)
            throw new ArgumentOutOfRangeException(field, $"{field} must be between 0 and 100.");
        return value;
    }

    private async Task<FreepikSkinEnhancerTaskResult> SkinEnhancerPollAsync(string taskId, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}{SkinEnhancerTaskPath}/{taskId}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);

        using var resp = await _client.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Freepik skin-enhancer poll error: {(int)resp.StatusCode} {resp.ReasonPhrase}: {raw}");

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

        return new FreepikSkinEnhancerTaskResult(status, generated, raw, returnedTaskId);
    }
}

