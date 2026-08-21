using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Occludra;

public partial class OccludraProvider
{
    private const string OccludraImageGenerationsEndpoint = "v1/images/generations";

    private static readonly JsonSerializerOptions OccludraImageJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public async Task<ImageResponse> ImageRequest(
        ImageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        if (request.Files?.Any() == true || request.Mask is not null)
            throw new NotSupportedException("Occludra does not document image edits.");

        var payload = CopyOccludraImageOptions(request.GetProviderMetadata<JsonElement>(GetIdentifier()));
        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;
        payload["response_format"] = "b64_json";
        SetOccludraImageOption(payload, "n", request.N);
        SetOccludraImageOption(payload, "size", request.Size);
        SetOccludraImageOption(payload, "aspect_ratio", request.AspectRatio);
        SetOccludraImageOption(payload, "seed", request.Seed);

        var result = await SendOccludraImageGenerationAsync(payload, cancellationToken);
        var images = await ToOccludraDataUrlsAsync(result.Response.Data, result.Response.OutputFormat, cancellationToken);

        if (images.Count == 0)
            throw new InvalidOperationException("Occludra image response did not contain any usable images.");

        return new ImageResponse
        {
            Images = images,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root),
            Usage = result.Response.Usage is null ? null : new ImageUsageData
            {
                InputTokens = result.Response.Usage.InputTokens,
                OutputTokens = result.Response.Usage.OutputTokens,
                TotalTokens = result.Response.Usage.TotalTokens
            },
            Response = new HeaderResponseData
            {
                Timestamp = result.Response.Created > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(result.Response.Created).UtcDateTime
                    : DateTime.UtcNow,
                Headers = result.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
        OpenAIImageGenerationRequest options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        // JsonExtensionData on AdditionalProperties keeps Occludra-specific fields as a raw,
        // top-level OpenAI-compatible passthrough without provider metadata DTOs.
        return _client.OpenAICompatibleImageGenerationRequestAsync(
            options,
            OccludraImageGenerationsEndpoint,
            cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        // Occludra documents image generation as non-streaming and ignores stream=true.
        await foreach (var streamEvent in _client.OpenAICompatibleImageGenerationNonStreamingAsStreamAsync(
            options,
            OccludraImageGenerationsEndpoint,
            cancellationToken))
        {
            yield return streamEvent;
        }
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Occludra does not document image edits.");

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Occludra does not document image edits.");

    private async Task<OccludraImageResult> SendOccludraImageGenerationAsync(
        JsonObject payload,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        using var request = new HttpRequestMessage(HttpMethod.Post, OccludraImageGenerationsEndpoint)
        {
            Content = new StringContent(
                payload.ToJsonString(OccludraImageJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Occludra image generation failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var parsed = root.Deserialize<OpenAIImagesResponse>(OccludraImageJsonOptions)
            ?? throw new InvalidOperationException("Occludra returned an invalid image response.");

        if (parsed.Data is null)
            throw new InvalidOperationException("Occludra image response did not contain a data array.");

        return new OccludraImageResult(root, response.GetHeaders(), parsed);
    }

    private async Task<List<string>> ToOccludraDataUrlsAsync(
        IEnumerable<OpenAIImageData>? data,
        string? outputFormat,
        CancellationToken cancellationToken)
    {
        var images = new List<string>();
        var fallbackMediaType = ToOccludraImageMediaType(outputFormat);

        foreach (var image in data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!string.IsNullOrWhiteSpace(image.B64Json))
            {
                images.Add($"data:{fallbackMediaType};base64,{image.B64Json}");
                continue;
            }

#pragma warning disable CS0618 // Occludra may return URL responses despite requesting b64_json.
            if (!string.IsNullOrWhiteSpace(image.Url))
                images.Add(await DownloadOccludraImageAsDataUrlAsync(image.Url, cancellationToken));
#pragma warning restore CS0618
        }

        return images;
    }

    private async Task<string> DownloadOccludraImageAsDataUrlAsync(
        string imageUrl,
        CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(
            imageUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? MediaTypeNames.Image.Png;
        return $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}";
    }

    private static JsonObject CopyOccludraImageOptions(JsonElement options)
    {
        if (options.ValueKind != JsonValueKind.Object)
            return [];

        return JsonNode.Parse(options.GetRawText()) as JsonObject ?? [];
    }

    private static void SetOccludraImageOption(JsonObject payload, string name, object? value)
    {
        if (value is null || value is string text && string.IsNullOrWhiteSpace(text))
            payload.Remove(name);
        else
            payload[name] = JsonValue.Create(value);
    }

    private static string ToOccludraImageMediaType(string? outputFormat)
        => outputFormat?.ToLowerInvariant() switch
        {
            "jpeg" or "jpg" => MediaTypeNames.Image.Jpeg,
            "webp" => "image/webp",
            _ => MediaTypeNames.Image.Png
        };

    private sealed record OccludraImageResult(
        JsonElement Root,
        Dictionary<string, string> Headers,
        OpenAIImagesResponse Response);
}
