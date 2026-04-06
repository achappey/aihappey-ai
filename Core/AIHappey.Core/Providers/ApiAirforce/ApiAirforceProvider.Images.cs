using System.Net.Mime;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.ApiAirforce;

public partial class ApiAirforceProvider
{
    private async Task<ImageResponse> ImageRequestApiAirforce(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var model = NormalizeModelId(request.Model);
        var now = DateTime.UtcNow;
        var warnings = BuildImageWarnings(request, model);
        var providerOptions = TryGetProviderOptions(request.ProviderOptions, GetIdentifier());
        var responseFormat = ResolveResponseFormat(providerOptions, "b64_json")!;

        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "model", "prompt", "n", "size", "response_format", "aspectRatio", "aspect_ratio", "image_urls", "images"
        };

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["prompt"] = request.Prompt,
            ["response_format"] = responseFormat
        };

        if (request.N is > 0)
            payload["n"] = request.N.Value;

        if (!string.IsNullOrWhiteSpace(request.Size))
            payload["size"] = request.Size;

        if (model.StartsWith("dirtberry", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(request.AspectRatio))
                payload["aspect_ratio"] = request.AspectRatio;

            if (request.Files?.Any() == true)
                payload["images"] = request.Files.Select(ToDataUrl).ToArray();
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(request.AspectRatio))
                payload["aspectRatio"] = request.AspectRatio;

            if (request.Files?.Any() == true)
                payload["image_urls"] = request.Files.Select(ToDataUrl).ToArray();
        }

        MergeRawProviderOptions(payload, request.ProviderOptions, GetIdentifier(), blocked);

        payload["model"] = model;
        payload["prompt"] = request.Prompt;
        payload["response_format"] = responseFormat;

        var root = await SendMediaGenerationAsync(payload, cancellationToken);
        var images = await ExtractImagesAsync(root, cancellationToken);

        if (images.Count == 0)
            throw new InvalidOperationException("ApiAirforce image generation returned no images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = root.Clone()
            },
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = root.Clone()
            }
        };
    }

    private static List<object> BuildImageWarnings(ImageRequest request, string model)
    {
        var warnings = new List<object>();

        if (request.Seed is not null)
            AddUnsupportedWarning(warnings, "seed", "ApiAirforce docs do not publish a generic seed parameter for these media models.");

        if (request.Mask is not null)
            AddUnsupportedWarning(warnings, "mask", "ApiAirforce media generation does not document mask uploads.");

        if (request.Files?.Any() == true)
        {
            var max = model.StartsWith("nano-banana", StringComparison.OrdinalIgnoreCase) ? 14
                : model.StartsWith("flux", StringComparison.OrdinalIgnoreCase) ? 4
                : model.StartsWith("dirtberry", StringComparison.OrdinalIgnoreCase) ? int.MaxValue
                : model.StartsWith("grok-imagine-video", StringComparison.OrdinalIgnoreCase) ? 2
                : 0;

            if (max > 0 && request.Files.Count() > max)
            {
                warnings.Add(new
                {
                    type = "unsupported",
                    feature = "files",
                    details = $"ApiAirforce model '{model}' documents at most {max} reference image(s); providerOptions may be needed to trim inputs explicitly."
                });
            }
        }

        return warnings;
    }

    private async Task<List<string>> ExtractImagesAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var images = new List<string>();

        if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array)
            return images;

        foreach (var item in dataEl.EnumerateArray())
        {
            if (item.TryGetProperty("b64_json", out var b64El) && b64El.ValueKind == JsonValueKind.String)
            {
                var b64 = b64El.GetString();
                if (!string.IsNullOrWhiteSpace(b64))
                    images.Add(b64.ToDataUrl(MediaTypeNames.Image.Png));

                continue;
            }

            var url = TryGetString(item, "url");
            if (string.IsNullOrWhiteSpace(url))
                continue;

            var downloaded = await TryFetchAsBase64Async(url, cancellationToken);
            if (downloaded is not null)
            {
                images.Add(downloaded.Value.Base64.ToDataUrl(downloaded.Value.MediaType));
                continue;
            }

            images.Add(url);
        }

        return images;
    }
}
