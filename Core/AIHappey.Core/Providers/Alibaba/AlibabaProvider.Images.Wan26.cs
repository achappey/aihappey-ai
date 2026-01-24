using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Alibaba;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Alibaba;

public partial class AlibabaProvider
{
    private static bool IsWan26Model(string modelName)
        => modelName.StartsWith("wan2.6-", StringComparison.OrdinalIgnoreCase);

    private static bool IsWan26T2i(string modelName)
        => string.Equals(modelName, "wan2.6-t2i", StringComparison.OrdinalIgnoreCase);

    private static bool IsWan26Image(string modelName)
        => string.Equals(modelName, "wan2.6-image", StringComparison.OrdinalIgnoreCase);

    private static string ToDashScopeImageDataUrl(ImageFile file)
        => $"data:{file.MediaType};base64,{file.Data}";

    private async Task<ImageResponse> Wan26ImageRequest(
        ImageRequest imageRequest,
        AlibabaWanImageOptions? wan,
        string modelName,
        List<object> warnings,
        DateTime now,
        CancellationToken cancellationToken)
    {
        // Singapore/intl only for now.
        var baseUrl = DefaultDashScopeBaseUrl;

        if (imageRequest.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        var dashScopeSize = MapGenericSizeToDashScope(imageRequest.Size);
        var inputImages = imageRequest.Files?.ToList() ?? [];

        if (IsWan26T2i(modelName) && inputImages.Count > 0)
        {
            warnings.Add(new { type = "unsupported", feature = "files", details = "wan2.6-t2i is text-to-image only; input images were ignored." });
            inputImages = [];
        }

        bool enableInterleave;
        if (IsWan26T2i(modelName))
        {
            enableInterleave = false;
        }
        else if (IsWan26Image(modelName))
        {
            // Auto-switch based on presence of input images.
            if (inputImages.Count == 0)
            {
                enableInterleave = true;
                warnings.Add(new { type = "mode_auto_selected", enable_interleave = true, details = "No input images provided; switched to mixed text-and-image mode (text-to-image)." });
            }
            else
            {
                enableInterleave = false;
            }

            if (wan?.EnableInterleave is not null && wan.EnableInterleave.Value != enableInterleave)
            {
                warnings.Add(new { type = "ignored", feature = "enable_interleave", details = "enable_interleave was inferred from input images and provider option was ignored." });
            }
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(modelName), modelName, "Unsupported Wan model.");
        }

        // Validate image count vs mode.
        if (!enableInterleave)
        {
            if (IsWan26Image(modelName) && (inputImages.Count < 1 || inputImages.Count > 4))
                throw new ArgumentException("wan2.6-image edit mode requires 1 to 4 input images.", nameof(imageRequest));
        }
        else
        {
            if (inputImages.Count > 1)
                throw new ArgumentException("wan2.6-image mixed mode supports at most 1 input image.", nameof(imageRequest));
        }

        // n semantics.
        int? n = imageRequest.N;
        if (enableInterleave)
        {
            if (n is > 1)
                throw new ArgumentException("In mixed mode (enable_interleave=true), n must be 1.", nameof(imageRequest));

            n = 1;
        }
        else
        {
            if (IsWan26Image(modelName))
            {
                n ??= 1;
                if (n is < 1 or > 4)
                    throw new ArgumentException("n must be between 1 and 4 for wan2.6-image edit mode.", nameof(imageRequest));
            }
            else if (IsWan26T2i(modelName))
            {
                n ??= 1;
                if (n is < 1 or > 4)
                    throw new ArgumentException("n must be between 1 and 4 for wan2.6-t2i.", nameof(imageRequest));
            }
        }

        // Build message content.
        var userContent = new List<object> { new { text = imageRequest.Prompt } };
        foreach (var img in inputImages)
            userContent.Add(new { image = ToDashScopeImageDataUrl(img) });

        var parameters = new Dictionary<string, object?>
        {
            ["prompt_extend"] = wan?.PromptExtend,
            ["watermark"] = wan?.Watermark,
            ["negative_prompt"] = wan?.NegativePrompt,
            ["size"] = dashScopeSize,
            ["seed"] = imageRequest.Seed,
            ["n"] = n
        };

        if (IsWan26Image(modelName))
        {
            parameters["enable_interleave"] = enableInterleave;
            if (enableInterleave)
                parameters["max_images"] = wan?.MaxImages;
        }

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
                        content = userContent
                    }
                }
            },
            parameters
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri($"{baseUrl}{QwenImageSyncPath}"))
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"{resp.StatusCode}: {raw}");

        var (imageUrls, texts) = ExtractImagesAndTextFromSyncResponse(raw);

        if (texts.Count > 0)
            warnings.Add(new { type = "dropped", feature = "text_output", details = "Wan mixed mode may return text+images; this endpoint returns images only." });

        if (imageUrls.Count == 0)
            throw new Exception("DashScope response did not contain any image URLs.");

        List<string> images = [];
        foreach (var url in imageUrls)
        {
            var bytes = await _client.GetByteArrayAsync(url, cancellationToken);
            images.Add(Convert.ToBase64String(bytes).ToDataUrl(MediaTypeNames.Image.Png));
        }

        Dictionary<string, JsonElement>? providerMetadata = null;
        if (texts.Count > 0)
        {
            providerMetadata = new Dictionary<string, JsonElement>
            {
                ["wan_text"] = JsonSerializer.SerializeToElement(texts, JsonSerializerOptions.Web)
            };
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
                Body = JsonDocument.Parse(raw).RootElement.Clone()
            }
        };
    }
}

