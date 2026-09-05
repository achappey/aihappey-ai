using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.HiNow;

public partial class HiNowProvider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("Prompt is required.", nameof(request));

        var payload = GetHiNowOptions(request.ProviderOptions);
        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;
        payload["async"] = false;
        var parameters = payload["parameters"] as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
        SetHiNow(parameters, "aspect_ratio", request.AspectRatio);
        if (string.IsNullOrWhiteSpace(request.AspectRatio) && TryConvertHiNowSize(request.Size, out var ratio)) parameters["aspect_ratio"] = ratio;
        if (parameters.Count > 0) payload["parameters"] = parameters;
        var inputs = request.Files?.Where(x => x is not null).Select(ToHiNowImageValue).ToArray() ?? [];
        if (inputs.Length > 0) payload["images"] = JsonSerializer.SerializeToNode(inputs, HiNowJson);

        var result = await SendHiNowJsonAsync(HttpMethod.Post, "v1/images", payload, "image generation", cancellationToken);
        var data = GetHiNowData(result.Root);
        var urls = GetHiNowUrls(data);
        if (urls.Count == 0) throw new InvalidOperationException("HiNow image response did not contain an image URL.");
        var images = new List<string>(urls.Count);
        foreach (var url in urls)
        {
            var media = await DownloadHiNowMediaAsync(url, "image/jpeg", cancellationToken);
            images.Add(Convert.ToBase64String(media.Bytes).ToDataUrl(media.MediaType));
        }
        var warnings = new List<object>();
        if (request.N is > 1) warnings.Add(new { type = "unsupported", feature = "n", details = "HiNow returns one image per call." });
        if (request.Mask is not null) warnings.Add(new { type = "unsupported", feature = "mask" });
        if (request.Seed is not null) warnings.Add(new { type = "unsupported", feature = "seed" });
        return new ImageResponse
        {
            Images = images, Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root),
            Response = new HeaderResponseData
            {
                Timestamp = DateTime.UtcNow, Headers = result.Headers,
                ModelId = (GetHiNowString(data, "model") ?? request.Model).ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        return (await ImageRequest(options.ToImageRequest(options.Model, GetIdentifier()), cancellationToken)).ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await ImageRequest(options.ToImageRequest(options.Model, GetIdentifier()), cancellationToken);
        foreach (var item in response.ToOpenAIImageGenerationCompletedEvents(options)) yield return item;
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        return (await ImageRequest(await options.ToImageRequest(options.Model, GetIdentifier(), cancellationToken), cancellationToken)).ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await ImageRequest(await options.ToImageRequest(options.Model, GetIdentifier(), cancellationToken), cancellationToken);
        foreach (var item in response.ToOpenAIImageEditCompletedEvents(options)) yield return item;
    }

    private static string ToHiNowImageValue(ImageFile image)
    {
        if (image.Type.Equals("url", StringComparison.OrdinalIgnoreCase) || image.Data.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return image.Data;
        return image.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? image.Data : $"data:{image.MediaType};base64,{image.Data}";
    }

    private static bool TryConvertHiNowSize(string? size, out string? ratio)
    {
        ratio = size?.ToLowerInvariant() switch { "1024x1024" => "1:1", "1536x1024" => "3:2", "1024x1536" => "2:3", _ => null };
        return ratio is not null;
    }
}
