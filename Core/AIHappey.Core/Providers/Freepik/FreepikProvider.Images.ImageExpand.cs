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
    private const string ImageExpandFluxProPath = "/v1/ai/image-expand/flux-pro";

    private sealed record FreepikImageExpandTaskResult(string Status, List<string>? Generated, string Raw, string TaskId);

    public async Task<ImageResponse> ImageExpandImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(imageRequest);
        if (string.IsNullOrWhiteSpace(imageRequest.Model))
            throw new ArgumentException("Model is required.", nameof(imageRequest));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (!imageRequest.Model.Equals("image-expand/flux-pro", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Freepik image model '{imageRequest.Model}' is not supported.");

        if (imageRequest.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });
        if (imageRequest.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });
        if (!string.IsNullOrWhiteSpace(imageRequest.Size))
            warnings.Add(new { type = "unsupported", feature = "size" });
        if (!string.IsNullOrWhiteSpace(imageRequest.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspect_ratio" });
        if (imageRequest.N is not null && imageRequest.N.Value != 1)
            warnings.Add(new { type = "unsupported", feature = "n", details = "Freepik image expand returns one image per request; generated a single image." });

        // Input image is required.
        var files = imageRequest.Files?.ToList();
        if (files is null || files.Count == 0)
            throw new ArgumentException("At least one input image is required in 'files'.", nameof(imageRequest));
        if (files.Count > 1)
            warnings.Add(new { type = "unsupported", feature = "files", details = "Freepik image expand supports a single input image; extra images were ignored." });

        var firstFile = files[0];
        if (string.IsNullOrWhiteSpace(firstFile?.Data))
            throw new ArgumentException("files[0] must include 'data' (base64).", nameof(imageRequest));

        // Docs require raw base64 bytes for `image`, but we also accept a data URL and pass it through.
        // If callers provide something else, we still send it and let the upstream API validate.
        var imageField = firstFile.Data;

        var metadata = imageRequest.GetImageProviderMetadata<FreepikImageProviderMetadata>(GetIdentifier());
        var expand = metadata?.ImageExpand;


        var startPayload = BuildImageExpandFluxProPayload(imageField, imageRequest.Prompt, expand, warnings);
        var startJson = JsonSerializer.Serialize(startPayload, JsonOpts);

        using var startReq = new HttpRequestMessage(HttpMethod.Post, BaseUrl + ImageExpandFluxProPath)
        {
            Content = new StringContent(startJson, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var startResp = await _client.SendAsync(startReq, cancellationToken);
        var startRaw = await startResp.Content.ReadAsStringAsync(cancellationToken);
        if (!startResp.IsSuccessStatusCode)
            throw new Exception($"Freepik image-expand start error: {(int)startResp.StatusCode} {startResp.ReasonPhrase}: {startRaw}");

        using var startDoc = JsonDocument.Parse(startRaw);
        var taskId = startDoc.RootElement.GetProperty("data").GetProperty("task_id").GetString();
        if (string.IsNullOrWhiteSpace(taskId))
            throw new Exception("Freepik response missing data.task_id");

        // Poll GET /v1/ai/image-expand/flux-pro/{task-id} until COMPLETED/FAILED.
        var final = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: ct => ImageExpandPollAsync(taskId, ct),
            isTerminal: r => r.Status is "COMPLETED" or "FAILED",
            interval: TimeSpan.FromMilliseconds(800),
            timeout: TimeSpan.FromSeconds(90),
            maxAttempts: null,
            cancellationToken: cancellationToken);

        if (final.Status == "FAILED")
            throw new Exception($"Freepik image-expand task failed (task_id={taskId}).");

        var firstUrl = final.Generated?.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstUrl))
            throw new Exception($"Freepik image-expand task completed but returned no generated URLs (task_id={taskId}).");

        // Download final asset.
        using var fileResp = await _client.GetAsync(firstUrl, cancellationToken);
        var fileBytes = await fileResp.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!fileResp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(fileBytes);
            throw new Exception($"Freepik image-expand download error: {(int)fileResp.StatusCode} {fileResp.ReasonPhrase}: {err}");
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

    private static Dictionary<string, object?> BuildImageExpandFluxProPayload(
        string image,
        string? prompt,
        ImageExpand? expand,
        List<object> warnings)
    {
        var payload = new Dictionary<string, object?>
        {
            ["image"] = image
            // webhook_url intentionally omitted (per user instruction: no webhooks)
        };

        if (!string.IsNullOrWhiteSpace(prompt))
            payload["prompt"] = prompt;

        if (expand?.Left is { } left)
            payload["left"] = Normalize0To2048(left, "left");
        if (expand?.Right is { } right)
            payload["right"] = Normalize0To2048(right, "right");
        if (expand?.Top is { } top)
            payload["top"] = Normalize0To2048(top, "top");
        if (expand?.Bottom is { } bottom)
            payload["bottom"] = Normalize0To2048(bottom, "bottom");

        // Freepik allows null/omitted for these fields; warn if caller provided no expansion at all.
        if (expand is null || (expand.Left is null && expand.Right is null && expand.Top is null && expand.Bottom is null))
        {
            warnings.Add(new
            {
                type = "compatibility",
                feature = "image_expand",
                details = "No expansion pixels were provided (left/right/top/bottom). Using API defaults."
            });
        }

        return payload;
    }

    private static int Normalize0To2048(int value, string field)
    {
        if (value < 0 || value > 2048)
            throw new ArgumentOutOfRangeException(field, $"{field} must be between 0 and 2048.");
        return value;
    }

    private async Task<FreepikImageExpandTaskResult> ImageExpandPollAsync(string taskId, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}{ImageExpandFluxProPath}/{taskId}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);

        using var resp = await _client.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Freepik image-expand poll error: {(int)resp.StatusCode} {resp.ReasonPhrase}: {raw}");

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

        return new FreepikImageExpandTaskResult(status, generated, raw, returnedTaskId);
    }
}

