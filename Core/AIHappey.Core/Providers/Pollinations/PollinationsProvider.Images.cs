using AIHappey.Core.AI;
using AIHappey.Common.Model;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Pollinations;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.Pollinations;

public partial class PollinationsProvider : IModelProvider
{
    private async IAsyncEnumerable<UIMessagePart> StreamImageAsync(
        ChatRequest chatRequest,
        string model,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // 1) Extract prompt from last user message text parts
        var prompt = string.Join("\n",
            chatRequest.Messages?
                .LastOrDefault(m => m.Role == Common.Model.Role.user)
                ?.Parts?
                .OfType<TextUIPart>()
                .Select(p => p.Text) ?? Array.Empty<string>());

        if (string.IsNullOrWhiteSpace(prompt))
        {
            yield return "No prompt provided.".ToErrorUIPart();
            yield break;
        }

        // 2) Build ImageRequest (pass-through what you have)
        var imageRequest = new ImageRequest
        {
            Prompt = prompt,
            Model = model,
            // If your ChatRequest carries these elsewhere in your app, map them here:
            // Size = chatRequest.Size,
            // AspectRatio = chatRequest.AspectRatio,
            // Seed = chatRequest.Seed,
            // Files = ...
            // Mask = ...
        };

        ImageResponse? result = null;
        string? exceptionMessage = null;

        try
        {
            result = await ImageRequest(imageRequest, cancellationToken);
        }
        catch (Exception ex)
        {
            exceptionMessage = ex.Message;
        }

        if (!string.IsNullOrEmpty(exceptionMessage))
        {
            yield return exceptionMessage.ToErrorUIPart();
            yield break;
        }

        // 3) Emit FileUIPart with:
        //    - MediaType = extracted from data URL
        //    - Url       = BASE64 ONLY (no prefix)
        foreach (var image in result?.Images ?? Array.Empty<string>())
        {
            // image = "data:image/png;base64,AAAA..."
            var commaIndex = image.IndexOf(',');
            if (commaIndex <= 0) continue;

            var header = image[..commaIndex];     // data:image/png;base64
            var data = image[(commaIndex + 1)..]; // base64 payload ONLY

            var mediaType = header
                .Replace("data:", "", StringComparison.OrdinalIgnoreCase)
                .Replace(";base64", "", StringComparison.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(mediaType)) continue;
            if (string.IsNullOrWhiteSpace(data)) continue;

            yield return new FileUIPart
            {
                MediaType = mediaType,
                Url = data
            };
        }

        yield return "stop".ToFinishUIPart(model, 0, 0, 0, null);
    }

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
        var metadata = imageRequest.GetImageProviderMetadata<PollinationsImageProviderMetadata>(GetIdentifier());
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
