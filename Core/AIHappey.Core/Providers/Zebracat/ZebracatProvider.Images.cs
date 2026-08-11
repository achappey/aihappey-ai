using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Extensions;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Zebracat;

public partial class ZebracatProvider
{
    private static readonly JsonSerializerOptions ZebracatImageJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));
        if (!string.Equals(NormalizeZebracatModel(request.Model), "generate-image", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Unsupported Zebracat image model '{request.Model}'.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var payload = CopySupportedZebracatOptions(
            request.GetProviderMetadata<JsonElement>(GetIdentifier()),
            ["style_id", "for_project", "duration", "width", "height", "images"]);

        payload["prompt"] = request.Prompt;
        ApplyZebracatImageSize(payload, request.Size);

        var referenceUrls = request.Files?
            .Where(file => file is not null && Uri.TryCreate(file.Data, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            .Select(file => file.Data)
            .ToArray() ?? [];
        if (referenceUrls.Length > 0 && !payload.ContainsKey("images"))
            payload["images"] = referenceUrls;
        if (request.Files?.Count() > referenceUrls.Length)
            warnings.Add(new { type = "unsupported", feature = "inline_files", details = "Zebracat reference images must be public URLs." });
        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });
        if (request.N is > 1)
            warnings.Add(new { type = "unsupported", feature = "n", details = "Zebracat returns one primary image." });
        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });
        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspect_ratio", details = "Use size or providerOptions.zebracat width/height." });

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/generate_image")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, ZebracatImageJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        ApplyAuthHeader(httpRequest);
        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Zebracat image generation failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var source = root.TryGetProperty("src", out var srcElement) ? srcElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(source))
            throw new InvalidOperationException("Zebracat image generation returned no src.");

        using var imageResponse = await _client.GetAsync(source, cancellationToken);
        var imageBytes = await imageResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!imageResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Zebracat image download failed ({(int)imageResponse.StatusCode}).");
        var mediaType = imageResponse.Content.Headers.ContentType?.MediaType ?? GuessZebracatImageMediaType(source);

        return new ImageResponse
        {
            Images = [$"data:{mediaType};base64,{Convert.ToBase64String(imageBytes)}"],
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
        var response = await ImageRequest(options.ToImageRequest(options.Model, GetIdentifier()), cancellationToken);
        foreach (var part in response.ToOpenAIImageGenerationCompletedEvents(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return part;
        }
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    private static Dictionary<string, object?> CopySupportedZebracatOptions(JsonElement options, IEnumerable<string> supported)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (options.ValueKind != JsonValueKind.Object)
            return result;
        var names = supported.ToHashSet(StringComparer.Ordinal);
        foreach (var property in options.EnumerateObject())
            if (names.Contains(property.Name))
                result[property.Name] = property.Value.Clone();
        return result;
    }

    private static void ApplyZebracatImageSize(Dictionary<string, object?> payload, string? size)
    {
        if (string.IsNullOrWhiteSpace(size) || payload.ContainsKey("width") || payload.ContainsKey("height"))
            return;
        var parts = size.ToLowerInvariant().Split('x', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && int.TryParse(parts[0], out var width) && int.TryParse(parts[1], out var height))
        {
            payload["width"] = width;
            payload["height"] = height;
        }
    }

    private static string GuessZebracatImageMediaType(string url)
        => Path.GetExtension(Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => MediaTypeNames.Image.Jpeg,
            ".webp" => "image/webp",
            ".gif" => MediaTypeNames.Image.Gif,
            _ => MediaTypeNames.Image.Png
        };

    private static string NormalizeZebracatModel(string model)
    {
        var slash = model.IndexOf('/');
        return slash >= 0 ? model[(slash + 1)..] : model;
    }

}
