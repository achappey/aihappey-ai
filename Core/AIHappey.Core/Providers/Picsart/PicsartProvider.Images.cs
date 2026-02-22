using System.Globalization;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Picsart;

public partial class PicsartProvider
{
    private static readonly JsonSerializerOptions PicsartJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record PicsartInferenceResult(string Status, JsonElement Root);

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        var model = request.Model.Trim();
        var (endpoint, inferenceEndpoint, modelName) = ResolveEndpoints(model);

        var (width, height) = ResolveDimensions(request, warnings);
        var count = request.N;

        if (model.StartsWith("picsart/logo/", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new { type = "unsupported", feature = "prompt" });
            warnings.Add(new { type = "unsupported", feature = "size" });
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });
        }

        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        if (request.Files?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "files" });

        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });

        using var submitRequest = BuildSubmitRequest(endpoint, modelName, request, width, height, count);
        using var submitResponse = await _client.SendAsync(submitRequest, cancellationToken);
        var submitRaw = await submitResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!submitResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Picsart image request failed ({(int)submitResponse.StatusCode}): {submitRaw}");

        using var submitDoc = JsonDocument.Parse(submitRaw);
        var inferenceId = ReadInferenceId(submitDoc.RootElement);
        if (string.IsNullOrWhiteSpace(inferenceId))
            throw new InvalidOperationException("Picsart response missing inference id.");

        var completed = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: ct => PollInferenceAsync(inferenceEndpoint, inferenceId, ct),
            isTerminal: result => IsTerminalStatus(result.Status),
            interval: TimeSpan.FromSeconds(2),
            timeout: TimeSpan.FromMinutes(5),
            maxAttempts: null,
            cancellationToken: cancellationToken);

        if (IsFailureStatus(completed.Status))
            throw new InvalidOperationException($"Picsart image generation failed with status '{completed.Status}'.");

        var images = await ExtractImagesAsync(completed.Root, cancellationToken);
        if (images.Count == 0)
            throw new InvalidOperationException("Picsart response returned no images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = completed.Root.Clone()
            },
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = submitDoc.RootElement.Clone()
            }
        };
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No Picsart API key.");

        _client.DefaultRequestHeaders.Remove("X-Picsart-API-Key");
        _client.DefaultRequestHeaders.Add("X-Picsart-API-Key", key);
    }

    private static (string endpoint, string inferenceEndpoint, string modelName) ResolveEndpoints(string model)
    {
        if (model.StartsWith("picsart/text2image/", StringComparison.OrdinalIgnoreCase))
            return ("v1/text2image", "v1/text2image/inferences/", model["picsart/text2image/".Length..]);

        if (model.StartsWith("picsart/text2sticker/", StringComparison.OrdinalIgnoreCase))
            return ("v1/text2sticker", "v1/text2sticker/inferences/", model["picsart/text2sticker/".Length..]);

        if (model.StartsWith("picsart/logo/", StringComparison.OrdinalIgnoreCase))
            return ("v1/logo", "v1/logo/inferences/", model["picsart/logo/".Length..]);

        throw new NotSupportedException($"Picsart image model '{model}' is not supported.");
    }

    private static (int? width, int? height) ResolveDimensions(ImageRequest request, List<object> warnings)
    {
        if (!string.IsNullOrWhiteSpace(request.Size))
        {
            var normalizedSize = request.Size.Replace(":", "x", StringComparison.OrdinalIgnoreCase);
            var width = new ImageRequest { Size = normalizedSize }.GetImageWidth();
            var height = new ImageRequest { Size = normalizedSize }.GetImageHeight();
            return (width, height);
        }

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
        {
            var inferred = request.AspectRatio.InferSizeFromAspectRatio(minWidth: 64, maxWidth: 1024, minHeight: 64, maxHeight: 1024);
            if (inferred is not null)
                return (inferred.Value.width, inferred.Value.height);

            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });
        }

        return (null, null);
    }

    private static HttpRequestMessage BuildSubmitRequest(
        string endpoint,
        string modelName,
        ImageRequest request,
        int? width,
        int? height,
        int? count)
    {
        if (endpoint.Equals("v1/logo", StringComparison.OrdinalIgnoreCase))
        {
            var form = new MultipartFormDataContent();

            form.Add("brand_name".NamedField(request.Prompt));
            form.Add("business_description".NamedField(request.Prompt));

            if (count is not null)
                form.Add("count".NamedField(count.Value.ToString(CultureInfo.InvariantCulture)));

            form.Add("model".NamedField(modelName));

            return new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = form
            };
        }

        var payload = new Dictionary<string, object?>
        {
            ["prompt"] = request.Prompt,
            ["width"] = width,
            ["height"] = height,
            ["count"] = count,
            ["model"] = modelName
        };

        var json = JsonSerializer.Serialize(payload, PicsartJsonOptions);
        return new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };
    }


    private static string? ReadInferenceId(JsonElement root)
    {
        if (root.TryGetProperty("inference_id", out var inferenceId) && inferenceId.ValueKind == JsonValueKind.String)
            return inferenceId.GetString();

        if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
            return idEl.GetString();

        return null;
    }

    private async Task<PicsartInferenceResult> PollInferenceAsync(
        string inferenceEndpoint,
        string inferenceId,
        CancellationToken cancellationToken)
    {
        using var pollReq = new HttpRequestMessage(HttpMethod.Get, $"{inferenceEndpoint}{Uri.EscapeDataString(inferenceId)}");
        using var pollResp = await _client.SendAsync(pollReq, cancellationToken);
        var pollRaw = await pollResp.Content.ReadAsStringAsync(cancellationToken);
        if (!pollResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Picsart inference poll failed ({(int)pollResp.StatusCode}): {pollRaw}");

        using var pollDoc = JsonDocument.Parse(pollRaw);
        var root = pollDoc.RootElement.Clone();
        var status = root.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String
            ? statusEl.GetString() ?? "unknown"
            : "unknown";

        return new PicsartInferenceResult(status, root);
    }

    private static bool IsTerminalStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return false;

        return string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "success", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "error", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFailureStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return false;

        return string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "error", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<List<string>> ExtractImagesAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var images = new List<string>();

        if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array)
            return images;

        foreach (var item in dataEl.EnumerateArray())
        {
            if (!item.TryGetProperty("url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String)
                continue;

            var url = urlEl.GetString();
            if (string.IsNullOrWhiteSpace(url))
                continue;

            var bytes = await _client.GetByteArrayAsync(url, cancellationToken);
            var mime = GuessImageMimeType(url);
            images.Add(Convert.ToBase64String(bytes).ToDataUrl(mime));
        }

        return images;
    }

    private static string GuessImageMimeType(string url)
    {
        if (url.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || url.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
            return MediaTypeNames.Image.Jpeg;

        if (url.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            return "image/webp";

        if (url.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return MediaTypeNames.Image.Png;

        return MediaTypeNames.Image.Png;
    }
}
