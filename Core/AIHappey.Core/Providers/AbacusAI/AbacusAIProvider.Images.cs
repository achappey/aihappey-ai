using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Common.Extensions;
using AIHappey.Core.MCP.Media;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.AbacusAI;

public sealed partial class AbacusAIProvider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);
        var content = new List<object> { new { type = "text", text = request.Prompt } };
        foreach (var file in request.Files ?? [])
            content.Add(new { type = "image_url", image_url = new { url = ImageInput(file) } });
        var payload = MediaPayload(request.Model, new[] { new { role = "user", content } });
        var model = payload["model"]!.GetValue<string>();
        var config = new JsonObject { ["prompt"] = request.Prompt };
        var warnings = new List<object>();
        var singleImage = model is "flux_kontext_edit" or "gpt_image15_edit" or "imagine_art" or "magnific" or "midjourney";
        if (request.N is { } n)
        {
            if (n is < 1 or > 4)
                throw new ArgumentOutOfRangeException(nameof(request.N), "AbacusAI supports 1-4 images.");
            if (!singleImage)
                config["num_images"] = n;
            else if (n != 1)
                warnings.Add(new { type = "unsupported", feature = "n", message = $"{model} does not support num_images." });
        }
        if (request.Size is not null)
            config["size"] = request.Size;
        if (request.AspectRatio is not null)
            config["aspect_ratio"] = request.AspectRatio;
        if (request.Seed is not null)
            config["seed"] = request.Seed;
        payload["image_config"] = config;
        MergeMediaOptions(payload, MediaOptions(request.ProviderOptions));
        payload["modalities"] = new JsonArray("image");
        // There is no documented common mask parameter. Model-specific masks can be supplied verbatim.
        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask", message = "Supply a documented model-specific mask via providerOptions.abacusai.image_config." });
        var (root, headers) = await SendMediaAsync(payload, cancellationToken);
        var message = MediaMessage(root);
        if (!message.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array || images.GetArrayLength() == 0)
            throw new InvalidOperationException("AbacusAI image response did not contain images.");
        var output = new List<string>();
        foreach (var image in images.EnumerateArray())
        {
            if (ReadString(image, "type") != "image_url"
                || !image.TryGetProperty("image_url", out var reference) || ReadString(reference, "url") is not { Length: > 0 } url)
                throw new InvalidOperationException("AbacusAI returned an invalid generated image reference.");
            if (MediaContentHelpers.TryParseDataUrl(url, out var mime, out var data))
            {
                if (!mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("AbacusAI returned non-image media.");
                output.Add(Convert.ToBase64String(Convert.FromBase64String(data)).ToDataUrl(mime));
            }
            else
            {
                var downloaded = await MediaContentHelpers.FetchProviderImageOutputAsBase64Async(new Uri(url), httpClientFactory, cancellationToken);
                output.Add(downloaded.Base64.ToDataUrl(downloaded.MimeType));
            }
        }
        var response = MediaResponse(root, request.Model, headers);
        var usage = root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object ? u : (JsonElement?)null;
        return new ImageResponse
        {
            Images = output, Warnings = warnings, ProviderMetadata = MediaMetadata(root),
            Usage = usage is { } tokens ? new()
            {
                InputTokens = (int?)ReadInt64(tokens, "prompt_tokens"),
                OutputTokens = (int?)ReadInt64(tokens, "completion_tokens"),
                TotalTokens = (int?)ReadInt64(tokens, "total_tokens")
            } : null,
            Response = new() { ModelId = response.ModelId, Timestamp = response.Timestamp, Headers = headers }
        };
    }

    private static string ImageInput(ImageFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file is ImageFileUrl reference)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(reference.Url);
            return reference.Url;
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(file.Data);
        if (MediaContentHelpers.TryParseDataUrl(file.Data, out _, out var data))
        {
            _ = Convert.FromBase64String(data);
            return file.Data;
        }
        return Convert.ToBase64String(Convert.FromBase64String(file.Data)).ToDataUrl(file.MediaType ?? "image/png");
    }
}
