using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.MiniMax;

namespace AIHappey.Core.Providers.MiniMax;

public partial class MiniMaxProvider
{
    private static readonly JsonSerializerOptions ImageJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// MiniMax text-to-image via <c>POST /v1/image_generation</c>.
    /// <para>
    /// Contract choices:
    /// - We always request <c>response_format=base64</c> so we can return unified data-URLs.
    /// - We pass through width/height as provided (from <see cref="ImageRequest.Size"/> or providerOptions),
    ///   even if aspect_ratio is also present; MiniMax will prioritize aspect_ratio.
    /// </para>
    /// </summary>
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

        // MiniMax supports image-to-image via `subject_reference`.
        // Contract choice: use only the first file as the subject reference, warn if more were provided.
        var hasFiles = imageRequest.Files?.Any() == true;
        var firstFile = hasFiles ? imageRequest.Files!.First() : null;

        if (hasFiles && imageRequest.Files!.Count() > 1)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "files",
                details = $"Input File count {imageRequest.Files!.Count()} not supported. Used single input image."
            });
        }

        if (imageRequest.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        var metadata = imageRequest.GetImageProviderMetadata<MiniMaxImageProviderMetadata>(GetIdentifier());

        // ---- model name ----
        // Tooling usually strips "minimax/" before calling provider.ImageRequest, but accept both.

        // ---- width/height (from Size and/or providerOptions overrides) ----
        var normalizedSize = imageRequest.Size?.Replace(":", "x", StringComparison.OrdinalIgnoreCase);
        var widthFromSize = string.IsNullOrWhiteSpace(normalizedSize) ? null : new ImageRequest { Size = normalizedSize }.GetImageWidth();
        var heightFromSize = string.IsNullOrWhiteSpace(normalizedSize) ? null : new ImageRequest { Size = normalizedSize }.GetImageHeight();

        var width = widthFromSize;
        var height = heightFromSize;

        // ---- aspect ratio ----
        // If user explicitly provided AspectRatio, pass through. Otherwise, derive nearest MiniMax ratio from width/height when possible.
        var aspectRatio = !string.IsNullOrWhiteSpace(imageRequest.AspectRatio)
            ? imageRequest.AspectRatio
                : (width is not null && height is not null)
                    ? NearestMiniMaxAspectRatio(width.Value, height.Value)
                    : null;

        // We default to base64 to keep output consistent with the rest of AIHappey.
        // Allow override via providerOptions.minimax.response_format.
        var responseFormat = "base64";

        var seed = imageRequest.Seed;
        var n = imageRequest.N;

        var payloadDict = new Dictionary<string, object?>
        {
            ["model"] = imageRequest.Model,
            ["prompt"] = imageRequest.Prompt
        };

        // ---- image-to-image (subject_reference) ----
        if (firstFile is not null)
        {
            payloadDict["subject_reference"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "character",
                    ["image_file"] = firstFile.Data.ToDataUrl(firstFile.MediaType)
                }
            };
        }

        if (aspectRatio is not null) payloadDict["aspect_ratio"] = aspectRatio;
        if (width is not null) payloadDict["width"] = width;
        if (height is not null) payloadDict["height"] = height;
        if (responseFormat is not null) payloadDict["response_format"] = responseFormat;
        if (seed is not null) payloadDict["seed"] = seed;
        if (n is not null) payloadDict["n"] = n;
        if (metadata?.PromptOptimizer is not null)
            payloadDict["prompt_optimizer"] = metadata.PromptOptimizer;

        var payload = JsonSerializer.Serialize(payloadDict, ImageJson);

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/image_generation")
        {
            Content = new StringContent(payload, Encoding.UTF8, MediaTypeNames.Application.Json)
        };


        using var resp = await _client.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"{resp.StatusCode}: {raw}");

        using var doc = JsonDocument.Parse(raw);

        // ---- MiniMax error surface (base_resp) ----
        if (doc.RootElement.TryGetProperty("base_resp", out var baseResp) &&
            baseResp.ValueKind == JsonValueKind.Object &&
            baseResp.TryGetProperty("status_code", out var statusCodeEl) &&
            statusCodeEl.ValueKind == JsonValueKind.Number &&
            statusCodeEl.GetInt32() != 0)
        {
            var traceId = doc.RootElement.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString()
                : null;

            var msg = baseResp.TryGetProperty("status_msg", out var msgEl) && msgEl.ValueKind == JsonValueKind.String
                ? msgEl.GetString()
                : "MiniMax request failed";

            throw new Exception($"MiniMax image_generation failed (status_code={statusCodeEl.GetInt32()}, status_msg={msg}, id={traceId}).");
        }

        // ---- Parse images ----
        var images = new List<string>();

        if (string.Equals(responseFormat, "base64", StringComparison.OrdinalIgnoreCase))
        {
            if (doc.RootElement.TryGetProperty("data", out var dataEl) &&
                dataEl.ValueKind == JsonValueKind.Object &&
                dataEl.TryGetProperty("image_base64", out var b64Arr) &&
                b64Arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var b64 in b64Arr.EnumerateArray())
                {
                    if (b64.ValueKind != JsonValueKind.String)
                        continue;

                    var s = b64.GetString();
                    if (string.IsNullOrWhiteSpace(s))
                        continue;

                    images.Add(s.ToDataUrl(MediaTypeNames.Image.Png));
                }
            }
        }

        if (images.Count == 0)
            throw new Exception("MiniMax returned no images.");

        // ---- providerMetadata (small + structured; avoids copying large base64 arrays twice) ----
        Dictionary<string, JsonElement>? providerMetadata = null;
        try
        {
            var meta = new Dictionary<string, JsonElement>();
            if (doc.RootElement.TryGetProperty("id", out var id)) meta["id"] = id.Clone();
            if (doc.RootElement.TryGetProperty("metadata", out var md)) meta["metadata"] = md.Clone();
            if (doc.RootElement.TryGetProperty("base_resp", out var br)) meta["base_resp"] = br.Clone();

            if (meta.Count > 0)
            {
                providerMetadata = new Dictionary<string, JsonElement>
                {
                    [GetIdentifier()] = JsonSerializer.SerializeToElement(meta, JsonSerializerOptions.Web)
                };
            }
        }
        catch
        {
            // best-effort only
        }

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            ProviderMetadata = providerMetadata,
            Response = new()
            {
                Timestamp = now,
                ModelId = imageRequest.Model,
                Body = doc.RootElement.Clone()
            }
        };
    }

    private static string NormalizeModelName(string model)
    {
        // Accept both: "image-01" and "minimax/image-01".
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(model));

        var trimmed = model.Trim();
        var slash = trimmed.IndexOf('/');
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
    }

    private static string NearestMiniMaxAspectRatio(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return "1:1";

        var target = (double)width / height;

        // MiniMax documented aspect ratios.
        var options = new Dictionary<string, double>
        {
            ["1:1"] = 1.0,
            ["16:9"] = 16d / 9d,
            ["4:3"] = 4d / 3d,
            ["3:2"] = 3d / 2d,
            ["2:3"] = 2d / 3d,
            ["3:4"] = 3d / 4d,
            ["9:16"] = 9d / 16d,
            ["21:9"] = 21d / 9d
        };

        string best = "1:1";
        double bestDiff = double.MaxValue;

        foreach (var kv in options)
        {
            var diff = Math.Abs(kv.Value - target);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                best = kv.Key;
            }
        }

        return best;
    }
}

