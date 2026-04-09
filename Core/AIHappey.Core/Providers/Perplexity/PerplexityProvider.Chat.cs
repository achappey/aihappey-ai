using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using System.Text.Json;
using System.Runtime.CompilerServices;
using AIHappey.Common.Extensions;

namespace AIHappey.Core.Providers.Perplexity;

public partial class PerplexityProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
     ChatRequest chatRequest,
     [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var unifiedRequest = chatRequest.ToUnifiedRequest(GetIdentifier());
        var emittedImageKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Dictionary<string, object?>? lastMetadata = null;

        await foreach (var part in this.StreamUnifiedAsync(
            unifiedRequest,
            cancellationToken))
        {
            // keep only the latest metadata (no duplicates)
            foreach (var imagePart in MapDownloadedImagesToFileParts(
               part.Metadata,
               GetIdentifier(),
               emittedImageKeys))
            {
                yield return imagePart;
            }

            foreach (var uiPart in part.Event.ToUIMessagePart(GetIdentifier()))
            {
                yield return uiPart;
            }
        }

        yield break;

    }

    private static IEnumerable<FileUIPart> MapDownloadedImagesToFileParts(
        Dictionary<string, object?>? metadata,
        string providerId,
        HashSet<string> emittedImageKeys)
    {
        if (!TryGetDownloadedImages(metadata, out var imagesElement))
            yield break;

        foreach (var image in imagesElement.EnumerateArray())
        {
            var dataUrl = TryGetString(image, "data_url");
            if (string.IsNullOrWhiteSpace(dataUrl))
                continue;

            var originUrl = TryGetString(image, "origin_url");
            var key = !string.IsNullOrWhiteSpace(originUrl) ? originUrl : dataUrl;
            if (!emittedImageKeys.Add(key))
                continue;

            var mediaType = TryGetString(image, "media_type") ?? "image/png";
            var filename = TryGetString(image, "filename");
            var title = TryGetString(image, "title");
            var width = TryGetInt32(image, "width");
            var height = TryGetInt32(image, "height");

            yield return new FileUIPart
            {
                MediaType = mediaType,
                Url = dataUrl.RemoveDataUrlPrefix(),
                ProviderMetadata = new Dictionary<string, Dictionary<string, object>?>
                {
                    [providerId] = new Dictionary<string, object>
                    {
                        ["origin_url"] = originUrl ?? string.Empty,
                        ["title"] = title ?? string.Empty,
                        ["width"] = width ?? 0,
                        ["height"] = height ?? 0
                    }
                }
            };
        }
    }

    private static bool TryGetDownloadedImages(
        Dictionary<string, object?>? metadata,
        out JsonElement imagesElement)
    {
        imagesElement = default;

        if (metadata is null)
            return false;

        if (!metadata.TryGetValue("chatcompletions.stream.downloaded_images", out var value) || value is null)
            return false;

        if (value is JsonElement json)
        {
            imagesElement = json;
            return imagesElement.ValueKind == JsonValueKind.Array;
        }

        try
        {
            imagesElement = JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
            return imagesElement.ValueKind == JsonValueKind.Array;
        }
        catch
        {
            imagesElement = default;
            return false;
        }
    }


}

