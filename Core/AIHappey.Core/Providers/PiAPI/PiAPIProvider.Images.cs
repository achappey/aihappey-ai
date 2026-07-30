using System.Net.Mime;
using System.Runtime.CompilerServices;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.PiAPI;

public partial class PiAPIProvider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var input = CreateImageInput(request.Prompt, request.Size, request.AspectRatio, request.Seed, request.N);
        if (request.Files is not null)
            input["image_urls"] = request.Files.Select(ToPiApiMediaValue).ToArray();
        if (request.Mask is not null)
            input["mask"] = ToPiApiMediaValue(request.Mask);

        var task = await CreateAndWaitForMediaTaskAsync(request.Model, "txt2img", input, request.ProviderOptions, cancellationToken);
        var images = new List<string>();
        foreach (var output in GetOutputValues(task.Result.Root, "image_url", "image_urls", "images"))
        {
            var image = await DownloadMediaAsync(output, MediaTypeNames.Image.Png, cancellationToken);
            images.Add(ToDataUrl(image.Base64, image.MimeType));
        }

        if (images.Count == 0)
            throw new InvalidOperationException("PiAPI image task completed without generated images.");

        return new ImageResponse
        {
            Images = images,
            ProviderMetadata = CreateMediaProviderMetadata(task.Create, task.Result),
            Response = new HeaderResponseData
            {
                Timestamp = DateTime.UtcNow,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
        OpenAIImageGenerationRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var task = await CreateAndWaitForMediaTaskAsync(
            options.Model,
            "txt2img",
            CreateImageInput(options.Prompt, options.Size, null, null, options.N),
            options.AdditionalProperties,
            cancellationToken);

        var images = new List<OpenAIImageData>();
        foreach (var output in GetOutputValues(task.Result.Root, "image_url", "image_urls", "images"))
        {
            var image = await DownloadMediaAsync(output, MediaTypeNames.Image.Png, cancellationToken);
            images.Add(new OpenAIImageData { B64Json = image.Base64 });
        }

        if (images.Count == 0)
            throw new InvalidOperationException("PiAPI image task completed without generated images.");

        return new OpenAIImagesResponse
        {
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Data = images,
            Background = options.Background,
            OutputFormat = options.OutputFormat,
            Quality = options.Quality,
            Size = options.Size
        };
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageGenerationRequestAsync(options, cancellationToken);
        foreach (var image in response.Data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(image.B64Json))
            {
                yield return new OpenAIImageGenerationCompleted
                {
                    B64Json = image.B64Json,
                    CreatedAt = response.Created,
                    Background = response.Background,
                    OutputFormat = response.OutputFormat,
                    Quality = response.Quality,
                    Size = response.Size,
                    Usage = response.Usage
                };
            }
        }
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        var input = CreateImageInput(options.Prompt, options.Size, null, null, options.N);
        var images = new List<string>();

        if (options.Images is not null)
            images.AddRange(options.Images.Select(image => image.ImageUrl).Where(url => !string.IsNullOrWhiteSpace(url))!);
        if (options.ImageFiles is not null)
        {
            foreach (var file in options.ImageFiles)
            {
                await using var stream = file.OpenReadStream();
                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory, cancellationToken);
                images.Add(ToDataUrl(Convert.ToBase64String(memory.ToArray()), file.ContentType ?? MediaTypeNames.Image.Png));
            }
        }
        if (images.Count > 0)
            input["image_urls"] = images;
        if (!string.IsNullOrWhiteSpace(options.Mask?.ImageUrl))
            input["mask"] = options.Mask.ImageUrl;

        var task = await CreateAndWaitForMediaTaskAsync(options.Model, "img2img", input, options.AdditionalProperties, cancellationToken);
        var generated = new List<OpenAIImageData>();
        foreach (var output in GetOutputValues(task.Result.Root, "image_url", "image_urls", "images"))
        {
            var image = await DownloadMediaAsync(output, MediaTypeNames.Image.Png, cancellationToken);
            generated.Add(new OpenAIImageData { B64Json = image.Base64 });
        }

        if (generated.Count == 0)
            throw new InvalidOperationException("PiAPI image edit task completed without generated images.");

        return new OpenAIImagesResponse
        {
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Data = generated,
            Background = options.Background,
            OutputFormat = options.OutputFormat,
            Quality = options.Quality,
            Size = options.Size
        };
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageEditRequestAsync(options, cancellationToken);
        foreach (var image in response.Data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(image.B64Json))
            {
                yield return new OpenAIImageEditCompleted
                {
                    B64Json = image.B64Json,
                    CreatedAt = response.Created,
                    Background = response.Background,
                    OutputFormat = response.OutputFormat,
                    Quality = response.Quality,
                    Size = response.Size,
                    Usage = response.Usage
                };
            }
        }
    }

    private static Dictionary<string, object?> CreateImageInput(string prompt, string? size, string? aspectRatio, int? seed, int? n)
    {
        var input = new Dictionary<string, object?>
        {
            ["prompt"] = prompt,
            ["seed"] = seed,
            ["batch_size"] = n,
            ["aspect_ratio"] = aspectRatio
        };

        if (!string.IsNullOrWhiteSpace(size))
        {
            var dimensions = size.ToLowerInvariant().Split('x', StringSplitOptions.TrimEntries);
            if (dimensions.Length == 2 && int.TryParse(dimensions[0], out var width) && int.TryParse(dimensions[1], out var height))
            {
                input["width"] = width;
                input["height"] = height;
            }
            else
                input["size"] = size;
        }

        return input;
    }

    private static string ToPiApiMediaValue(ImageFile file)
        => file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || file.Data.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || file.Data.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? file.Data
                : ToDataUrl(file.Data, string.IsNullOrWhiteSpace(file.MediaType) ? MediaTypeNames.Image.Png : file.MediaType);
}
