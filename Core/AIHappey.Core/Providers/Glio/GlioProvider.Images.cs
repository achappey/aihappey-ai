using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.Glio;

public partial class GlioProvider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        var options = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var payload = CopyGlioRootOptions(options);
        var parameters = GetGlioParams(payload);
        var files = request.Files?.Where(file => file is not null).ToList() ?? [];

        payload["model"] = request.Model;
        payload["action"] = files.Count > 0 || request.Mask is not null ? "modify" : "generate";
        parameters["prompt"] = request.Prompt;
        SetGlioValue(parameters, "size", request.Size);
        SetGlioValue(parameters, "aspect_ratio", request.AspectRatio);
        SetGlioValue(parameters, "seed", request.Seed);
        SetGlioValue(parameters, "n", request.N);

        if (files.Count > 0)
        {
            var imagesInput = files.Select(file => ToGlioDataUrl(file.Data, file.MediaType)).ToList();
            parameters["image"] = imagesInput[0];
            parameters["images"] = imagesInput;
        }
        if (request.Mask is not null)
            parameters["mask"] = ToGlioDataUrl(request.Mask.Data, request.Mask.MediaType);

        var job = await RunGlioJobAsync(payload, cancellationToken);
        var images = new List<string>(job.Urls.Count);
        foreach (var url in job.Urls)
        {
            var media = await DownloadGlioMediaAsync(url, GuessGlioImageMediaType(url), cancellationToken);
            images.Add($"data:{media.MediaType};base64,{Convert.ToBase64String(media.Bytes)}");
        }

        var deletion = await DeleteGlioJobAsync(job.JobId, cancellationToken);
        job = job with { Delete = deletion };

        return new ImageResponse
        {
            Images = images,
            ProviderMetadata = CreateGlioJobMetadata(job),
            Response = new()
            {
                Timestamp = now,
                Headers = job.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
        OpenAIImageGenerationRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var response = await ImageRequest(options.ToImageRequest(options.Model, GetIdentifier()), cancellationToken);
        return response.ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var response = await ImageRequest(options.ToImageRequest(options.Model, GetIdentifier()), cancellationToken);
        foreach (var part in response.ToOpenAIImageGenerationCompletedEvents(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return part;
        }
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        var request = await options.ToImageRequest(options.Model, GetIdentifier(), cancellationToken);
        var response = await ImageRequest(request, cancellationToken);
        return response.ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        var request = await options.ToImageRequest(options.Model, GetIdentifier(), cancellationToken);
        var response = await ImageRequest(request, cancellationToken);
        foreach (var part in response.ToOpenAIImageEditCompletedEvents(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return part;
        }
    }

    private static void SetGlioValue(Dictionary<string, object?> parameters, string name, object? value)
    {
        if (value is not null && (value is not string text || !string.IsNullOrWhiteSpace(text)))
            parameters[name] = value;
    }

    private static string GuessGlioImageMediaType(string url)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => MediaTypeNames.Image.Jpeg,
            ".webp" => "image/webp",
            ".gif" => MediaTypeNames.Image.Gif,
            _ => MediaTypeNames.Image.Png
        };
    }
}
