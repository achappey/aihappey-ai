using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Volink;

public partial class VolinkProvider
{
    public async Task<ImageResponse> ImageRequest(
        ImageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);

        var files = request.Files?.ToArray() ?? [];
        if (files.Length > 0 && string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required for image edits.", nameof(request));
        if (files.Length == 0 && string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required for image generation.", nameof(request));

        List<object> warnings = [];
        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });
        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });

        var options = VolinkImageOptions(request.ProviderOptions);
        OpenAIImagesResponse response;
        if (files.Length == 0)
        {
            response = await OpenAIImageGenerationRequestAsync(new OpenAIImageGenerationRequest
            {
                Model = request.Model,
                Prompt = request.Prompt,
                N = request.N,
                Size = request.Size,
                ResponseFormat = "b64_json",
                AdditionalProperties = options
            }, cancellationToken);
        }
        else
        {
            response = await OpenAIImageEditRequestAsync(new OpenAIImageEditRequest
            {
                Model = request.Model,
                Prompt = request.Prompt,
                N = request.N,
                Size = request.Size,
                Images = files.Select(ToVolinkImageReference).ToArray(),
                Mask = request.Mask is null ? null : ToVolinkImageReference(request.Mask),
                AdditionalProperties = options
            }, cancellationToken);
        }

        var format = response.OutputFormat ?? "png";
        return new ImageResponse
        {
            Images = (response.Data ?? [])
                .Where(image => !string.IsNullOrWhiteSpace(image.B64Json))
                .Select(image => $"data:{VolinkImageMimeType(format)};base64,{image.B64Json}"),
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(JsonSerializer.SerializeToElement(response)),
            Usage = response.Usage is null ? null : new ImageUsageData
            {
                InputTokens = response.Usage.InputTokens,
                OutputTokens = response.Usage.OutputTokens,
                TotalTokens = response.Usage.TotalTokens
            },
            Response = new HeaderResponseData
            {
                Timestamp = DateTime.UtcNow,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
        OpenAIImageGenerationRequest options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return _client.OpenAICompatibleImageGenerationRequestAsync(
            options, cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return _client.OpenAICompatibleImageGenerationStreamingAsync(
            options, cancellationToken: cancellationToken);
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return _client.OpenAICompatibleImageEditRequestAsync(
            options, cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return _client.OpenAICompatibleImageEditStreamingAsync(
            options, cancellationToken: cancellationToken);
    }

    private static OpenAIImageReference ToVolinkImageReference(ImageFile file)
    {
        if (file is ImageFileUrl url)
            return new OpenAIImageReference { ImageUrl = url.Url };
        if (string.IsNullOrWhiteSpace(file.Data) || string.IsNullOrWhiteSpace(file.MediaType))
            throw new ArgumentException("Image file data and media type are required.", nameof(file));

        return new OpenAIImageReference
        {
            ImageUrl = file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                ? file.Data
                : $"data:{file.MediaType};base64,{file.Data}"
        };
    }

    private static Dictionary<string, JsonElement>? VolinkImageOptions(
        Dictionary<string, JsonElement>? providerOptions)
    {
        if (providerOptions is null)
            return null;
        return providerOptions.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
    }

    private static string VolinkImageMimeType(string format)
        => format.Trim().ToLowerInvariant() switch
        {
            "jpeg" or "jpg" => "image/jpeg",
            "webp" => "image/webp",
            _ => "image/png"
        };
}
