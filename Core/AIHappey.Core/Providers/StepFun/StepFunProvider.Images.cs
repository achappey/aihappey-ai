using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.StepFun;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.StepFun;

public partial class StepFunProvider
{
    private static readonly JsonSerializerOptions StepFunImageJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> ImageRequestStepFun(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var metadata = request.GetProviderMetadata<StepFunImageProviderMetadata>(GetIdentifier());
        var files = request.Files?.ToList() ?? [];

        ValidateStepFunImageRequest(request, metadata, files, warnings);

        var responseFormat = NormalizeStepFunImageResponseFormat(metadata?.ResponseFormat, warnings);
        var raw = files.Count > 0
            ? await SendStepFunImageEditRequestAsync(request, metadata, files[0], responseFormat, cancellationToken)
            : await SendStepFunImageGenerationRequestAsync(request, metadata, responseFormat, cancellationToken);

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement.Clone();
        var images = await ExtractStepFunImagesAsync(root, responseFormat, cancellationToken);

        if (images.Count == 0)
            throw new InvalidOperationException("StepFun image API returned no images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            ProviderMetadata = GetIdentifier()
                .CreatePrimitiveProviderMetadata(root.Clone()),
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private async Task<string> SendStepFunImageGenerationRequestAsync(
        ImageRequest request,
        StepFunImageProviderMetadata? metadata,
        string responseFormat,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt,
            ["size"] = NormalizeStepFunImageSize(request.Size),
            ["n"] = 1,
            ["response_format"] = responseFormat,
            ["seed"] = request.Seed,
            ["steps"] = metadata?.Steps,
            ["cfg_scale"] = metadata?.CfgScale,
            ["negative_prompt"] = metadata?.NegativePrompt,
            ["text_mode"] = metadata?.TextMode
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, StepFunImageJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"StepFun image generation failed ({(int)resp.StatusCode}): {raw}");

        return raw;
    }

    private async Task<string> SendStepFunImageEditRequestAsync(
        ImageRequest request,
        StepFunImageProviderMetadata? metadata,
        ImageFile image,
        string responseFormat,
        CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(request.Model), "model");
        form.Add(new StringContent(request.Prompt), "prompt");
        form.Add(new StringContent(responseFormat), "response_format");

        if (request.Seed is not null)
            form.Add(new StringContent(request.Seed.Value.ToString()), "seed");

        if (metadata?.Steps is not null)
            form.Add(new StringContent(metadata.Steps.Value.ToString()), "steps");

        if (metadata?.CfgScale is not null)
            form.Add(new StringContent(metadata.CfgScale.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)), "cfg_scale");

        if (!string.IsNullOrWhiteSpace(metadata?.NegativePrompt))
            form.Add(new StringContent(metadata.NegativePrompt), "negative_prompt");

        if (metadata?.TextMode is not null)
            form.Add(new StringContent(metadata.TextMode.Value ? "true" : "false"), "text_mode");

        var imageBytes = Convert.FromBase64String(image.Data.RemoveDataUrlPrefix());
        var imageContent = new ByteArrayContent(imageBytes);
        imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(image.MediaType);
        form.Add(imageContent, "image", "image");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/images/edits")
        {
            Content = form
        };

        using var resp = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"StepFun image edit failed ({(int)resp.StatusCode}): {raw}");

        return raw;
    }

    private async Task<List<string>> ExtractStepFunImagesAsync(JsonElement root, string responseFormat, CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("StepFun image API response is missing the data array.");

        var images = new List<string>();

        foreach (var item in dataEl.EnumerateArray())
        {
            if (item.TryGetProperty("b64_json", out var b64El) && b64El.ValueKind == JsonValueKind.String)
            {
                var b64 = b64El.GetString();
                if (!string.IsNullOrWhiteSpace(b64))
                    images.Add(b64.ToDataUrl(MediaTypeNames.Image.Png));

                continue;
            }

            if (item.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
            {
                var url = urlEl.GetString();
                if (string.IsNullOrWhiteSpace(url))
                    continue;

                using var imageResp = await _client.GetAsync(url, cancellationToken);
                var bytes = await imageResp.Content.ReadAsByteArrayAsync(cancellationToken);

                if (!imageResp.IsSuccessStatusCode || bytes.Length == 0)
                    throw new InvalidOperationException($"Failed to download StepFun image from returned URL ({(int)imageResp.StatusCode}).");

                var mediaType = imageResp.Content.Headers.ContentType?.MediaType
                    ?? GuessStepFunImageMediaType(url)
                    ?? MediaTypeNames.Image.Png;

                images.Add(Convert.ToBase64String(bytes).ToDataUrl(mediaType));
            }
        }

        return images;
    }

    private static void ValidateStepFunImageRequest(
        ImageRequest request,
        StepFunImageProviderMetadata? metadata,
        IReadOnlyList<ImageFile> files,
        List<object> warnings)
    {
        if (request.Prompt.Length > 512)
            throw new ArgumentException("StepFun image prompt must be 512 characters or fewer.", nameof(request));

        if (request.N is not null && request.N.Value != 1)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "n",
                details = $"StepFun currently supports n=1. Requested n={request.N.Value}, using n=1."
            });
        }

        if (files.Count > 1)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "files",
                details = "StepFun image editing currently supports one input image. Used files[0]."
            });
        }

        if (request.Mask is not null)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "mask"
            });
        }

        if (!string.IsNullOrWhiteSpace(request.AspectRatio) && string.IsNullOrWhiteSpace(request.Size))
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "aspectRatio",
                details = "StepFun image API expects explicit size values; aspectRatio is ignored."
            });
        }

        if (metadata?.NegativePrompt?.Length > 512)
            throw new ArgumentException("StepFun negative_prompt must be 512 characters or fewer.", nameof(request));

        if (metadata?.Steps is not null and (< 1 or > 50))
            throw new ArgumentOutOfRangeException(nameof(request), "StepFun steps must be in the range [1, 50].");

        if (metadata?.CfgScale is not null and (< 1.0 or > 10.0))
            throw new ArgumentOutOfRangeException(nameof(request), "StepFun cfg_scale must be in the range [1.0, 10.0].");

        if (request.Seed is < 0 or > 2147483647)
            throw new ArgumentOutOfRangeException(nameof(request), "StepFun seed must be in the range [0, 2147483647].");
    }

    private static string NormalizeStepFunImageResponseFormat(string? responseFormat, List<object> warnings)
    {
        if (string.IsNullOrWhiteSpace(responseFormat))
            return "b64_json";

        var normalized = responseFormat.Trim().ToLowerInvariant();
        if (normalized is "b64_json" or "url")
            return normalized;

        warnings.Add(new
        {
            type = "unsupported",
            feature = "providerOptions.stepfun.response_format",
            details = $"StepFun supports response_format b64_json or url. Requested '{responseFormat}', using b64_json."
        });

        return "b64_json";
    }

    private static string? NormalizeStepFunImageSize(string? size)
        => string.IsNullOrWhiteSpace(size)
            ? null
            : size.Trim().Replace(" ", string.Empty, StringComparison.OrdinalIgnoreCase);

    private static string? GuessStepFunImageMediaType(string url)
    {
        var clean = url.Split('?', '#')[0];

        if (clean.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || clean.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
            return MediaTypeNames.Image.Jpeg;

        if (clean.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            return "image/webp";

        if (clean.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
            return MediaTypeNames.Image.Gif;

        if (clean.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return MediaTypeNames.Image.Png;

        return null;
    }
}
