using AIHappey.Core.AI;
using AIHappey.Common.Extensions;
using AIHappey.Core.Extensions;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.AIHorde;

public partial class AIHordeProvider
{
    private const string ImageApiBaseUrl = "https://aihorde.net/api/v2/";
    private static readonly TimeSpan ImagePollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ImageRequestLifetime = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions ImageJsonOptions = new(JsonSerializerDefaults.Web)
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

        var inputImages = request.Files?.Where(file => file is not null).ToList() ?? [];
        var warnings = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });

        var payload = BuildImagePayload(request, inputImages);
        var submission = await SubmitImageRequestAsync(payload, cancellationToken);
        var completed = await WaitForImageCompletionAsync(submission.Id, cancellationToken);
        var images = await DownloadGeneratedImagesAsync(completed, cancellationToken);

        if (images.Count == 0)
            throw new InvalidOperationException($"AI Horde image generation completed but returned no usable images (id={submission.Id}).");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(completed.Clone()),
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }


    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var result = await ImageRequest(options.ToImageRequest(options.Model, GetIdentifier()), cancellationToken);
        return result.ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var result = await ImageRequest(options.ToImageRequest(options.Model, GetIdentifier()), cancellationToken);
        foreach (var streamEvent in result.ToOpenAIImageGenerationCompletedEvents(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return streamEvent;
        }
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        var request = await options.ToImageRequest(options.Model, GetIdentifier(), cancellationToken);
        var result = await ImageRequest(request, cancellationToken);
        return result.ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        var request = await options.ToImageRequest(options.Model, GetIdentifier(), cancellationToken);
        var result = await ImageRequest(request, cancellationToken);
        foreach (var streamEvent in result.ToOpenAIImageEditCompletedEvents(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return streamEvent;
        }
    }

    private Dictionary<string, object?> BuildImagePayload(ImageRequest request, IReadOnlyList<ImageFile> inputImages)
    {
        var payload = CopyProviderOptions(GetProviderOptions(request));
        payload["prompt"] = request.Prompt;

        var parameters = GetPayloadObject(payload, "params");
        if (request.N is not null)
            parameters["n"] = request.N;
        if (request.Seed is not null)
            parameters["seed"] = request.Seed.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var (width, height) = ParseSize(request.Size);
        if (width is not null)
            parameters["width"] = width;
        if (height is not null)
            parameters["height"] = height;
        if (parameters.Count > 0)
            payload["params"] = parameters;

        if (inputImages.Count > 0)
        {
            payload["source_image"] = GetNakedBase64(inputImages[0], "image");
            payload["source_processing"] = request.Mask is null ? "img2img" : "inpainting";

            if (inputImages.Count > 1)
            {
                payload["extra_source_images"] = inputImages.Skip(1)
                    .Select(image => new Dictionary<string, object?>
                    {
                        ["image"] = GetNakedBase64(image, "image"),
                        ["strength"] = 1
                    })
                    .ToArray();
            }
        }

        if (request.Mask is not null)
        {
            if (inputImages.Count == 0)
                throw new ArgumentException("AI Horde inpainting requires an input image together with the mask.", nameof(request));
            payload["source_mask"] = GetNakedBase64(request.Mask, "mask");
        }

        // `aihorde-image` is a local catalog trigger, not an AI Horde upstream model name.
        // Deliberately do not derive or add a `models` member from request.Model. A caller may
        // still explicitly supply `providerOptions.aihorde.models` to select an upstream model.
        return payload;
    }

    private async Task<(string Id, JsonElement Submission)> SubmitImageRequestAsync(
        Dictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        using var request = CreateImageRequest(HttpMethod.Post, "generate/async");
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload, ImageJsonOptions),
            Encoding.UTF8,
            MediaTypeNames.Application.Json);

        using var response = await _client.SendAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"AI Horde image submission failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var id = GetString(root, "id")
            ?? throw new InvalidOperationException("AI Horde image submission response did not contain an id.");
        return (id, root);
    }

    private async Task<JsonElement> WaitForImageCompletionAsync(string id, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow - startedAt >= ImageRequestLifetime)
                throw new TimeoutException($"AI Horde image generation did not complete before its 10 minute request lifetime expired (id={id}).");

            var check = await GetImageApiJsonAsync($"generate/check/{Uri.EscapeDataString(id)}", cancellationToken);
            if (GetBoolean(check, "done"))
            {
                var status = await GetImageApiJsonAsync($"generate/status/{Uri.EscapeDataString(id)}", cancellationToken);
                if (GetBoolean(status, "faulted"))
                    throw new InvalidOperationException($"AI Horde image generation faulted (id={id}).");
                return status;
            }

            await Task.Delay(ImagePollInterval, cancellationToken);
        }
    }

    private async Task<JsonElement> GetImageApiJsonAsync(string relativePath, CancellationToken cancellationToken)
    {
        using var request = CreateImageRequest(HttpMethod.Get, relativePath);
        using var response = await _client.SendAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"AI Horde image status request failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private async Task<List<string>> DownloadGeneratedImagesAsync(JsonElement status, CancellationToken cancellationToken)
    {
        if (status.ValueKind != JsonValueKind.Object
            || !status.TryGetProperty("generations", out var generations)
            || generations.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("AI Horde image status response did not contain generations.");
        }

        List<string> images = [];
        foreach (var generation in generations.EnumerateArray())
        {
            if (string.Equals(GetString(generation, "state"), "faulted", StringComparison.OrdinalIgnoreCase)
                || string.Equals(GetString(generation, "state"), "failed", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var url = GetString(generation, "img");
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var imageUri))
                continue;

            using var response = await _client.GetAsync(imageUri, cancellationToken);
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"AI Horde generated image download failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

            var mediaType = response.Content.Headers.ContentType?.MediaType
                ?? GuessImageMediaType(imageUri.AbsolutePath)
                ?? MediaTypeNames.Image.Png;
            images.Add(Convert.ToBase64String(bytes).ToDataUrl(mediaType));
        }

        return images;
    }

    private HttpRequestMessage CreateImageRequest(HttpMethod method, string relativePath)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(AIHorde)} API key.");

        var request = new HttpRequestMessage(method, new Uri(new Uri(ImageApiBaseUrl), relativePath));
        request.Headers.Remove("apikey");
        request.Headers.TryAddWithoutValidation("apikey", key);
        request.Headers.TryAddWithoutValidation("Client-Agent", "AIHappey:1:AIHappey");
        return request;
    }

    private static Dictionary<string, object?> CopyProviderOptions(JsonElement options)
    {
        if (options.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, object?>(StringComparer.Ordinal);

        return options.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.Clone() as object,
            StringComparer.Ordinal);
    }

    private JsonElement GetProviderOptions(ImageRequest request)
        => request.ProviderOptions is not null
            && request.ProviderOptions.TryGetValue(GetIdentifier(), out var options)
            ? options
            : default;

    private static Dictionary<string, object?> GetPayloadObject(Dictionary<string, object?> payload, string name)
    {
        if (!payload.TryGetValue(name, out var value))
            return new Dictionary<string, object?>(StringComparer.Ordinal);

        if (value is JsonElement { ValueKind: JsonValueKind.Object } element)
        {
            return element.EnumerateObject().ToDictionary(
                property => property.Name,
                property => property.Value.Clone() as object,
                StringComparer.Ordinal);
        }

        return value as Dictionary<string, object?> ?? new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    private static string GetNakedBase64(ImageFile image, string field)
    {
        if (string.IsNullOrWhiteSpace(image.Data)
            || image.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || image.Data.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || image.Data.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"AI Horde {field} inputs must contain naked base64 data; data URLs and remote URLs are not supported.");
        }

        var base64 = image.Data.Trim();
        try
        {
            _ = Convert.FromBase64String(base64);
            return base64;
        }
        catch (FormatException exception)
        {
            throw new ArgumentException($"AI Horde {field} input data must be base64 encoded.", nameof(image), exception);
        }
    }

    private static (int? Width, int? Height) ParseSize(string? size)
    {
        if (string.IsNullOrWhiteSpace(size))
            return (null, null);

        var pieces = size.Trim().ToLowerInvariant().Split('x', StringSplitOptions.TrimEntries);
        if (pieces.Length != 2
            || !int.TryParse(pieces[0], out var width)
            || !int.TryParse(pieces[1], out var height)
            || width <= 0
            || height <= 0)
        {
            throw new ArgumentException("Image size must be specified as WIDTHxHEIGHT.", nameof(size));
        }

        return (width, height);
    }

    private static string? GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool GetBoolean(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind is JsonValueKind.True;

    private static string? GuessImageMediaType(string path)
        => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => MediaTypeNames.Image.Jpeg,
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => null
        };

}
