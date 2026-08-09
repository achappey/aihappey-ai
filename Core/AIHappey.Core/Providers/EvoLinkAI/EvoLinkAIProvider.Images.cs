using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;

namespace AIHappey.Core.Providers.EvoLinkAI;

public partial class EvoLinkAIProvider
{
    private const string EvoLinkAIImageEndpoint = "v1/images/generations";

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var providerOptions = GetEvoLinkAIProviderOptions(request.ProviderOptions);
        var payload = CreateEvoLinkAIPassthroughPayload(providerOptions);
        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;
        if (!string.IsNullOrWhiteSpace(request.Size)) payload["size"] = request.Size;
        else if (!string.IsNullOrWhiteSpace(request.AspectRatio)) payload["size"] = request.AspectRatio;
        if (request.Seed is not null) payload["seed"] = request.Seed;
        if (request.N is not null) payload["n"] = request.N;

        var imageUrls = new List<string>();
        foreach (var file in request.Files ?? [])
            imageUrls.Add(await ResolveEvoLinkAIInputUrlAsync(file.Data, file.MediaType, null, cancellationToken));
        if (imageUrls.Count > 0) payload["image_urls"] = imageUrls;
        if (request.Mask is not null)
            payload["mask_url"] = await ResolveEvoLinkAIInputUrlAsync(request.Mask.Data, request.Mask.MediaType, "mask.png", cancellationToken);

        var operation = await CreateAndWaitEvoLinkAIImageAsync(payload, providerOptions, cancellationToken);
        var images = await ReadEvoLinkAIImagesAsync(operation.Terminal.Root, cancellationToken);

        return new ImageResponse
        {
            Images = images,
            Warnings = [],
            ProviderMetadata = CreateEvoLinkAIMetadata(
                EvoLinkAIImageEndpoint, payload, operation.CreateRoot, operation.Terminal.Root,
                operation.Terminal.TaskId, operation.Terminal.Status, operation.CreateHeaders, operation.Terminal.Headers),
            Response = new HeaderResponseData
            {
                Timestamp = ResolveEvoLinkAITimestamp(operation.Terminal.Root, DateTime.UtcNow),
                Headers = operation.Terminal.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
        OpenAIImageGenerationRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var request = new ImageRequest
        {
            Model = options.Model,
            Prompt = options.Prompt,
            N = options.N,
            Size = options.Size,
            ProviderOptions = CreateEvoLinkAIAdditionalProviderOptions(options.AdditionalProperties, options.Quality, null)
        };
        return ToEvoLinkAIOpenAIImagesAsync(ImageRequest(request, cancellationToken), options.Background, options.OutputFormat, options.Quality, options.Size);
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

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ValidateOpenAIImageEditRequest();

        var files = new List<ImageFile>();
        foreach (var reference in options.Images ?? [])
        {
            if (!string.IsNullOrWhiteSpace(reference.ImageUrl))
                files.Add(new ImageFile { Data = reference.ImageUrl, MediaType = MediaTypeNames.Image.Png });
        }
        foreach (var file in options.ImageFiles ?? [])
            files.Add(await ToEvoLinkAIImageFileAsync(file, cancellationToken));

        ImageFile? mask = null;
        if (!string.IsNullOrWhiteSpace(options.Mask?.ImageUrl))
            mask = new ImageFile { Data = options.Mask.ImageUrl, MediaType = MediaTypeNames.Image.Png };
        else if (options.MaskFile is not null)
            mask = await ToEvoLinkAIImageFileAsync(options.MaskFile, cancellationToken);

        var request = new ImageRequest
        {
            Model = options.Model,
            Prompt = options.Prompt,
            N = options.N,
            Size = options.Size,
            Files = files,
            Mask = mask,
            ProviderOptions = CreateEvoLinkAIAdditionalProviderOptions(options.AdditionalProperties, options.Quality, options.InputFidelity)
        };
        return await ToEvoLinkAIOpenAIImagesAsync(ImageRequest(request, cancellationToken), options.Background, options.OutputFormat, options.Quality, options.Size);
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

    private async Task<(JsonElement CreateRoot, EvoLinkAITaskResult Terminal, Dictionary<string, string> CreateHeaders)> CreateAndWaitEvoLinkAIImageAsync(
        Dictionary<string, object?> payload,
        JsonElement? providerOptions,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Post, EvoLinkAIImageEndpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, EvoLinkAISpeechJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"EvoLinkAI image request failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var createRoot = document.RootElement.Clone();
        var terminal = await WaitForEvoLinkAITaskAsync(createRoot, providerOptions, cancellationToken);
        if (!IsEvoLinkAISuccessStatus(terminal.Status) && !HasEvoLinkAIResults(terminal.Root))
            throw new InvalidOperationException($"EvoLinkAI image generation failed with status '{terminal.Status}': {GetEvoLinkAITaskError(terminal.Root)}");
        return (createRoot, terminal, response.GetHeaders());
    }

    private async Task<List<string>> ReadEvoLinkAIImagesAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var images = new List<string>();
        foreach (var url in GetEvoLinkAIResultUrls(root, "image"))
        {
            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                images.Add(url);
                continue;
            }

            var media = await DownloadEvoLinkAIMediaAsync(url, GuessEvoLinkAIImageMediaType(url) ?? MediaTypeNames.Image.Png, cancellationToken);
            images.Add(Convert.ToBase64String(media.Bytes).ToDataUrl(media.MediaType));
        }

        return images.Count > 0
            ? images
            : throw new InvalidOperationException("No image result returned from EvoLinkAI image task.");
    }

    private static async Task<ImageFile> ToEvoLinkAIImageFileAsync(IFormFile file, CancellationToken cancellationToken)
    {
        await using var stream = file.OpenReadStream();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);
        return new ImageFile
        {
            Data = Convert.ToBase64String(memory.ToArray()),
            MediaType = file.ContentType ?? MediaTypeNames.Image.Png
        };
    }

    private Dictionary<string, JsonElement>? CreateEvoLinkAIAdditionalProviderOptions(
        Dictionary<string, JsonElement>? additional,
        string? quality,
        string? inputFidelity)
    {
        var options = new Dictionary<string, object?>();
        foreach (var property in additional ?? []) options[property.Key] = property.Value.Clone();
        if (!string.IsNullOrWhiteSpace(quality)) options["quality"] = quality;
        if (!string.IsNullOrWhiteSpace(inputFidelity)) options["input_fidelity"] = inputFidelity;
        return options.Count == 0
            ? null
            : new Dictionary<string, JsonElement> { [GetIdentifier()] = JsonSerializer.SerializeToElement(options, EvoLinkAISpeechJsonOptions) };
    }

    private static async Task<OpenAIImagesResponse> ToEvoLinkAIOpenAIImagesAsync(
        Task<ImageResponse> responseTask,
        string? background,
        string? outputFormat,
        string? quality,
        string? size)
    {
        var response = await responseTask;
        return new OpenAIImagesResponse
        {
            Created = new DateTimeOffset(response.Response.Timestamp).ToUnixTimeSeconds(),
            Background = background,
            OutputFormat = outputFormat,
            Quality = quality,
            Size = size,
            Data = response.Images?.Select(image => new OpenAIImageData
            {
                B64Json = image[(image.IndexOf(',') + 1)..]
            }).ToList()
        };
    }

    private static string? GuessEvoLinkAIImageMediaType(string url)
        => Path.GetExtension(Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => MediaTypeNames.Image.Jpeg,
            ".gif" => MediaTypeNames.Image.Gif,
            ".webp" => "image/webp",
            ".png" => MediaTypeNames.Image.Png,
            _ => null
        };
}
