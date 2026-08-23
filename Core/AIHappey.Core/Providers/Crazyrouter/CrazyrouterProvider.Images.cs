using System.Net.Mime;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Crazyrouter;

public partial class CrazyrouterProvider
{
    private async Task<ImageResponse> CrazyrouterImageRequestAsync(
        ImageRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var warnings = new List<object>();
        if (request.Files?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "files", details = "Use Crazyrouter's documented native Gemini route for reference-image workflows." });
        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        var additional = CrazyrouterProviderOptions(request.ProviderOptions);
        CrazyrouterSet(additional, "aspect_ratio", request.AspectRatio);
        CrazyrouterSet(additional, "seed", request.Seed);

        var openAIRequest = new OpenAIImageGenerationRequest
        {
            Model = request.Model,
            Prompt = request.Prompt,
            N = request.N,
            Size = request.Size,
            ResponseFormat = "b64_json",
            AdditionalProperties = additional.Count == 0 ? null : additional
        };

        var response = await OpenAIImageGenerationRequestAsync(openAIRequest, cancellationToken);
        var images = await CrazyrouterNormalizeImagesAsync(response.Data ?? [], cancellationToken);
        if (images.Count == 0)
            throw new InvalidOperationException("Crazyrouter image response contained no images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(response),
            Usage = response.Usage is null ? null : new ImageUsageData
            {
                InputTokens = response.Usage.InputTokens,
                OutputTokens = response.Usage.OutputTokens,
                TotalTokens = response.Usage.TotalTokens
            },
            Response = new HeaderResponseData
            {
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Timestamp = response.Created > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(response.Created).UtcDateTime
                    : DateTime.UtcNow
            }
        };
    }

    private static Dictionary<string, JsonElement> CrazyrouterProviderOptions(
        Dictionary<string, JsonElement>? providerOptions)
    {
        if (providerOptions?.TryGetValue("crazyrouter", out var value) != true
            || value.ValueKind != JsonValueKind.Object)
            return [];
        return value.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone(), StringComparer.OrdinalIgnoreCase);
    }

    private static void CrazyrouterSet(Dictionary<string, JsonElement> target, string name, object? value)
    {
        if (value is not null && (value is not string text || !string.IsNullOrWhiteSpace(text)))
            target[name] = JsonSerializer.SerializeToElement(value);
    }

    private async Task<List<string>> CrazyrouterNormalizeImagesAsync(
        IEnumerable<OpenAIImageData> data,
        CancellationToken cancellationToken)
    {
        List<string> images = [];
        foreach (var item in data)
        {
            if (!string.IsNullOrWhiteSpace(item.B64Json))
            {
                images.Add(item.B64Json.ToDataUrl(MediaTypeNames.Image.Png));
                continue;
            }
#pragma warning disable CS0618
            if (!string.IsNullOrWhiteSpace(item.Url))
            {
                using var response = await _client.GetAsync(item.Url, cancellationToken);
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Crazyrouter image download failed ({(int)response.StatusCode}).");
                images.Add(Convert.ToBase64String(bytes).ToDataUrl(
                    response.Content.Headers.ContentType?.MediaType ?? MediaTypeNames.Image.Png));
            }
#pragma warning restore CS0618
        }
        return images;
    }
}
