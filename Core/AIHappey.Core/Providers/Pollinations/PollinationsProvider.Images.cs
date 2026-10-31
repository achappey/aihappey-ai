using System.Text.Json;
using AIHappey.Common.Extensions;
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
        var metadata = imageRequest.GetProviderMetadata<Dictionary<string, JsonElement>>(GetIdentifier());
        foreach (var (name, value) in metadata ?? [])
        {
            if (string.Equals(name, "enhance", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "private", StringComparison.OrdinalIgnoreCase))
                continue;

            query.Add($"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(GetQueryValue(value))}");
        }

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

        if (GetOptionalBoolean(metadata, "enhance") == true)
            query.Add("enhance=true");

        if (GetOptionalBoolean(metadata, "private") == true)
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

    private static bool? GetOptionalBoolean(
        Dictionary<string, JsonElement>? metadata,
        string name)
    {
        bool? result = null;
        foreach (var (key, value) in metadata ?? [])
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                result = value.Deserialize<bool?>(JsonSerializerOptions.Web);
        }

        return result;
    }

    private static string GetQueryValue(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            JsonValueKind.Object or JsonValueKind.Array => value.GetRawText(),
            _ => value.GetRawText()
        };

}
