using AIHappey.Core.AI;
using AIHappey.Common.Extensions;
using AIHappey.Core.Extensions;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.RewindAI;

public partial class RewindAIProvider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        ApplyAuthHeader();
        List<object> warnings = [];
        if (request.Files?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "files", message = "RewindAI image generation does not document image-to-image inputs." });
        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask", message = "RewindAI image generation does not document masks." });

        var payload = CreateRewindAIPayload(request.ProviderOptions,
            ("model", request.Model),
            ("prompt", request.Prompt),
            ("size", request.Size),
            ("n", request.N),
            ("seed", request.Seed));
        var requestBody = JsonSerializer.Serialize(payload, RewindAIJson);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
        {
            Content = new StringContent(requestBody, Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"RewindAI image generation failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var images = ExtractRewindAIImages(root, ReadRewindAIString(root, "format", "output_format"));
        if (images.Count == 0)
            throw new InvalidOperationException("RewindAI image generation response did not contain generated images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                Headers = response.GetHeaders(),
                ModelId = ReadRewindAIString(root, "model").ToModelId(GetIdentifier())
                    ?? request.Model.ToModelId(GetIdentifier())
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
        var response = await ImageRequest(options.ToImageRequest(options.Model, GetIdentifier()), cancellationToken);
        foreach (var streamEvent in response.ToOpenAIImageGenerationCompletedEvents(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return streamEvent;
        }
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("RewindAI does not expose an image-edit endpoint.");
    }

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("RewindAI does not expose an image-edit endpoint.");
    }

    private static List<string> ExtractRewindAIImages(JsonElement root, string format)
    {
        List<string> images = [];
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
                AddRewindAIImage(images, item, format);
        }
        else if (root.TryGetProperty("images", out var imageList) && imageList.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in imageList.EnumerateArray())
                AddRewindAIImage(images, item, format);
        }
        else
        {
            AddRewindAIImage(images, root, format);
        }

        return images.Distinct(StringComparer.Ordinal).ToList();
    }

    private static void AddRewindAIImage(List<string> images, JsonElement item, string fallbackFormat)
    {
        var value = item.ValueKind == JsonValueKind.String
            ? item.GetString()
            : ReadRewindAIString(item, "url", "b64_json", "image", "data");
        if (string.IsNullOrWhiteSpace(value))
            return;

        if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && !IsRewindAIAbsoluteUrl(value))
        {
            var format = item.ValueKind == JsonValueKind.Object
                ? ReadRewindAIString(item, "format", "output_format")
                : string.Empty;
            value = value.ToDataUrl(GetRewindAIImageMimeType(string.IsNullOrWhiteSpace(format) ? fallbackFormat : format));
        }

        images.Add(value);
    }

}
