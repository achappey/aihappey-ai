using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;

namespace AIHappey.Core.Providers.NagaAI;

public partial class NagaAIProvider
{
    private const string NagaAIImageGenerationsEndpoint = "v1/images/generations";
    private const string NagaAIImageEditsEndpoint = "v1/images/edits";
    private static readonly HashSet<string> NagaAIImageGenerationReserved = new(
        ["model", "prompt", "quality", "size", "n", "response_format", "responseFormat"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> NagaAIImageEditReserved = new(
        ["model", "prompt", "background", "mask", "n", "quality", "response_format", "responseFormat", "size", "image", "image[]"],
        StringComparer.OrdinalIgnoreCase);

    public async Task<ImageResponse> ImageRequest(
        ImageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var files = request.Files?.Where(file => file is not null).ToList() ?? [];
        var isEdit = files.Count > 0 || request.Mask is not null;
        OpenAIImagesResponse response;
        if (isEdit)
        {
            if (files.Count == 0)
                throw new ArgumentException("NagaAI image edits require at least one input image.", nameof(request));
            var streams = new List<MemoryStream>();
            try
            {
                response = await OpenAIImageEditRequestAsync(
                    CreateNagaAIImageEditRequest(request, files, metadata, streams),
                    cancellationToken);
            }
            finally
            {
                foreach (var stream in streams)
                    await stream.DisposeAsync();
            }
        }
        else
        {
            response = await OpenAIImageGenerationRequestAsync(
                new OpenAIImageGenerationRequest
                {
                    Model = request.Model,
                    Prompt = request.Prompt,
                    N = request.N,
                    Size = request.Size,
                    Quality = ReadNagaAIString(metadata, "quality"),
                    ResponseFormat = ReadNagaAIString(metadata, "response_format", "responseFormat") ?? "b64_json",
                    AdditionalProperties = CopyNagaAIProperties(metadata, NagaAIImageGenerationReserved)
                },
                cancellationToken);
        }

        var outputFormat = response.OutputFormat ?? ReadNagaAIString(metadata, "output_format", "outputFormat");
        var mediaType = outputFormat?.ToLowerInvariant() switch
        {
            "jpeg" or "jpg" => MediaTypeNames.Image.Jpeg,
            "webp" => "image/webp",
            _ => MediaTypeNames.Image.Png
        };
        var images = response.Data?.Select(image => !string.IsNullOrWhiteSpace(image.B64Json)
                ? image.B64Json.ToDataUrl(mediaType)
                : image.Url)
            .Where(image => !string.IsNullOrWhiteSpace(image))
            .Cast<string>()
            .ToArray() ?? [];
        if (images.Length == 0)
            throw new InvalidOperationException("NagaAI image response did not contain any images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = BuildNagaAIImageWarnings(request),
            Usage = response.Usage is null ? null : new ImageUsageData
            {
                InputTokens = response.Usage.InputTokens,
                OutputTokens = response.Usage.OutputTokens,
                TotalTokens = response.Usage.TotalTokens
            },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(response),
            Response = new HeaderResponseData
            {
                Timestamp = response.Created > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(response.Created).UtcDateTime
                    : DateTime.UtcNow,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
        OpenAIImageGenerationRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        ApplyAuthHeader();
        return _client.OpenAICompatibleImageGenerationRequestAsync(
            options,
            NagaAIImageGenerationsEndpoint,
            cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        await foreach (var streamEvent in _client.OpenAICompatibleImageGenerationNonStreamingAsStreamAsync(
            options,
            NagaAIImageGenerationsEndpoint,
            cancellationToken))
            yield return streamEvent;
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        ApplyAuthHeader();
        return _client.OpenAICompatibleImageEditRequestAsync(
            options,
            NagaAIImageEditsEndpoint,
            cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        await foreach (var streamEvent in _client.OpenAICompatibleImageEditNonStreamingAsStreamAsync(
            options,
            NagaAIImageEditsEndpoint,
            cancellationToken))
            yield return streamEvent;
    }

    private static OpenAIImageEditRequest CreateNagaAIImageEditRequest(
        ImageRequest request,
        IReadOnlyList<ImageFile> files,
        JsonElement metadata,
        List<MemoryStream> streams)
        => new()
        {
            Model = request.Model,
            Prompt = request.Prompt,
            N = request.N,
            Size = request.Size,
            Background = ReadNagaAIString(metadata, "background"),
            Quality = ReadNagaAIString(metadata, "quality"),
            ImageFiles = files.Select((file, index) => CreateNagaAIFormFile(file, "image", $"image-{index + 1}", streams)).ToArray(),
            MaskFile = request.Mask is null ? null : CreateNagaAIFormFile(request.Mask, "mask", "mask", streams),
            AdditionalProperties = CopyNagaAIProperties(metadata, NagaAIImageEditReserved)
        };

    private static IFormFile CreateNagaAIFormFile(
        ImageFile file,
        string fieldName,
        string fallbackName,
        List<MemoryStream> streams)
    {
        if (string.IsNullOrWhiteSpace(file.Data))
            throw new ArgumentException("Image data is required.", nameof(file));
        if (file.Data.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || file.Data.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("NagaAI multipart image edits require base64 image data.");

        byte[] bytes;
        try { bytes = Convert.FromBase64String(file.Data.RemoveDataUrlPrefix()); }
        catch (FormatException exception)
        {
            throw new ArgumentException("Image data must be base64 encoded.", nameof(file), exception);
        }
        var stream = new MemoryStream(bytes, writable: false);
        streams.Add(stream);
        var mediaType = string.IsNullOrWhiteSpace(file.MediaType) ? MediaTypeNames.Image.Png : file.MediaType;
        var extension = mediaType.ToLowerInvariant() switch
        {
            MediaTypeNames.Image.Jpeg or "image/jpg" => ".jpg",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            _ => ".png"
        };
        return new FormFile(stream, 0, stream.Length, fieldName, fallbackName + extension)
        {
            Headers = new HeaderDictionary(),
            ContentType = mediaType
        };
    }

    private static IEnumerable<object> BuildNagaAIImageWarnings(ImageRequest request)
    {
        var warnings = new List<object>();
        if (request.Seed.HasValue)
            warnings.Add(new { type = "unsupported", feature = "seed" });
        if (!string.IsNullOrWhiteSpace(request.AspectRatio) && string.IsNullOrWhiteSpace(request.Size))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });
        return warnings;
    }

    private static Dictionary<string, JsonElement>? CopyNagaAIProperties(
        JsonElement metadata,
        HashSet<string> reserved)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return null;
        Dictionary<string, JsonElement> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (var property in metadata.EnumerateObject())
            if (!reserved.Contains(property.Name))
                result[property.Name] = property.Value.Clone();
        return result.Count == 0 ? null : result;
    }

    private static string? ReadNagaAIString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var name in names)
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        return null;
    }

    private static double? ReadNagaAIDouble(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var name in names)
            if (element.TryGetProperty(name, out var value) && value.TryGetDouble(out var number))
                return number;
        return null;
    }
}
