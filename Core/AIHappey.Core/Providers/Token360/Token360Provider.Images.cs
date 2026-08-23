using System.Net.Mime;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Token360;

public partial class Token360Provider
{
    private async Task<ImageResponse> Token360ImageRequestAsync(
        ImageRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var additional = Token360ProviderOptions(request.ProviderOptions);
        Token360Set(additional, "aspect_ratio", request.AspectRatio);
        Token360Set(additional, "seed", request.Seed);

        var files = request.Files?.Where(x => !string.IsNullOrWhiteSpace(x.Data)).ToArray() ?? [];
        if (files.Length > 0)
            additional["images"] = JsonSerializer.SerializeToElement(files.Select(Token360ImageValue).ToArray());
        if (request.Mask is { Data.Length: > 0 })
            additional["mask"] = JsonSerializer.SerializeToElement(Token360ImageValue(request.Mask));

        var openAIRequest = new OpenAIImageGenerationRequest
        {
            Model = request.Model,
            Prompt = request.Prompt,
            N = request.N,
            Size = request.Size,
            ResponseFormat = "b64_json",
            AdditionalProperties = additional.Count == 0 ? null : additional
        };

        var response = await OpenAIImageGenerationRequestAsync(openAIRequest, cancellationToken);
        var images = await Token360NormalizeImagesAsync(response.Data ?? [], cancellationToken);
        if (images.Count == 0)
            throw new InvalidOperationException("Token360 image response contained no images.");

        return new ImageResponse
        {
            Images = images,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(response),
            Usage = response.Usage is null ? null : new ImageUsageData
            {
                InputTokens = response.Usage.InputTokens,
                OutputTokens = response.Usage.OutputTokens,
                TotalTokens = response.Usage.TotalTokens
            },
            Response = new HeaderResponseData
            {
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Timestamp = response.Created > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(response.Created).UtcDateTime
                    : DateTime.UtcNow
            }
        };
    }

    private async Task<OpenAIImageGenerationRequest> Token360TranslateEditRequestAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken)
    {
        var imageRequest = await options.ToImageRequest(options.Model, GetIdentifier(), cancellationToken);
        var additional = Token360ProviderOptions(imageRequest.ProviderOptions);
        var files = imageRequest.Files?.Where(x => !string.IsNullOrWhiteSpace(x.Data)).ToArray() ?? [];
        additional["images"] = JsonSerializer.SerializeToElement(files.Select(Token360ImageValue).ToArray());
        if (imageRequest.Mask is { Data.Length: > 0 })
            additional["mask"] = JsonSerializer.SerializeToElement(Token360ImageValue(imageRequest.Mask));
        Token360Set(additional, "input_fidelity", options.InputFidelity);

        return new OpenAIImageGenerationRequest
        {
            Model = options.Model,
            Prompt = options.Prompt,
            N = options.N,
            Size = options.Size,
            Quality = options.Quality,
            OutputFormat = options.OutputFormat,
            Moderation = options.Moderation,
            PartialImages = options.PartialImages,
            Stream = options.Stream,
            AdditionalProperties = additional
        };
    }

    private static Dictionary<string, JsonElement> Token360ProviderOptions(
        Dictionary<string, JsonElement>? providerOptions)
    {
        if (providerOptions?.TryGetValue("token360", out var value) != true
            || value.ValueKind != JsonValueKind.Object)
            return [];

        return value.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone(), StringComparer.OrdinalIgnoreCase);
    }

    private static string Token360ImageValue(ImageFile image)
        => image.Data.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || image.Data.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || image.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                ? image.Data
                : image.Data.ToDataUrl(string.IsNullOrWhiteSpace(image.MediaType) ? MediaTypeNames.Image.Png : image.MediaType);

    private static void Token360Set(Dictionary<string, JsonElement> target, string name, object? value)
    {
        if (value is not null && (value is not string text || !string.IsNullOrWhiteSpace(text)))
            target[name] = JsonSerializer.SerializeToElement(value);
    }

    private async Task<List<string>> Token360NormalizeImagesAsync(
        IEnumerable<OpenAIImageData> data,
        CancellationToken cancellationToken)
    {
        List<string> images = [];
        foreach (var item in data)
        {
            if (!string.IsNullOrWhiteSpace(item.B64Json))
            {
                images.Add(item.B64Json.ToDataUrl(MediaTypeNames.Image.Png));
                continue;
            }

#pragma warning disable CS0618
            if (!string.IsNullOrWhiteSpace(item.Url))
            {
                using var response = await _client.GetAsync(item.Url, cancellationToken);
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Token360 image download failed ({(int)response.StatusCode}).");
                images.Add(Convert.ToBase64String(bytes).ToDataUrl(
                    response.Content.Headers.ContentType?.MediaType ?? MediaTypeNames.Image.Png));
            }
#pragma warning restore CS0618
        }
        return images;
    }
}
