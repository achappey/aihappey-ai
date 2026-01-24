using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Fireworks;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.Fireworks;

public partial class FireworksProvider
{
    private static readonly JsonSerializerOptions ImageJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(imageRequest);
        if (string.IsNullOrWhiteSpace(imageRequest.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(imageRequest));
        if (string.IsNullOrWhiteSpace(imageRequest.Model))
            throw new ArgumentException("Model is required.", nameof(imageRequest));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (imageRequest.Mask is not null)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "mask"
            });
        }

        // Basic routing:
        // - If an init image is provided, use /image_to_image.
        // - If this looks like a Flux model, use workflow endpoint.
        // - Otherwise use /image_generation text-to-image.
        var hasInitImage = imageRequest.Files?.Any() == true;
        var looksLikeFlux = imageRequest.Model.Contains("/flux-", StringComparison.OrdinalIgnoreCase)
                           || imageRequest.Model.Contains("flux-", StringComparison.OrdinalIgnoreCase);

        List<string> images;
        object? responseBody;

        if (looksLikeFlux)
        {
            if (imageRequest.Files?.Count() > 1)
            {
                warnings.Add(new
                {
                    type = "unsupported",
                    feature = "files",
                    details = "Multiple files/image-to-image are not supported for Fireworks workflow models. Falling back to single image-to-image."
                });
            }

            (images, responseBody) = await GenerateViaWorkflowAsync(imageRequest, cancellationToken);
        }
        else if (hasInitImage)
        {
            (images, responseBody) = await GenerateViaImageGenerationImageToImageAsync(imageRequest, cancellationToken);
        }
        else
        {
            (images, responseBody) = await GenerateViaImageGenerationTextToImageAsync(imageRequest, cancellationToken);
        }

        if (images.Count == 0)
            throw new Exception("Fireworks returned no images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = imageRequest.Model,
                Body = responseBody
            }
        };
    }

    private async Task<(List<string> images, object? responseBody)> GenerateViaImageGenerationTextToImageAsync(
        ImageRequest imageRequest,
        CancellationToken ct)
    {
        // Fireworks Image Generation API (binary image response)
        // POST /inference/v1/image_generation/<model>
        // Accept: image/jpeg

        var modelPath = NormalizeModelPath(imageRequest.Model);
        var endpoint = $"v1/image_generation/{modelPath}";

        var width = imageRequest.GetImageWidth();
        var height = imageRequest.GetImageHeight();

        if ((width is null || height is null) && !string.IsNullOrWhiteSpace(imageRequest.AspectRatio))
        {
            var inferred = imageRequest.AspectRatio.InferSizeFromAspectRatio();
            if (inferred is not null)
            {
                width ??= inferred.Value.width;
                height ??= inferred.Value.height;
            }
        }

        var n = Math.Clamp(imageRequest.N ?? 1, 1, 10);
        var images = new List<string>(capacity: n);
        var bodies = new List<object>();

        for (var i = 0; i < n; i++)
        {
            var payload = JsonSerializer.Serialize(new
            {
                prompt = imageRequest.Prompt,
                seed = imageRequest.Seed,
                width,
                height,
            }, ImageJson);

            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(payload, Encoding.UTF8, MediaTypeNames.Application.Json)
            };

            req.Headers.Accept.Clear();
            req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue(MediaTypeNames.Image.Jpeg));

            using var resp = await _client.SendAsync(req, ct);
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                var maybeText = TryDecodeUtf8(bytes);
                throw new Exception($"{resp.StatusCode}: {maybeText ?? "Fireworks image_generation request failed."}");
            }

            images.Add(Convert.ToBase64String(bytes).ToDataUrl(MediaTypeNames.Image.Jpeg));
            bodies.Add(new { endpoint, request = JsonDocument.Parse(payload).RootElement.Clone() });
        }

        return (images, new { requests = bodies });
    }

    private async Task<(List<string> images, object? responseBody)> GenerateViaImageGenerationImageToImageAsync(
        ImageRequest imageRequest,
        CancellationToken ct)
    {
        // Fireworks Image Generation image-to-image (multipart)
        // POST /inference/v1/image_generation/<model>/image_to_image

        var modelPath = NormalizeModelPath(imageRequest.Model);
        var endpoint = $"v1/image_generation/{modelPath}/image_to_image";

        var init = imageRequest.Files!.First();

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(imageRequest.Prompt), "prompt");

        if (imageRequest.Seed.HasValue)
            form.Add(new StringContent(imageRequest.Seed.Value.ToString()), "seed");

        // We pass the init image as raw bytes; Fireworks expects a file part.
        var initBytes = Convert.FromBase64String(init.Data);
        var initContent = new ByteArrayContent(initBytes);
        initContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(init.MediaType);
        form.Add(initContent, "init_image", "init_image");

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = form
        };

        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue(MediaTypeNames.Image.Jpeg));

        using var resp = await _client.SendAsync(req, ct);
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            var maybeText = TryDecodeUtf8(bytes);
            throw new Exception($"{resp.StatusCode}: {maybeText ?? "Fireworks image_to_image request failed."}");
        }

        // image_to_image returns a single image payload.
        var images = new List<string>
        {
            Convert.ToBase64String(bytes).ToDataUrl(MediaTypeNames.Image.Jpeg)
        };

        return (images, new { endpoint });
    }

    private async Task<(List<string> images, object? responseBody)> GenerateViaWorkflowAsync(
        ImageRequest imageRequest,
        CancellationToken ct)
    {
        // Fireworks Workflows API (submit + poll)
        // POST /inference/v1/workflows/<model>
        // POST /inference/v1/workflows/<model>/get_result

        var modelPath = NormalizeModelPath(imageRequest.Model);
        var submitEndpoint = $"v1/workflows/{modelPath}";
        var resultEndpoint = $"v1/workflows/{modelPath}/get_result";
        var metadata = imageRequest.GetProviderMetadata<FireworksImageProviderMetadata>(GetIdentifier());

        var payload = new Dictionary<string, object?>
        {
            ["prompt"] = imageRequest.Prompt,
        };

        if (imageRequest.Seed is not null)
        {
            payload["seed"] = imageRequest.Seed;
        }

        var imageFile = imageRequest.Files?.FirstOrDefault();
        if (imageFile is not null)
        {
            payload["input_image"] = imageFile.Data.ToDataUrl(imageFile.MediaType);
        }

        if (imageRequest.AspectRatio is not null)
        {
            payload["aspect_ratio"] = imageRequest.AspectRatio;
        }

        if (metadata?.OutputFormat is not null)
        {
            payload["output_format"] = metadata.OutputFormat;
        }

        if (metadata?.SafetyTolerance is not null)
        {
            payload["safety_tolerance"] = metadata.SafetyTolerance;
        }

        if (metadata?.PromptUpsampling is not null)
        {
            payload["prompt_upsampling"] = metadata.PromptUpsampling;
        }

        if (metadata?.WebhookUrl is not null)
        {
            payload["webhook_url"] = metadata.WebhookUrl;
        }

        var submitPayload = JsonSerializer.Serialize(payload, ImageJson);
        using var submitReq = new HttpRequestMessage(HttpMethod.Post, submitEndpoint)
        {
            Content = new StringContent(submitPayload, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        submitReq.Headers.Accept.Clear();
        submitReq.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));

        using var submitResp = await _client.SendAsync(submitReq, ct);
        var submitRaw = await submitResp.Content.ReadAsStringAsync(ct);
        if (!submitResp.IsSuccessStatusCode)
            throw new Exception($"{submitResp.StatusCode}: {submitRaw}");

        using var submitDoc = JsonDocument.Parse(submitRaw);
        var requestId = submitDoc.RootElement.TryGetProperty("request_id", out var idEl) ? idEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(requestId))
            throw new Exception("Fireworks workflow submit returned no request_id.");

        // Poll
        const int maxAttempts = 60;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(1), ct);

            var pollPayload = JsonSerializer.Serialize(new { id = requestId }, ImageJson);
            using var pollReq = new HttpRequestMessage(HttpMethod.Post, resultEndpoint)
            {
                Content = new StringContent(pollPayload, Encoding.UTF8, MediaTypeNames.Application.Json)
            };

            // The Fireworks docs show Accept: image/jpeg, but the polling response is JSON.
            pollReq.Headers.Accept.Clear();
            pollReq.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));

            using var pollResp = await _client.SendAsync(pollReq, ct);
            var pollRaw = await pollResp.Content.ReadAsStringAsync(ct);
            if (!pollResp.IsSuccessStatusCode)
                throw new Exception($"{pollResp.StatusCode}: {pollRaw}");

            using var pollDoc = JsonDocument.Parse(pollRaw);
            var status = pollDoc.RootElement.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(status))
                continue;

            if (status.Equals("Failed", StringComparison.OrdinalIgnoreCase)
                || status.Equals("Error", StringComparison.OrdinalIgnoreCase))
            {
                var details = pollDoc.RootElement.TryGetProperty("details", out var detEl) ? detEl.ToString() : null;
                throw new Exception($"Generation failed: {details ?? "Unknown error"}");
            }

            if (status.Equals("Ready", StringComparison.OrdinalIgnoreCase)
                || status.Equals("Complete", StringComparison.OrdinalIgnoreCase)
                || status.Equals("Finished", StringComparison.OrdinalIgnoreCase))
            {
                // result.sample may be an URL or base64
                if (!pollDoc.RootElement.TryGetProperty("result", out var resultEl)
                    || resultEl.ValueKind != JsonValueKind.Object
                    || !resultEl.TryGetProperty("sample", out var sampleEl))
                {
                    throw new Exception("Fireworks workflow result returned no result.sample.");
                }

                var sample = sampleEl.GetString();
                if (string.IsNullOrWhiteSpace(sample))
                    throw new Exception("Fireworks workflow result returned empty sample.");

                var bytes = sample.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? await _client.GetByteArrayAsync(sample, ct)
                    : Convert.FromBase64String(sample);

                var images = new List<string>
                {
                    Convert.ToBase64String(bytes).ToDataUrl(MediaTypeNames.Image.Jpeg)
                };

                return (images, new
                {
                    requestId,
                    submit = JsonDocument.Parse(submitRaw).RootElement.Clone(),
                    poll = JsonDocument.Parse(pollRaw).RootElement.Clone()
                });
            }
        }

        throw new TimeoutException("Timed out waiting for Fireworks workflow result.");
    }

    private static string NormalizeModelPath(string model)
    {
        // Expected incoming model string style in this repo:
        //   fireworks/accounts/fireworks/models/<name>
        // We remove a leading "fireworks/" if present (tolerant), and then use it as path.
        return model.StartsWith("fireworks/", StringComparison.OrdinalIgnoreCase)
            ? model["fireworks/".Length..]
            : model;
    }

    private static string? TryDecodeUtf8(byte[] bytes)
    {
        if (bytes is null || bytes.Length == 0)
            return null;

        try
        {
            var text = Encoding.UTF8.GetString(bytes);
            // Heuristic: only return if it looks like JSON or a readable error.
            if (text.TrimStart().StartsWith("{", StringComparison.Ordinal)
                || text.TrimStart().StartsWith("[", StringComparison.Ordinal)
                || text.Contains("error", StringComparison.OrdinalIgnoreCase))
                return text;

            return null;
        }
        catch
        {
            return null;
        }
    }
}

