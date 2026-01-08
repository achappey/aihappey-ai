using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.Alibaba;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.Alibaba;

public partial class AlibabaProvider
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // Qwen-Image (sync) endpoint (Singapore / intl)
    private const string DefaultDashScopeBaseUrl = "https://dashscope-intl.aliyuncs.com";
    private const string QwenImageSyncPath = "/api/v1/services/aigc/multimodal-generation/generation";

    private static readonly HashSet<string> AllowedSizes =
    [
        "1664*928",  // 16:9 (default)
        "1472*1104", // 4:3
        "1328*1328", // 1:1
        "1104*1472", // 3:4
        "928*1664"   // 9:16
    ];

    public async Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(imageRequest);
        if (string.IsNullOrWhiteSpace(imageRequest.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(imageRequest));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (imageRequest.N is > 1)
        {
            warnings.Add(new { type = "unsupported", feature = "n", details = "DashScope Qwen-Image returns exactly 1 image." });
        }

        if (imageRequest.Files?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "files" });

        if (imageRequest.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        var providerMetadata = imageRequest.GetImageProviderMetadata<AlibabaImageProviderMetadata>(GetIdentifier());

        var modelName = NormalizeAlibabaModelName(imageRequest.Model);

        var effectiveSize = CoerceSize(
            providerMetadata?.Size,
            MapGenericSizeToDashScope(imageRequest.Size),
            warnings);

        var effectiveSeed = providerMetadata?.Seed ?? imageRequest.Seed;

        var payload = new
        {
            model = modelName,
            input = new
            {
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = new[] { new { text = imageRequest.Prompt } }
                    }
                }
            },
            parameters = new
            {
                negative_prompt = providerMetadata?.NegativePrompt,
                prompt_extend = providerMetadata?.PromptExtend,
                watermark = providerMetadata?.Watermark,
                size = effectiveSize,
                seed = effectiveSeed
            }
        };

        var baseUrl = !string.IsNullOrWhiteSpace(providerMetadata?.BaseUrl)
            ? providerMetadata!.BaseUrl!.TrimEnd('/')
            : DefaultDashScopeBaseUrl;

        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri($"{baseUrl}{QwenImageSyncPath}"))
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"{resp.StatusCode}: {raw}");

        var imageUrl = ExtractImageUrlFromSyncResponse(raw);
        var bytes = await _client.GetByteArrayAsync(imageUrl, cancellationToken);
        var b64 = Convert.ToBase64String(bytes);

        return new ImageResponse
        {
            Images = [b64.ToDataUrl(MediaTypeNames.Image.Png)],
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = imageRequest.Model,
                Body = JsonDocument.Parse(raw).RootElement.Clone()
            }
        };
    }

    private static string NormalizeAlibabaModelName(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(model));

        // Accept both: "alibaba/qwen-image-plus" and "qwen-image-plus".
        if (!model.Contains('/'))
            return model.Trim();

        var split = model.SplitModelId();
        return string.IsNullOrWhiteSpace(split.Model) ? model.Trim() : split.Model.Trim();
    }

    private static string? MapGenericSizeToDashScope(string? genericSize)
    {
        if (string.IsNullOrWhiteSpace(genericSize))
            return null;

        // request uses "1664x928" while DashScope expects "1664*928"
        return genericSize.Trim().ToLowerInvariant().Replace('x', '*');
    }

    private static string CoerceSize(string? providerSize, string? mappedGenericSize, List<object> warnings)
    {
        var candidate = providerSize ?? mappedGenericSize ?? "1664*928";
        candidate = candidate.Trim();

        if (AllowedSizes.Contains(candidate))
            return candidate;

        warnings.Add(new
        {
            type = "unsupported",
            feature = "size",
            details = $"Requested size '{candidate}' not supported by DashScope Qwen-Image. Falling back to 1664*928."
        });

        return "1664*928";
    }

    private static string ExtractImageUrlFromSyncResponse(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;

        // output.choices[0].message.content[0].image
        var url = root
            .GetProperty("output")
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")[0]
            .GetProperty("image")
            .GetString();

        if (string.IsNullOrWhiteSpace(url))
            throw new Exception("DashScope response did not contain an image URL.");

        return url;
    }
}

