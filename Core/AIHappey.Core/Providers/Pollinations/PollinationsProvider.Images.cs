using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Pollinations;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.Pollinations;

public partial class PollinationsProvider 
{
    public async Task<ImageResponse> ImageRequest(
       ImageRequest imageRequest,
       CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        if (string.IsNullOrWhiteSpace(imageRequest.Prompt))
            throw new ArgumentException("Prompt is required", nameof(imageRequest));

        // Build URL
        var prompt = Uri.EscapeDataString(imageRequest.Prompt);
        var start = DateTime.UtcNow;

        var query = new List<string>();
        var metadata = imageRequest.GetProviderMetadata<PollinationsImageProviderMetadata>(GetIdentifier());
        if (!string.IsNullOrWhiteSpace(imageRequest.Model))
            query.Add($"model={Uri.EscapeDataString(imageRequest.Model)}");

        List<object> warnings = [];

        var imageWidth = imageRequest.GetImageWidth();
        var imageHeight = imageRequest.GetImageHeight();

        if (imageWidth is not null && imageHeight is not null)
        {
            query.Add($"width={imageWidth}");
            query.Add($"height={imageHeight}");
        }
        else if (!string.IsNullOrWhiteSpace(imageRequest.AspectRatio))
        {
            var inferred = imageRequest.AspectRatio.InferSizeFromAspectRatio();

            if (inferred is not null)
            {
                query.Add($"width={inferred.Value.width}");
                query.Add($"height={inferred.Value.height}");

                warnings.Add(new
                {
                    type = "compatibility",
                    feature = "aspectRatio",
                    fetails = $"No size provided. Inferred {inferred.Value.width}x{inferred.Value.height} from aspect ratio {imageRequest.AspectRatio}."
                });
            }
        }

        if (imageRequest.Seed.HasValue)
            query.Add($"seed={imageRequest.Seed.Value}");

        if (metadata?.Enhance == true)
            query.Add("enhance=true");

        if (metadata?.Private == true)
            query.Add("private=true");

        var url = $"https://image.pollinations.ai/prompt/{prompt}";
        if (query.Count > 0)
            url += "?" + string.Join("&", query);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await _client.SendAsync(
            req,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Pollinations image error: {err}");
        }

        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);
        var mime = resp.Content.Headers.ContentType?.MediaType ?? "image/jpeg";

        if (imageRequest.Mask is not null)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "mask"
            });
        }

        if (imageRequest.Files?.Any() == true)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "files"
            });
        }

        return new ImageResponse
        {
            Images = [$"data:{mime};base64,{Convert.ToBase64String(bytes)}"],
            Warnings = warnings,
            Response = new ()
            {
                Timestamp = start,
                ModelId = imageRequest.Model
            }
        };
    }

}
