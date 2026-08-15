using AIHappey.Core.AI;
using AIHappey.Common.Extensions;
using AIHappey.Core.Extensions;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.ArliAI;

public partial class ArliAIProvider
{
    private const string TextToImageEndpoint = "v1/txt2img";
    private const string ImageToImageEndpoint = "v1/img2img";

    private static readonly JsonSerializerOptions ArliImageJsonOptions = new(JsonSerializerDefaults.Web)
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

        var files = request.Files?.ToList() ?? [];
        var isEdit = files.Count > 0 || request.Mask is not null;
        var payload = CopyObject(request.GetProviderMetadata<JsonElement>(GetIdentifier()));
        AddIfMissing(payload, "sd_model_checkpoint", request.Model);
        AddIfMissing(payload, "prompt", request.Prompt);
        AddIfMissing(payload, "batch_size", request.N);
        AddIfMissing(payload, "seed", request.Seed);
        AddSizeIfMissing(payload, request.Size);

        if (files.Count > 0 && !payload.ContainsKey("init_images"))
            payload["init_images"] = new JsonArray(files.Select(x => JsonValue.Create(StripDataUrl(x.Data))).ToArray());
        if (request.Mask is not null && !payload.ContainsKey("mask"))
            payload["mask"] = StripDataUrl(request.Mask.Data);

        var result = await SendArliImageRequestAsync(payload, isEdit ? ImageToImageEndpoint : TextToImageEndpoint, cancellationToken);
        var images = GetImages(result.Root)
            .Select(x => ToDataUrl(x, "image/png"))
            .ToList();
        if (images.Count == 0)
            throw new InvalidOperationException("ArliAI image response did not contain generated images.");

        return new ImageResponse
        {
            Images = images,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root),
            Response = new HeaderResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = result.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }


    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var payload = CopyObject(options.AdditionalProperties);
        AddIfMissing(payload, "sd_model_checkpoint", options.Model);
        AddIfMissing(payload, "prompt", options.Prompt);
        AddIfMissing(payload, "batch_size", options.N);
        AddSizeIfMissing(payload, options.Size);
        var result = await SendArliImageRequestAsync(payload, TextToImageEndpoint, cancellationToken);
        return ToOpenAIResponse(result.Root, options.Background, options.OutputFormat, options.Quality, options.Size);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageGenerationRequestAsync(options, cancellationToken);
        foreach (var image in response.Data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(image.B64Json))
                yield return new OpenAIImageGenerationCompleted
                {
                    B64Json = image.B64Json,
                    CreatedAt = response.Created,
                    Background = response.Background,
                    OutputFormat = response.OutputFormat,
                    Quality = response.Quality,
                    Size = response.Size
                };
        }
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        var payload = CopyObject(options.AdditionalProperties);
        AddIfMissing(payload, "sd_model_checkpoint", options.Model);
        AddIfMissing(payload, "prompt", options.Prompt);
        AddIfMissing(payload, "batch_size", options.N);
        AddSizeIfMissing(payload, options.Size);

        if (!payload.ContainsKey("init_images"))
        {
            var images = new JsonArray();
            foreach (var image in options.Images ?? [])
                if (!string.IsNullOrWhiteSpace(image.ImageUrl))
                    images.Add(await ResolveBase64Async(image.ImageUrl, cancellationToken));
            foreach (var file in options.ImageFiles ?? [])
            {
                await using var stream = file.OpenReadStream();
                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory, cancellationToken);
                images.Add(Convert.ToBase64String(memory.ToArray()));
            }
            if (images.Count > 0)
                payload["init_images"] = images;
        }

        if (!payload.ContainsKey("mask"))
        {
            if (!string.IsNullOrWhiteSpace(options.Mask?.ImageUrl))
                payload["mask"] = await ResolveBase64Async(options.Mask.ImageUrl, cancellationToken);
            else if (options.MaskFile is not null)
            {
                await using var stream = options.MaskFile.OpenReadStream();
                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory, cancellationToken);
                payload["mask"] = Convert.ToBase64String(memory.ToArray());
            }
        }

        var result = await SendArliImageRequestAsync(payload, ImageToImageEndpoint, cancellationToken);
        return ToOpenAIResponse(result.Root, options.Background, options.OutputFormat, options.Quality, options.Size);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageEditRequestAsync(options, cancellationToken);
        foreach (var image in response.Data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(image.B64Json))
                yield return new OpenAIImageEditCompleted
                {
                    B64Json = image.B64Json,
                    CreatedAt = response.Created,
                    Background = response.Background,
                    OutputFormat = response.OutputFormat,
                    Quality = response.Quality,
                    Size = response.Size
                };
        }
    }

    private async Task<ArliImageResult> SendArliImageRequestAsync(JsonObject payload, string endpoint, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload.ToJsonString(ArliImageJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"ArliAI image request failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        return new ArliImageResult(document.RootElement.Clone(), response.GetHeaders());
    }

    private async Task<string> ResolveBase64Async(string value, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme.Equals("data", StringComparison.OrdinalIgnoreCase))
            return StripDataUrl(value);
        using var response = await _client.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        return Convert.ToBase64String(await response.Content.ReadAsByteArrayAsync(cancellationToken));
    }

    private static JsonObject CopyObject(JsonElement element)
        => element.ValueKind == JsonValueKind.Object ? JsonNode.Parse(element.GetRawText()) as JsonObject ?? [] : [];

    private static JsonObject CopyObject(Dictionary<string, JsonElement>? values)
        => values is null ? [] : JsonSerializer.SerializeToNode(values, ArliImageJsonOptions) as JsonObject ?? [];

    private static void AddIfMissing<T>(JsonObject payload, string name, T value)
    {
        if (!payload.ContainsKey(name) && value is not null)
            payload[name] = JsonSerializer.SerializeToNode(value, ArliImageJsonOptions);
    }

    private static void AddSizeIfMissing(JsonObject payload, string? size)
    {
        if (string.IsNullOrWhiteSpace(size)) return;
        var parts = size.Split('x', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return;
        if (!payload.ContainsKey("width") && int.TryParse(parts[0], out var width)) payload["width"] = width;
        if (!payload.ContainsKey("height") && int.TryParse(parts[1], out var height)) payload["height"] = height;
    }

    private static IEnumerable<string> GetImages(JsonElement root)
    {
        if (!root.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var image in images.EnumerateArray())
            if (image.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(image.GetString()))
                yield return StripDataUrl(image.GetString()!);
    }

    private static OpenAIImagesResponse ToOpenAIResponse(JsonElement root, string? background, string? format, string? quality, string? size)
        => new()
        {
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Background = background,
            OutputFormat = format ?? "png",
            Quality = quality,
            Size = size,
            Data = GetImages(root).Select(x => new OpenAIImageData { B64Json = x }).ToList()
        };

    private static string StripDataUrl(string value)
    {
        var marker = value.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
        return marker < 0 ? value : value[(marker + 8)..];
    }

    private static string ToDataUrl(string base64, string mediaType)
        => $"data:{mediaType};base64,{StripDataUrl(base64)}";

    private sealed record ArliImageResult(JsonElement Root, IDictionary<string, string> Headers);
}
