using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.SenseNova;

public sealed partial class SenseNovaProvider
{
    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
        OpenAIImageGenerationRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        return await SendImageRequestAsync(options, "generations", cancellationToken);
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        if (options.Mask is not null || options.MaskFile is not null)
            throw new NotSupportedException("SenseNova does not document mask-based image editing.");

        var images = new List<OpenAIImageReference>();
        foreach (var image in options.Images ?? [])
            images.Add(new() { ImageUrl = ToEditImageUrl(image.ImageUrl) });
        foreach (var file in options.ImageFiles ?? [])
        {
            await using var stream = file.OpenReadStream();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, cancellationToken);
            images.Add(new()
            {
                ImageUrl = ToEditImageUrl(Convert.ToBase64String(memory.ToArray()), file.ContentType)
            });
        }

        var payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in JsonSerializer.SerializeToElement(options, ImageJsonOptions).EnumerateObject())
            payload[property.Name] = property.Value.Clone();
        payload["images"] = JsonSerializer.SerializeToElement(images, ImageJsonOptions);
        return await SendImageRequestAsync(payload, "edits", cancellationToken);
    }

    private async Task<OpenAIImagesResponse> SendImageRequestAsync(
        object options, string operation, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        var payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in JsonSerializer.SerializeToElement(options, ImageJsonOptions).EnumerateObject())
            payload[property.Name] = property.Value.Clone();
        payload["stream"] = JsonSerializer.SerializeToElement(false);

        using var response = await _client.PostAsJsonAsync($"v1/images/{operation}", payload, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"SenseNova image {operation} failed with status {(int)response.StatusCode}: {error}",
                null,
                response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<OpenAIImagesResponse>(ImageJsonOptions, cancellationToken)
            ?? throw new InvalidOperationException($"SenseNova image {operation} returned an empty response.");
    }

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Mask is not null)
            throw new NotSupportedException("SenseNova does not document mask-based image editing.");

        var options = new OpenAIImageGenerationRequest
        {
            Model = request.Model,
            Prompt = request.Prompt,
            N = request.N,
            Size = request.Size,
            ResponseFormat = "b64_json",
            AdditionalProperties = request.ProviderOptions is not null &&
                                   request.ProviderOptions.TryGetValue(GetIdentifier(), out var native) &&
                                   native.ValueKind == JsonValueKind.Object
                ? native.EnumerateObject()
                    .Where(property => property.Name is not ("model" or "prompt" or "n" or "size" or "response_format"))
                    .ToDictionary(property => property.Name, property => property.Value.Clone())
                : null
        };
        var files = request.Files?.ToArray() ?? [];
        var response = files.Length == 0
            ? await OpenAIImageGenerationRequestAsync(options, cancellationToken)
            : await OpenAIImageEditRequestAsync(new()
            {
                Model = options.Model,
                Prompt = options.Prompt,
                N = options.N,
                Size = options.Size,
                Images = files.Select(file => new OpenAIImageReference
                {
                    ImageUrl = ToEditImageUrl(file is ImageFileUrl url ? url.Url : file.Data, file.MediaType)
                }).ToArray(),
                AdditionalProperties = new(options.AdditionalProperties ?? [])
                {
                    ["response_format"] = JsonSerializer.SerializeToElement("b64_json")
                }
            }, cancellationToken);
        var mediaType = response.OutputFormat?.ToLowerInvariant() switch
        {
            "jpg" or "jpeg" => "image/jpeg",
            "webp" => "image/webp",
            _ => "image/png"
        };
        var images = response.Data?
            .Where(image => !string.IsNullOrWhiteSpace(image.B64Json))
            .Select(image => image.B64Json!.ToDataUrl(mediaType))
            .ToArray() ?? [];
        if (images.Length == 0)
            throw new InvalidOperationException("SenseNova image request returned no images.");

        var warnings = new List<object>();
        if (request.Seed.HasValue)
            warnings.Add(new { type = "unsupported", feature = "seed" });
        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });

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
            Response = new()
            {
                Timestamp = response.Created > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(response.Created).UtcDateTime
                    : DateTime.UtcNow,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private static string ToEditImageUrl(string? image, string? mediaType = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(image);
        if (Uri.TryCreate(image, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https")
            return image;
        if (image.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) &&
            image.Contains(";base64,", StringComparison.OrdinalIgnoreCase))
            return image;

        _ = Convert.FromBase64String(image);
        return image.ToDataUrl(string.IsNullOrWhiteSpace(mediaType) ? "image/png" : mediaType);
    }

    private static readonly JsonSerializerOptions ImageJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
