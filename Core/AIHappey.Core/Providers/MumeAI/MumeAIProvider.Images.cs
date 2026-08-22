using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Net.Http.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.MumeAI;

public partial class MumeAIProvider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        ApplyAuthHeader();
        var payload = MumePayload(GetMumeProviderOptions(request.ProviderOptions));
        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;
        if (!string.IsNullOrWhiteSpace(request.AspectRatio)) payload["aspect_ratio"] = request.AspectRatio;
        if (!string.IsNullOrWhiteSpace(request.Size)) payload["size"] = request.Size;
        if (request.N.HasValue) payload["n"] = request.N.Value;
        if (request.Seed.HasValue) payload["seed"] = request.Seed.Value;

        var references = request.Files?.Select(file => new Dictionary<string, object?>
        {
            ["type"] = "image_url",
            ["image_url"] = new Dictionary<string, object?>
            {
                ["url"] = NormalizeMumeImage(file)
            }
        }).ToList();
        if (references?.Count > 0)
            payload["input_references"] = references;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
        {
            Content = JsonContent.Create(payload)
        };
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Mume AI image generation failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var images = new List<string>();
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (item.TryGetProperty("b64_json", out var base64) && base64.ValueKind == JsonValueKind.String)
                {
                    images.Add(base64.GetString()!.ToDataUrl("image/png"));
                    continue;
                }

                var url = MumeString(item, "url");
                if (string.IsNullOrWhiteSpace(url))
                    continue;
                if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    images.Add(url);
                    continue;
                }

                using var imageResponse = await _client.GetAsync(url, cancellationToken);
                var bytes = await imageResponse.Content.ReadAsByteArrayAsync(cancellationToken);
                if (!imageResponse.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Mume AI image download failed ({(int)imageResponse.StatusCode}).");
                images.Add(Convert.ToBase64String(bytes).ToDataUrl(imageResponse.Content.Headers.ContentType?.MediaType ?? "image/png"));
            }
        }

        return new ImageResponse
        {
            Images = images,
            Warnings = [],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new()
            {
                Timestamp = ReadMumeCreated(root) ?? DateTime.UtcNow,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }
        
    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return _client.OpenAICompatibleImageGenerationRequestAsync(options, "v1/images/generations", cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        await foreach (var item in _client.OpenAICompatibleImageGenerationNonStreamingAsStreamAsync(
            options, "v1/images/generations", cancellationToken))
            yield return item;
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Mume AI does not document an image edits endpoint.");
    }

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Mume AI does not document an image edits endpoint.");
    }

    private static string NormalizeMumeImage(ImageFile file)
    {
        if (string.IsNullOrWhiteSpace(file.Data))
            throw new ArgumentException("Image reference data is required.", nameof(file));
        if (file.Data.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || file.Data.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return file.Data;
        return file.Data.ToDataUrl(string.IsNullOrWhiteSpace(file.MediaType) ? "image/png" : file.MediaType);
    }

    private static DateTime? ReadMumeCreated(JsonElement root)
        => root.TryGetProperty("created", out var created)
            && created.ValueKind == JsonValueKind.Number
            && created.TryGetInt64(out var unix)
                ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime
                : null;

}
