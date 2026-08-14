using System.Runtime.CompilerServices;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Vidu;

public partial class ViduProvider
{

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        ValidateViduOpenAIImageCount(options.N);

        var request = new ImageRequest
        {
            Model = options.Model,
            Prompt = options.Prompt,
            N = options.N,
            Size = options.Size,
            AspectRatio = ReadString(options.AdditionalProperties, "aspect_ratio", "aspectRatio"),
            Seed = ReadInt(options.AdditionalProperties, "seed"),
            ProviderOptions = CreateViduImageProviderOptions(options.AdditionalProperties)
        };

        return ToOpenAIImagesResponse(await ImageRequest(request, cancellationToken), options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageGenerationRequestAsync(options, cancellationToken);
        foreach (var image in response.Data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(image.B64Json))
                continue;
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

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        ValidateViduOpenAIImageCount(options.N);
        if (options.Mask is not null || options.MaskFile is not null)
            throw new NotSupportedException("Vidu reference-to-image does not support masks.");

        var files = new List<ImageFile>();
        foreach (var image in options.Images ?? [])
        {
            if (!string.IsNullOrWhiteSpace(image.FileId))
                throw new NotSupportedException("Vidu does not support OpenAI file IDs for image edits.");
            if (string.IsNullOrWhiteSpace(image.ImageUrl))
                throw new ArgumentException("Each Vidu reference image requires image_url.", nameof(options));
            files.Add(new ImageFile { Data = image.ImageUrl, MediaType = GuessImageMediaType(image.ImageUrl) ?? "image/png" });
        }

        foreach (var file in options.ImageFiles ?? [])
        {
            await using var stream = file.OpenReadStream();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, cancellationToken);
            files.Add(new ImageFile
            {
                Data = Convert.ToBase64String(memory.ToArray()),
                MediaType = string.IsNullOrWhiteSpace(file.ContentType) ? "image/png" : file.ContentType
            });
        }

        if (files.Count is < 1 or > 7)
            throw new ArgumentException("Vidu image edits require between 1 and 7 reference images.", nameof(options));

        var request = new ImageRequest
        {
            Model = options.Model,
            Prompt = options.Prompt,
            N = options.N,
            Size = options.Size,
            AspectRatio = ReadString(options.AdditionalProperties, "aspect_ratio", "aspectRatio"),
            Seed = ReadInt(options.AdditionalProperties, "seed"),
            Files = files,
            ProviderOptions = CreateViduImageProviderOptions(options.AdditionalProperties)
        };

        return ToOpenAIImagesResponse(await ImageRequest(request, cancellationToken), options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageEditRequestAsync(options, cancellationToken);
        foreach (var image in response.Data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(image.B64Json))
                continue;
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

    private static void ValidateViduOpenAIImageCount(int? n)
    {
        if (n is > 1)
            throw new NotSupportedException("Vidu currently returns one image per task.");
    }

    private Dictionary<string, System.Text.Json.JsonElement>? CreateViduImageProviderOptions(Dictionary<string, System.Text.Json.JsonElement>? properties)
    {
        if (properties is null || properties.Count == 0)
            return null;
        var raw = new Dictionary<string, System.Text.Json.JsonElement>(properties, StringComparer.OrdinalIgnoreCase);
        raw.Remove("seed");
        raw.Remove("aspect_ratio");
        raw.Remove("aspectRatio");
        return new() { [GetIdentifier()] = System.Text.Json.JsonSerializer.SerializeToElement(raw) };
    }

    private static string? ReadString(Dictionary<string, System.Text.Json.JsonElement>? properties, params string[] names)
    {
        foreach (var name in names)
            if (properties?.TryGetValue(name, out var value) == true && value.ValueKind == System.Text.Json.JsonValueKind.String)
                return value.GetString();
        return null;
    }

    private static int? ReadInt(Dictionary<string, System.Text.Json.JsonElement>? properties, params string[] names)
    {
        foreach (var name in names)
            if (properties?.TryGetValue(name, out var value) == true && value.TryGetInt32(out var result))
                return result;
        return null;
    }

    private static OpenAIImagesResponse ToOpenAIImagesResponse(ImageResponse response, OpenAIImageGenerationRequest options)
        => new()
        {
            Created = new DateTimeOffset(response.Response.Timestamp).ToUnixTimeSeconds(),
            Data = response.Images?.Select(x => new OpenAIImageData { B64Json = RemoveDataUrlPrefix(x) }).ToList() ?? [],
            Background = options.Background,
            OutputFormat = options.OutputFormat,
            Quality = options.Quality,
            Size = options.Size
        };

    private static OpenAIImagesResponse ToOpenAIImagesResponse(ImageResponse response, OpenAIImageEditRequest options)
        => new()
        {
            Created = new DateTimeOffset(response.Response.Timestamp).ToUnixTimeSeconds(),
            Data = response.Images?.Select(x => new OpenAIImageData { B64Json = RemoveDataUrlPrefix(x) }).ToList() ?? [],
            Background = options.Background,
            OutputFormat = options.OutputFormat,
            Quality = options.Quality,
            Size = options.Size
        };

    private static string RemoveDataUrlPrefix(string value)
    {
        if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return value;
        var comma = value.IndexOf(',');
        return comma >= 0 ? value[(comma + 1)..] : value;
    }

}

