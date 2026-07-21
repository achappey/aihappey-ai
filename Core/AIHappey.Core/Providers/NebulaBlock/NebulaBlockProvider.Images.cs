using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.NebulaBlock;

public partial class NebulaBlockProvider
{
    private static readonly JsonSerializerOptions NebulaBlockImageJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt
        };

        AddImageSize(payload, request.Size, warnings);
        AddImageInputs(payload, request, warnings);
        MergeProviderOptions(payload, request.GetProviderMetadata<JsonElement>(GetIdentifier()));

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/images/generation")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, NebulaBlockImageJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"NebulaBlock image generation failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var images = ExtractImages(root);

        if (images.Count == 0)
            throw new InvalidOperationException("NebulaBlock image generation response did not contain usable images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new()
            {
                Timestamp = now,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private static void AddImageSize(Dictionary<string, object?> payload, string? size, List<object> warnings)
    {
        if (string.IsNullOrWhiteSpace(size))
            return;

        var dimensions = size
            .Replace(':', 'x')
            .Split('x', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (dimensions.Length == 2
            && int.TryParse(dimensions[0], out var width)
            && int.TryParse(dimensions[1], out var height)
            && width > 0
            && height > 0)
        {
            payload["width"] = width;
            payload["height"] = height;
            return;
        }

        warnings.Add(new { type = "unsupported", feature = "size", value = size });
    }

    private static void AddImageInputs(Dictionary<string, object?> payload, ImageRequest request, List<object> warnings)
    {
        var files = request.Files?.Where(file => file is not null).ToList() ?? [];
        if (files.Count > 0)
            payload["image"] = files[0].Data.RemoveDataUrlPrefix();
        if (files.Count > 1)
            warnings.Add(new { type = "unsupported", feature = "files", details = "NebulaBlock supports one image input; only the first file was sent." });

        if (request.Mask is not null)
            payload["mask"] = request.Mask.Data.RemoveDataUrlPrefix();
    }

    private static void MergeProviderOptions(Dictionary<string, object?> payload, JsonElement providerOptions)
    {
        if (providerOptions.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in providerOptions.EnumerateObject())
            payload[property.Name] = property.Value.Clone();
    }

    private static List<string> ExtractImages(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("NebulaBlock image generation response is missing the 'data' array.");

        List<string> images = [];
        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("b64_json", out var image)
                || image.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(image.GetString()))
            {
                continue;
            }

            images.Add(image.GetString()!.ToDataUrl(MediaTypeNames.Image.Png));
        }

        return images;
    }

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();

        var result = await ImageRequest(options.ToImageRequest(
            options.ResolveOpenAIImageGenerationModel(),
            GetIdentifier()), cancellationToken);

        return result.ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();

        var result = await ImageRequest(options.ToImageRequest(
            options.ResolveOpenAIImageGenerationModel(),
            GetIdentifier()), cancellationToken);

        foreach (var streamEvent in result.ToOpenAIImageGenerationCompletedEvents(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return streamEvent;
        }
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();

        var request = await options.ToImageRequest(
            options.ResolveOpenAIImageEditModel(),
            GetIdentifier(),
            cancellationToken);
        var result = await ImageRequest(request, cancellationToken);

        return result.ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();

        var request = await options.ToImageRequest(
            options.ResolveOpenAIImageEditModel(),
            GetIdentifier(),
            cancellationToken);
        var result = await ImageRequest(request, cancellationToken);

        foreach (var streamEvent in result.ToOpenAIImageEditCompletedEvents(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return streamEvent;
        }
    }

    public async Task<OpenAIImagesResponse> OpenAIImageVariationRequestAsync(OpenAIImageVariationRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageVariationRequest();

        var request = await options.ToImageRequest(
            options.ResolveOpenAIImageVariationModel(),
            GetIdentifier(),
            cancellationToken);
        var result = await ImageRequest(request, cancellationToken);

        return result.ToOpenAIImagesResponse(options);
    }

}
