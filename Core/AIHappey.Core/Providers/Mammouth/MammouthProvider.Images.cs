using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Mammouth;

public sealed partial class MammouthProvider
{
    private static readonly JsonSerializerOptions ImageJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
        OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        if (string.IsNullOrWhiteSpace(options.Model))
            throw new ArgumentException("Model is required.", nameof(options));
        ApplyAuthHeader();
        var payload = JsonSerializer.SerializeToElement(options, ImageJson).EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone());
        payload["model"] = JsonSerializer.SerializeToElement(NativeModel(options.Model));
        if (payload.TryGetValue("stream", out var stream) && stream.ValueKind == JsonValueKind.True)
            throw new NotSupportedException("Mammouth does not document streaming image generation.");
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
        {
            Content = JsonContent.Create(payload)
        };
        using var response = await _client.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<OpenAIImagesResponse>(ImageJson, cancellationToken)
            ?? throw new JsonException("Mammouth returned an empty image response.");
    }

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Mammouth does not document streaming image generation.");

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Files?.Any() == true || request.Mask is not null || request.AspectRatio is not null)
            throw new NotSupportedException("Use native image options for Mammouth; image editing and aspect-ratio translation are not supported.");
        var native = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var options) == true)
        {
            if (options.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Mammouth provider options must be an object.", nameof(request));
            foreach (var property in options.EnumerateObject())
                native[property.Name] = property.Value.Clone();
        }
        if (request.Seed is not null)
            native["seed"] = JsonSerializer.SerializeToElement(request.Seed);
        native.Remove("model");
        native.Remove("prompt");
        native.Remove("response_format");
        if (request.N is not null) native.Remove("n");
        if (request.Size is not null) native.Remove("size");
        var timestamp = DateTime.UtcNow;
        var result = await OpenAIImageGenerationRequestAsync(new()
        {
            Model = request.Model, Prompt = request.Prompt, N = request.N, Size = request.Size,
            ResponseFormat = "b64_json", AdditionalProperties = native
        }, cancellationToken);
        var format = result.OutputFormat ??
            (native.TryGetValue("output_format", out var outputFormat) && outputFormat.ValueKind == JsonValueKind.String
                ? outputFormat.GetString() : null) ?? "png";
        var mimeType = format is "jpg" or "jpeg" ? "image/jpeg" : $"image/{format}";
        var images = (result.Data ?? []).Select(image => image.B64Json)
            .Where(image => !string.IsNullOrWhiteSpace(image))
            .Select(image => $"data:{mimeType};base64,{image}").ToArray();
        if (images.Length == 0)
            throw new JsonException("Mammouth returned no base64 images.");
        return new ImageResponse
        {
            Images = images,
            Response = new() { Timestamp = timestamp, ModelId = $"{GetIdentifier()}/{NativeModel(request.Model)}" },
            Usage = result.Usage is null ? null : new()
            {
                InputTokens = result.Usage.InputTokens, OutputTokens = result.Usage.OutputTokens,
                TotalTokens = result.Usage.TotalTokens
            },
            ProviderMetadata = new() { [GetIdentifier()] = JsonSerializer.SerializeToElement(result, ImageJson) }
        };
    }
}
