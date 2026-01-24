using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model.Providers.Freepik;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.Freepik;

public sealed partial class FreepikProvider
{
    private const string ImageRelightPath = "/v1/ai/image-relight";

    private sealed record FreepikRelightTaskResult(string Status, List<string>? Generated, string Raw, string TaskId);

    public async Task<ImageResponse> RelightImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(imageRequest);
        if (string.IsNullOrWhiteSpace(imageRequest.Model))
            throw new ArgumentException("Model is required.", nameof(imageRequest));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (!imageRequest.Model.Equals("image-relight", StringComparison.OrdinalIgnoreCase))
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
            warnings.Add(new { type = "unsupported", feature = "n", details = "Freepik relight returns one image per request; generated a single image." });

        // Input image is required.
        var files = imageRequest.Files?.ToList();
        if (files is null || files.Count == 0)
            throw new ArgumentException("At least one input image is required in 'files'.", nameof(imageRequest));
        if (files.Count > 1)
            warnings.Add(new { type = "unsupported", feature = "files", details = "Freepik relight supports a single input image; extra images were ignored." });

        var firstFile = files[0];
        if (string.IsNullOrWhiteSpace(firstFile?.Data))
            throw new ArgumentException("files[0] must include 'data' (base64).", nameof(imageRequest));

        // Requirement: raw base64 only (no data URLs).
        if (LooksLikeDataUrl(firstFile.Data))
            throw new ArgumentException("files[0].data must be raw base64 (data URLs are not supported for Freepik relight).", nameof(imageRequest));

        var metadata = imageRequest.GetProviderMetadata<FreepikImageProviderMetadata>(GetIdentifier());
        var relight = metadata?.Relight;

        var startPayload = BuildRelightPayload(firstFile.Data, imageRequest.Prompt, relight, warnings);
        var startJson = JsonSerializer.Serialize(startPayload, JsonOpts);

