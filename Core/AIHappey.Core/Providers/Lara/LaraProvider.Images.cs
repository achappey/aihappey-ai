using System.Runtime.CompilerServices;
using AIHappey.Common.Model.Providers.Lara;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using Lara.Sdk;

namespace AIHappey.Core.Providers.Lara;

public partial class LaraProvider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var targetLanguage = GetTargetLanguage(request.Model);
        var metadata = request.GetProviderMetadata<LaraProviderMetadata>(GetIdentifier());
        var sourceLanguage = metadata?.Source;
        if (string.IsNullOrWhiteSpace(sourceLanguage))
            throw new ArgumentException("Lara image translation requires providerMetadata.lara.source.", nameof(request));

        var files = request.Files?.ToList() ?? [];
        if (files.Count != 1)
            throw new ArgumentException("Lara image translation requires exactly one input image.", nameof(request));

        if (request.Mask is not null)
            throw new NotSupportedException("Lara image translation does not support an edit mask.");

        var inputPath = Path.Combine(Path.GetTempPath(), $"aihappey-lara-{Guid.NewGuid():n}{GetImageExtension(files[0].MediaType)}");
        try
        {
            await File.WriteAllBytesAsync(inputPath, GetImageBytes(files[0]), cancellationToken);
            await using var translated = await CreateTranslator().Images.Translate(
                inputPath,
                sourceLanguage,
                targetLanguage,
                new ImageTranslateOptions
                {
                    AdaptTo = metadata?.AdaptTo,
                    Glossaries = metadata?.Glossaries,
                    NoTrace = metadata?.NoTrace ?? false,
                    Style = ParseStyle(metadata?.Style),
                    Model = ParseImageModel(metadata?.ImageModel)
                });

            await using var buffer = new MemoryStream();
            await translated.CopyToAsync(buffer, cancellationToken);
            var mimeType = GetImageMimeType(files[0].MediaType);
            var image = $"data:{mimeType};base64,{Convert.ToBase64String(buffer.ToArray())}";

            return new ImageResponse
            {
                Images = [image],
                Warnings = [],
                ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
                Response = new HeaderResponseData
                {
                    Timestamp = DateTime.UtcNow,
                    ModelId = request.Model.ToModelId(GetIdentifier())
                }
            };
        }
        finally
        {
            try { File.Delete(inputPath); } catch { /* best-effort temporary-file cleanup */ }
        }
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        var request = await options.ToImageRequest(options.Model, GetIdentifier(), cancellationToken);
        var result = await ImageRequest(request, cancellationToken);
        return result.ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        var request = await options.ToImageRequest(options.Model, GetIdentifier(), cancellationToken);
        var result = await ImageRequest(request, cancellationToken);
        foreach (var streamEvent in result.ToOpenAIImageEditCompletedEvents(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return streamEvent;
        }
    }

    private static ImageTranslationModel? ParseImageModel(string? model)
        => Enum.TryParse<ImageTranslationModel>(model, ignoreCase: true, out var parsed) ? parsed : null;

    private static byte[] GetImageBytes(ImageFile file)
    {
        if (string.IsNullOrWhiteSpace(file.Data))
            throw new ArgumentException("Input image data cannot be empty.", nameof(file));

        var data = file.Data;
        var marker = data.IndexOf(",", StringComparison.Ordinal);
        if (data.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && marker >= 0)
            data = data[(marker + 1)..];

        return Convert.FromBase64String(data);
    }

    private static string GetImageExtension(string? mediaType)
        => GetImageMimeType(mediaType) switch
        {
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            _ => ".png"
        };

    private static string GetImageMimeType(string? mediaType)
        => mediaType?.Trim().ToLowerInvariant() switch
        {
            "image/jpg" or "image/jpeg" => "image/jpeg",
            "image/webp" => "image/webp",
            "image/gif" => "image/gif",
            _ => "image/png"
        };
}
