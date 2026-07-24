using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.LiteRouter;

public partial class LiteRouterProvider
{
    private const string ImageGenerationEndpoint = "https://image.literouter.com/generate";

    private static readonly JsonSerializerOptions LiteRouterImageJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var warnings = new List<object>();
        if (request.Files?.Any() == true)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "files",
                details = "LiteRouter documents text-to-image generation only. Input files were ignored."
            });
        }

        if (request.Mask is not null)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "mask",
                details = "LiteRouter documents text-to-image generation only. The mask was ignored."
            });
        }

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var (width, height) = ParseImageSize(request.Size);
        var result = await GenerateImageAsync(
            request.Prompt,
            request.Model,
            width,
            height,
            request.Seed,
            metadata,
            cancellationToken);

        return new ImageResponse
        {
            Images = [$"data:{MediaTypeNames.Image.Jpeg};base64,{Convert.ToBase64String(result.ImageBytes)}"],
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Metadata),
            Response = new()
            {
                Timestamp = result.Timestamp,
                Headers = result.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();

        var response = await ImageRequest(options.ToImageRequest(options.Model, GetIdentifier()), cancellationToken);
        return response.ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();

        // LiteRouter returns one binary JPEG response and does not expose an image streaming API.
        var response = await ImageRequest(options.ToImageRequest(options.Model, GetIdentifier()), cancellationToken);
        foreach (var streamEvent in response.ToOpenAIImageGenerationCompletedEvents(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return streamEvent;
        }
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("LiteRouter does not document image editing support.");

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("LiteRouter does not document image editing or image-edit streaming support.");

    private async Task<LiteRouterImageResult> GenerateImageAsync(
        string prompt,
        string? model,
        int? width,
        int? height,
        int? seed,
        JsonElement metadata,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        var payload = CreateImagePayload(metadata);
        payload["prompt"] = prompt;
        if (!string.IsNullOrWhiteSpace(model))
            payload["model"] = model;
        if (width.HasValue)
            payload["width"] = width.Value;
        if (height.HasValue)
            payload["height"] = height.Value;
        if (seed.HasValue)
            payload["seed"] = seed.Value;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, ImageGenerationEndpoint)
        {
            Content = new StringContent(
                payload.ToJsonString(LiteRouterImageJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var imageBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = imageBytes.Length == 0 ? null : Encoding.UTF8.GetString(imageBytes);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"LiteRouter image generation failed ({(int)response.StatusCode})."
                : $"LiteRouter image generation failed ({(int)response.StatusCode}): {error}");
        }

        if (imageBytes.Length == 0)
            throw new InvalidOperationException("LiteRouter image generation returned an empty image response.");

        return new LiteRouterImageResult(
            imageBytes,
            DateTime.UtcNow,
            response.GetHeaders(),
            JsonSerializer.SerializeToElement(response.GetHeaders()));
    }

    private static JsonObject CreateImagePayload(JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return [];

        return JsonNode.Parse(metadata.GetRawText())?.AsObject()
            ?? throw new ArgumentException("LiteRouter provider metadata must be a JSON object.", nameof(metadata));
    }

    private static (int? Width, int? Height) ParseImageSize(string? size)
    {
        if (string.IsNullOrWhiteSpace(size))
            return (null, null);

        var dimensions = size.Trim().Split('x', 'X', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (dimensions.Length != 2
            || !int.TryParse(dimensions[0], out var width)
            || !int.TryParse(dimensions[1], out var height)
            || width <= 0
            || height <= 0)
        {
            throw new ArgumentException("Size must use the '<width>x<height>' format with positive integer dimensions.", nameof(size));
        }

        return (width, height);
    }

    private sealed record LiteRouterImageResult(
        byte[] ImageBytes,
        DateTime Timestamp,
        IDictionary<string, string> Headers,
        JsonElement Metadata);
}