        using var startReq = new HttpRequestMessage(HttpMethod.Post, BaseUrl + ImageRelightPath)
        {
            Content = new StringContent(startJson, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var startResp = await _client.SendAsync(startReq, cancellationToken);
        var startRaw = await startResp.Content.ReadAsStringAsync(cancellationToken);
        if (!startResp.IsSuccessStatusCode)
            throw new Exception($"Freepik image-relight start error: {(int)startResp.StatusCode} {startResp.ReasonPhrase}: {startRaw}");

        using var startDoc = JsonDocument.Parse(startRaw);
        var taskId = startDoc.RootElement.GetProperty("data").GetProperty("task_id").GetString();
        if (string.IsNullOrWhiteSpace(taskId))
            throw new Exception("Freepik response missing data.task_id");

        // Poll GET /v1/ai/image-relight/{task-id} until COMPLETED/FAILED.
        var final = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: ct => RelightPollAsync(taskId, ct),
            isTerminal: r => r.Status is "COMPLETED" or "FAILED",
            interval: TimeSpan.FromMilliseconds(800),
            timeout: TimeSpan.FromSeconds(90),
            maxAttempts: null,
            cancellationToken: cancellationToken);

        if (final.Status == "FAILED")
            throw new Exception($"Freepik image-relight task failed (task_id={taskId}).");

        var firstUrl = final.Generated?.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstUrl))
            throw new Exception($"Freepik image-relight task completed but returned no generated URLs (task_id={taskId}).");

        // Download final asset.
        using var fileResp = await _client.GetAsync(firstUrl, cancellationToken);
        var fileBytes = await fileResp.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!fileResp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(fileBytes);
            throw new Exception($"Freepik image-relight download error: {(int)fileResp.StatusCode} {fileResp.ReasonPhrase}: {err}");
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

    private static Dictionary<string, object?> BuildRelightPayload(
        string image,
        string? prompt,
        Relight? relight,
        List<object> warnings)
    {
        var payload = new Dictionary<string, object?>
        {
            ["image"] = image
            // webhook_url intentionally omitted (per user instruction: polling)
        };

        if (!string.IsNullOrWhiteSpace(prompt))
            payload["prompt"] = prompt;

        // Mutually exclusive transfer sources.
        /*    if (!string.IsNullOrWhiteSpace(relight?.TransferLightFromReferenceImage)
                && !string.IsNullOrWhiteSpace(relight?.TransferLightFromLightmap))
            {
                throw new ArgumentException("Provide only one of transfer_light_from_reference_image or transfer_light_from_lightmap.");
            }

            if (!string.IsNullOrWhiteSpace(relight?.TransferLightFromReferenceImage))
            {
                if (LooksLikeDataUrl(relight.TransferLightFromReferenceImage))
                    throw new ArgumentException("transfer_light_from_reference_image must be raw base64 (no data URLs).", nameof(relight));
                payload["transfer_light_from_reference_image"] = relight.TransferLightFromReferenceImage;
            }

            if (!string.IsNullOrWhiteSpace(relight?.TransferLightFromLightmap))
            {
                if (LooksLikeDataUrl(relight.TransferLightFromLightmap))
                    throw new ArgumentException("transfer_light_from_lightmap must be raw base64 (no data URLs).", nameof(relight));
                payload["transfer_light_from_lightmap"] = relight.TransferLightFromLightmap;
            }
    */
        if (relight?.LightTransferStrength is { } strength)
            payload["light_transfer_strength"] = Normalize0To100(strength, "light_transfer_strength");

        if (relight?.InterpolateFromOriginal is { } interpolate)
            payload["interpolate_from_original"] = interpolate;

        if (relight?.ChangeBackground is { } changeBg)
            payload["change_background"] = changeBg;

        if (!string.IsNullOrWhiteSpace(relight?.Style))
            payload["style"] = relight.Style;

        if (relight?.PreserveDetails is { } preserve)
            payload["preserve_details"] = preserve;

        if (relight?.AdvancedSettings is not null)
        {
            var adv = new Dictionary<string, object?>();

            if (relight.AdvancedSettings.Whites is { } whites)
                adv["whites"] = whites;
            if (relight.AdvancedSettings.Blacks is { } blacks)
                adv["blacks"] = blacks;
            if (relight.AdvancedSettings.Brightness is { } brightness)
                adv["brightness"] = brightness;
            if (relight.AdvancedSettings.Contrast is { } contrast)
                adv["contrast"] = contrast;
            if (relight.AdvancedSettings.Saturation is { } saturation)
                adv["saturation"] = saturation;
            if (!string.IsNullOrWhiteSpace(relight.AdvancedSettings.Engine))
                adv["engine"] = relight.AdvancedSettings.Engine;
            if (!string.IsNullOrWhiteSpace(relight.AdvancedSettings.TransferLightA))
                adv["transfer_light_a"] = relight.AdvancedSettings.TransferLightA;
            if (!string.IsNullOrWhiteSpace(relight.AdvancedSettings.TransferLightB))
                adv["transfer_light_b"] = relight.AdvancedSettings.TransferLightB;
            if (relight.AdvancedSettings.FixedGeneration is { } fixedGen)
                adv["fixed_generation"] = fixedGen;

            if (adv.Count > 0)
                payload["advanced_settings"] = adv;
        }

        if (relight is null)
        {
            warnings.Add(new
            {
                type = "compatibility",
                feature = "relight",
                details = "No relight config provided; using API defaults."
            });
        }

        return payload;
    }

    private static bool LooksLikeDataUrl(string s)
        => s.TrimStart().StartsWith("data:", StringComparison.OrdinalIgnoreCase);

    private async Task<FreepikRelightTaskResult> RelightPollAsync(string taskId, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}{ImageRelightPath}/{taskId}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);

        using var resp = await _client.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Freepik image-relight poll error: {(int)resp.StatusCode} {resp.ReasonPhrase}: {raw}");

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

        return new FreepikRelightTaskResult(status, generated, raw, returnedTaskId);
    }
}

