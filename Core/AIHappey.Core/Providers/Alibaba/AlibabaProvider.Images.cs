using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Alibaba;
using AIHappey.Core.AI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Alibaba;

public partial class AlibabaProvider
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // DashScope (sync) endpoint (Singapore / intl)
    private const string DefaultDashScopeBaseUrl = "https://dashscope-intl.aliyuncs.com";
    private const string QwenImageSyncPath = "/api/v1/services/aigc/multimodal-generation/generation";

    public async Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(imageRequest);
        if (string.IsNullOrWhiteSpace(imageRequest.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(imageRequest));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        var providerMetadata = imageRequest.GetProviderMetadata<AlibabaImageProviderMetadata>(GetIdentifier());

        if (IsWan26Model(imageRequest.Model))
            return await Wan26ImageRequest(imageRequest, providerMetadata?.Wan, imageRequest.Model, warnings, now, cancellationToken);

        var inputImages = imageRequest.Files?.ToList() ?? [];
        var isQwenImage3 = imageRequest.Model.StartsWith("qwen-image-3.0", StringComparison.OrdinalIgnoreCase);

        if (inputImages.Count > 0 && !isQwenImage3)
        {
            warnings.Add(new { type = "unsupported", feature = "files" });
            inputImages = [];
        }

        if (isQwenImage3 && inputImages.Count > 3)
            throw new ArgumentException("Qwen Image 3.0 supports between 1 and 3 reference images.", nameof(imageRequest));

        if (imageRequest.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        // NOTE: per requirements, we do not validate/limit sizes.
        // We only reshape "WxH" => "W*H" for DashScope.
        var dashScopeSize = MapGenericSizeToDashScope(imageRequest.Size);

        // Route providerOptions based on model family.
        var (promptExtend, negativePrompt, watermark) = ResolveDashScopeParams(imageRequest.Model, providerMetadata);

        var content = new List<object>();
        foreach (var image in inputImages)
            content.Add(new { image = ToDashScopeImageDataUrl(image) });
        content.Add(new { text = imageRequest.Prompt });

        var payload = new
        {
            model = imageRequest.Model,
            input = new
            {
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content
                    }
                }
            },
            parameters = new
            {
                // qwen-only (ignored by tongyi z-image)
                negative_prompt = negativePrompt,
                watermark,

                // shared
                prompt_extend = promptExtend,
                prompt_extend_mode = providerMetadata?.Qwen?.PromptExtendMode,
                enable_thinking = providerMetadata?.Qwen?.EnableThinking,
                size = dashScopeSize,
                seed = imageRequest.Seed,
                n = imageRequest.N
            }
        };

        // Singapore-only: always use intl base.
        var baseUrl = DefaultDashScopeBaseUrl;

        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri($"{baseUrl}{QwenImageSyncPath}"))
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"{resp.StatusCode}: {raw}");

        var (imageUrls, _) = ExtractImagesAndTextFromSyncResponse(raw);
        if (imageUrls.Count == 0)
            throw new Exception("DashScope response did not contain an image URL.");

        List<string> images = [];
        foreach (var imageUrl in imageUrls)
        {
            var bytes = await _client.GetByteArrayAsync(imageUrl, cancellationToken);
            images.Add(Convert.ToBase64String(bytes).ToDataUrl(MediaTypeNames.Image.Png));
        }

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = imageRequest.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private static string? MapGenericSizeToDashScope(string? genericSize)
    {
        if (string.IsNullOrWhiteSpace(genericSize))
            return null;

        // request uses "1664x928" while DashScope expects "1664*928"
        return genericSize.Trim().ToLowerInvariant().Replace('x', '*');
    }

    private static (bool? PromptExtend, string? NegativePrompt, bool? Watermark) ResolveDashScopeParams(
        string modelName,
        AlibabaImageProviderMetadata? metadata)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        // Tongyi Z-Image models (text-to-image)
        if (string.Equals(modelName, "z-image-turbo", StringComparison.OrdinalIgnoreCase))
        {
            return (
                metadata?.Tongyi?.PromptExtend,
                null,
                null);
        }

        // Default to Qwen Image behavior.
        return (
            metadata?.Qwen?.PromptExtend,
            metadata?.Qwen?.NegativePrompt,
            metadata?.Qwen?.Watermark);
    }
}

