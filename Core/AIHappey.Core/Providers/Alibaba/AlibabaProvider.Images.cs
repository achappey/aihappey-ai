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

        var modelName = NormalizeAlibabaModelName(imageRequest.Model);

        if (IsWan26Model(modelName))
            return await Wan26ImageRequest(imageRequest, providerMetadata?.Wan, modelName, warnings, now, cancellationToken);

        if (imageRequest.N is > 1)
            warnings.Add(new { type = "unsupported", feature = "n", details = "DashScope Qwen-Image returns exactly 1 image." });

        if (imageRequest.Files?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "files" });

        if (imageRequest.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        // NOTE: per requirements, we do not validate/limit sizes.
        // We only reshape "WxH" => "W*H" for DashScope.
        var dashScopeSize = MapGenericSizeToDashScope(imageRequest.Size);

        // Route providerOptions based on model family.
        var (promptExtend, negativePrompt, watermark) = ResolveDashScopeParams(modelName, providerMetadata);

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
                // qwen-only (ignored by tongyi z-image)
                negative_prompt = negativePrompt,
                watermark,

                // shared
                prompt_extend = promptExtend,
                size = dashScopeSize,
                seed = imageRequest.Seed
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

        var bytes = await _client.GetByteArrayAsync(imageUrls[0], cancellationToken);
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

