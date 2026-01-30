using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.KlingAI;
using AIHappey.Core.AI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.KlingAI;

public partial class KlingAIProvider
{
    private static readonly JsonSerializerOptions ImageJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<ImageResponse> ImageRequest(ImageRequest imageRequest,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(imageRequest);
        if (string.IsNullOrWhiteSpace(imageRequest.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(imageRequest));
        if (string.IsNullOrWhiteSpace(imageRequest.Model))
            throw new ArgumentException("Model is required.", nameof(imageRequest));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var metadata = imageRequest.GetProviderMetadata<KlingAIImageProviderMetadata>(GetIdentifier());

        if (imageRequest.Mask is not null)
        {
            warnings.Add(new { type = "unsupported", feature = "mask" });
        }

        var firstFile = imageRequest.Files?.FirstOrDefault();
        if (imageRequest.Files?.Skip(1).Any() == true)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "files",
                details = "Multiple input images not supported; used files[0]."
            });
        }

        var modelName = NormalizeModelName(imageRequest.Model);

        var payload = new Dictionary<string, object?>
        {
            ["prompt"] = imageRequest.Prompt,
            ["model_name"] = modelName,
            ["negative_prompt"] = metadata?.NegativePrompt,
            ["n"] = imageRequest.N,
            ["aspect_ratio"] = imageRequest.AspectRatio,
            ["resolution"] = metadata?.Resolution,
            ["image_reference"] = metadata?.ImageReference,
            ["image_fidelity"] = metadata?.ImageFidelity,
            ["human_fidelity"] = metadata?.HumanFidelity,
            ["callback_url"] = metadata?.CallbackUrl,
            ["external_task_id"] = metadata?.ExternalTaskId
        };

        if (imageRequest.Seed is not null)
        {
            warnings.Add(new { type = "unsupported", feature = "seed" });
        }

        if (!string.IsNullOrWhiteSpace(imageRequest.Size))
        {
            var normalizedSize = imageRequest.Size.Replace(":", "x", StringComparison.OrdinalIgnoreCase);
            var width = string.IsNullOrWhiteSpace(normalizedSize)
                ? null
                : new ImageRequest { Size = normalizedSize }.GetImageWidth();
            var height = string.IsNullOrWhiteSpace(normalizedSize)
                ? null
                : new ImageRequest { Size = normalizedSize }.GetImageHeight();

            if (width is null || height is null)
            {
                warnings.Add(new { type = "unsupported", feature = "size", details = "Size must be formatted as WxH (e.g., 1024x1024)." });
            }
            else
            {
                var inferred = width.Value / (double)height.Value;
                var ratio = FindClosestAspectRatio(inferred);
                if (string.IsNullOrWhiteSpace(imageRequest.AspectRatio))
                    payload["aspect_ratio"] = ratio;
                else
                    warnings.Add(new { type = "compatibility", feature = "size", details = "Size ignored because aspect_ratio was provided." });
            }
        }

        if (firstFile is not null)
        {
            var imageData = firstFile.Data;
            if (imageData.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                imageData = imageData.RemoveDataUrlPrefix();

            payload["image"] = imageData;

            if (!string.IsNullOrWhiteSpace(metadata?.NegativePrompt))
            {
                warnings.Add(new
                {
                    type = "unsupported",
                    feature = "negative_prompt",
                    details = "negative_prompt is not supported for image-to-image requests and was ignored."
                });
                payload.Remove("negative_prompt");
            }
        }

        var json = JsonSerializer.Serialize(payload, ImageJson);
        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"{resp.StatusCode}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        if (!root.TryGetProperty("code", out var codeEl) || codeEl.GetInt32() != 0)
        {
            var message = root.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : "Unknown KlingAI error";
            throw new Exception($"KlingAI request failed: {message}");
        }

        var taskId = ExtractTaskId(root);
        var final = await PollTaskAsync(taskId, cancellationToken);
        var images = await ExtractImagesAsync(final, cancellationToken);

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = imageRequest.Model,
                Body = final.Clone()
            }
        };
    }

    private static string ExtractTaskId(JsonElement root)
    {
        if (root.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("task_id", out var taskIdEl) &&
            taskIdEl.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(taskIdEl.GetString()))
        {
            return taskIdEl.GetString()!;
        }

        throw new Exception("No task_id returned from KlingAI API.");
    }

    private async Task<JsonElement> PollTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        var final = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: async ct =>
            {
                using var pollResp = await _client.GetAsync($"v1/images/generations/{taskId}", ct);
                var pollRaw = await pollResp.Content.ReadAsStringAsync(ct);
                if (!pollResp.IsSuccessStatusCode)
                    throw new Exception($"{pollResp.StatusCode}: {pollRaw}");

                using var pollDoc = JsonDocument.Parse(pollRaw);
                return pollDoc.RootElement.Clone();
            },
            isTerminal: r =>
            {
                var status = GetTaskStatus(r);
                return status is "succeed" or "failed";
            },
            interval: TimeSpan.FromSeconds(5),
            timeout: TimeSpan.FromMinutes(5),
            maxAttempts: null,
            cancellationToken: cancellationToken);

        var status = GetTaskStatus(final);
        if (status == "failed")
        {
            var msg = TryGetStatusMessage(final) ?? "KlingAI task failed.";
            throw new Exception(msg);
        }

        return final;
    }

    private static string? GetTaskStatus(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return null;

        if (data.TryGetProperty("task_status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String)
            return statusEl.GetString();

        return null;
    }

    private static string? TryGetStatusMessage(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return null;

        if (data.TryGetProperty("task_status_msg", out var msgEl) && msgEl.ValueKind == JsonValueKind.String)
            return msgEl.GetString();

        return null;
    }

    private async Task<List<string>> ExtractImagesAsync(JsonElement root, CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new Exception("KlingAI poll response missing data object.");

        if (!data.TryGetProperty("task_result", out var result) || result.ValueKind != JsonValueKind.Object)
            throw new Exception("KlingAI poll response missing task_result.");

        if (!result.TryGetProperty("images", out var imagesEl) || imagesEl.ValueKind != JsonValueKind.Array)
            throw new Exception("KlingAI poll response missing images array.");

        var images = new List<string>();
        foreach (var img in imagesEl.EnumerateArray())
        {
            if (!img.TryGetProperty("url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String)
                continue;

            var url = urlEl.GetString();
            if (string.IsNullOrWhiteSpace(url))
                continue;

            using var imgResp = await _client.GetAsync(url, cancellationToken);
            var bytes = await imgResp.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!imgResp.IsSuccessStatusCode)
                throw new Exception($"Failed to download KlingAI image: {imgResp.StatusCode}");

            var mediaType = imgResp.Content.Headers.ContentType?.MediaType ?? MediaTypeNames.Image.Png;
            images.Add(Convert.ToBase64String(bytes).ToDataUrl(mediaType));
        }

        if (images.Count == 0)
            throw new Exception("KlingAI returned no images.");

        return images;
    }

    private static string NormalizeModelName(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return model;

        var trimmed = model.Trim();
        var slash = trimmed.IndexOf('/');
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
    }

    private static string FindClosestAspectRatio(double ratio)
    {
        var options = new Dictionary<string, double>
        {
            ["16:9"] = 16d / 9d,
            ["9:16"] = 9d / 16d,
            ["1:1"] = 1d,
            ["4:3"] = 4d / 3d,
            ["3:4"] = 3d / 4d,
            ["3:2"] = 3d / 2d,
            ["2:3"] = 2d / 3d,
            ["21:9"] = 21d / 9d
        };

        var best = "1:1";
        var bestDiff = double.MaxValue;
        foreach (var kv in options)
        {
            var diff = Math.Abs(kv.Value - ratio);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                best = kv.Key;
            }
        }

        return best;
    }
}
