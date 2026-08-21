using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Neosantara;

public partial class NeosantaraProvider
{
    private static readonly JsonSerializerOptions NeosantaraImageJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly HashSet<string> NeosantaraImageReserved =
        new(["model", "prompt", "n", "size", "response_format"], StringComparer.OrdinalIgnoreCase);

    public async Task<ImageResponse> ImageRequestNeosantara(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var payload = CopyNeosantaraJsonObject(metadata, NeosantaraImageReserved);
        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;
        payload["n"] = request.N ?? 1;
        payload["size"] = request.Size ?? "1024x1024";
        payload["response_format"] = "b64_json";

        var warnings = new List<object>();
        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });
        if (!string.IsNullOrWhiteSpace(request.AspectRatio) && string.IsNullOrWhiteSpace(request.Size))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });
        if (request.Files?.Any() == true || request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "image_edit" });

        var (root, headers) = await SendNeosantaraImageAsync(payload, cancellationToken);
        var images = await ExtractNeosantaraImagesAsync(root, cancellationToken);
        if (images.Count == 0)
            throw new InvalidOperationException("Neosantara image response did not contain images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new()
            {
                Timestamp = ReadNeosantaraUnixTime(root, "created") ?? DateTime.UtcNow,
                Headers = headers,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        ApplyAuthHeader();
        return await _client.OpenAICompatibleImageGenerationRequestAsync(options, "v1/images/generations", cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        ApplyAuthHeader();
        await foreach (var streamEvent in _client.OpenAICompatibleImageGenerationNonStreamingAsStreamAsync(
            options, "v1/images/generations", cancellationToken))
            yield return streamEvent;
    }

    private async Task<(JsonElement Root, IDictionary<string, string> Headers)> SendNeosantaraImageAsync(
        Dictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, NeosantaraImageJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Neosantara image generation failed ({(int)response.StatusCode}): {raw}");
        using var document = JsonDocument.Parse(raw);
        return (document.RootElement.Clone(), response.GetHeaders());
    }

    private async Task<List<string>> ExtractNeosantaraImagesAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var images = new List<string>();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return images;

        foreach (var item in data.EnumerateArray())
        {
            if (item.TryGetProperty("b64_json", out var b64) && b64.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(b64.GetString()))
            {
                images.Add(b64.GetString()!.ToDataUrl(MediaTypeNames.Image.Png));
                continue;
            }
            if (!item.TryGetProperty("url", out var urlElement) || urlElement.ValueKind != JsonValueKind.String)
                continue;
            var url = urlElement.GetString();
            if (string.IsNullOrWhiteSpace(url))
                continue;
            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                images.Add(url);
                continue;
            }
            using var response = await _client.GetAsync(url, cancellationToken);
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!response.IsSuccessStatusCode || bytes.Length == 0)
                throw new InvalidOperationException($"Failed to download Neosantara image ({(int)response.StatusCode}).");
            images.Add(Convert.ToBase64String(bytes).ToDataUrl(response.Content.Headers.ContentType?.MediaType ?? MediaTypeNames.Image.Png));
        }
        return images;
    }

    private static Dictionary<string, object?> CopyNeosantaraJsonObject(JsonElement source, IReadOnlySet<string> reserved)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (source.ValueKind != JsonValueKind.Object)
            return result;
        foreach (var property in source.EnumerateObject())
            if (!reserved.Contains(property.Name))
                result[property.Name] = property.Value.Clone();
        return result;
    }

    private static DateTime? ReadNeosantaraUnixTime(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeSeconds(value.GetInt64()).UtcDateTime
            : null;
}
