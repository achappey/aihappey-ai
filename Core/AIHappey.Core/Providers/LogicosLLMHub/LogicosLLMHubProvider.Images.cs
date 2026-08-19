using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.LogicosLLMHub;

public partial class LogicosLLMHubProvider
{
    private const string ImageGenerationsEndpoint = "v1/images/generations";

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
        if (request.Files?.Any() == true || request.Mask is not null)
            throw new NotSupportedException("Logicos LLM Hub does not document image edits.");

        var payload = CopyProviderMetadata(request.GetProviderMetadata<JsonElement>(GetIdentifier()));
        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;
        SetOrRemove(payload, "n", request.N);
        SetOrRemove(payload, "size", request.Size);

        var result = await SendImageGenerationAsync(payload, cancellationToken);
        var images = new List<string>();
        foreach (var image in result.Response.Data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(image.B64Json))
            {
                images.Add($"data:{GetImageMediaType(result.Response.OutputFormat)};base64,{image.B64Json}");
            }
            else if (!string.IsNullOrWhiteSpace(image.Url))
            {
                images.Add(await DownloadAsDataUrlAsync(image.Url, cancellationToken));
            }
        }

        if (images.Count == 0)
            throw new InvalidOperationException("Logicos LLM Hub image response did not contain any usable images.");

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

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
        OpenAIImageGenerationRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var payload = JsonSerializer.SerializeToNode(options, ImageJsonOptions) as JsonObject
            ?? throw new InvalidOperationException("Could not serialize the Logicos LLM Hub image request.");
        payload.Remove("stream");

        var result = await SendImageGenerationAsync(payload, cancellationToken);
        result.Response.Background ??= options.Background;
        result.Response.OutputFormat ??= options.OutputFormat;
        result.Response.Quality ??= options.Quality;
        result.Response.Size ??= options.Size;
        return result.Response;
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageGenerationRequestAsync(options, cancellationToken);
        foreach (var image in response.Data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            var base64 = image.B64Json;
            if (string.IsNullOrWhiteSpace(base64) && !string.IsNullOrWhiteSpace(image.Url))
                base64 = await DownloadAsBase64Async(image.Url, cancellationToken);
            if (string.IsNullOrWhiteSpace(base64))
                continue;

            yield return new OpenAIImageGenerationCompleted
            {
                B64Json = base64,
                CreatedAt = response.Created,
                Background = response.Background,
                OutputFormat = response.OutputFormat,
                Quality = response.Quality,
                Size = response.Size,
                Usage = response.Usage
            };
        }
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Logicos LLM Hub does not document image edits.");

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Logicos LLM Hub does not document image edits.");

    private async Task<LogicosLLMHubImageResult> SendImageGenerationAsync(
        JsonObject payload,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Post, ImageGenerationsEndpoint)
        {
            Content = new StringContent(payload.ToJsonString(ImageJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Logicos LLM Hub image generation failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var parsed = root.Deserialize<OpenAIImagesResponse>(ImageJsonOptions)
            ?? throw new InvalidOperationException("Logicos LLM Hub returned an invalid image response.");
        if (parsed.Data is null)
            throw new InvalidOperationException("Logicos LLM Hub image response did not contain a data array.");

        return new(root, response.GetHeaders(), parsed);
    }

    private async Task<string> DownloadAsDataUrlAsync(string imageUrl, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(imageUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? MediaTypeNames.Image.Png;
        return $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}";
    }

    private async Task<string> DownloadAsBase64Async(string imageUrl, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(imageUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return Convert.ToBase64String(await response.Content.ReadAsByteArrayAsync(cancellationToken));
    }

    private static JsonObject CopyProviderMetadata(JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return [];

        return JsonNode.Parse(metadata.GetRawText()) as JsonObject ?? [];
    }

    private static void SetOrRemove(JsonObject payload, string propertyName, object? value)
    {
        if (value is null || value is string text && string.IsNullOrWhiteSpace(text))
            payload.Remove(propertyName);
        else
            payload[propertyName] = JsonValue.Create(value);
    }

    private static string GetImageMediaType(string? outputFormat)
        => outputFormat?.ToLowerInvariant() switch
        {
            "jpeg" or "jpg" => MediaTypeNames.Image.Jpeg,
            "webp" => "image/webp",
            _ => MediaTypeNames.Image.Png
        };

    private sealed record LogicosLLMHubImageResult(
        JsonElement Root,
        Dictionary<string, string> Headers,
        OpenAIImagesResponse Response);
}
