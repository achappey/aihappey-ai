using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.NexosAI;

public partial class NexosAIProvider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await GenerateNexosImagesAsync(request.Model, request.Prompt, request.N, request.Size,
            GetProviderString(request.ProviderOptions, "quality"),
            GetProviderString(request.ProviderOptions, "style"), cancellationToken);
        var warnings = new List<object>();
        if (request.Files?.Any() == true) warnings.Add(new { type = "unsupported", feature = "files" });
        if (request.Mask is not null) warnings.Add(new { type = "unsupported", feature = "mask" });
        if (!string.IsNullOrWhiteSpace(request.AspectRatio)) warnings.Add(new { type = "unsupported", feature = "aspectRatio" });
        if (request.Seed is not null) warnings.Add(new { type = "unsupported", feature = "seed" });

        return new ImageResponse
        {
            Images = result.Images.Select(x => x.Base64.ToDataUrl(MediaTypeNames.Image.Png)).ToList(),
            Warnings = warnings,
            Usage = ReadImageUsage(result.Root),
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(
                result.Root.EnumerateObject().Where(x => x.Name != "data").ToDictionary(x => x.Name, x => x.Value.Clone())),
            Response = new HeaderResponseData
            {
                Timestamp = result.Timestamp,
                Headers = result.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var result = await GenerateNexosImagesAsync(options.Model, options.Prompt, options.N, options.Size,
            options.Quality, options.Style, cancellationToken);

        return new OpenAIImagesResponse
        {
            Created = result.Root.TryGetProperty("created", out var created) && created.TryGetInt64(out var value)
                ? value : DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Data = result.Images.Select(x => new OpenAIImageData { B64Json = x.Base64, RevisedPrompt = x.RevisedPrompt }).ToList(),
            Background = options.Background,
            OutputFormat = options.OutputFormat,
            Quality = options.Quality,
            Size = options.Size
        };
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageGenerationRequestAsync(options, cancellationToken);
        foreach (var image in response.Data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(image.B64Json))
                yield return new OpenAIImageGenerationCompleted
                {
                    B64Json = image.B64Json, CreatedAt = response.Created, Background = response.Background,
                    OutputFormat = response.OutputFormat, Quality = response.Quality, Size = response.Size
                };
        }
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("NexosAI does not document an image-edit endpoint.");

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("NexosAI does not document an image-edit endpoint.");

    private async Task<NexosImagesResult> GenerateNexosImagesAsync(string model, string prompt, int? n,
        string? size, string? quality, string? style, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("Model is required.", nameof(model));
        if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("Prompt is required.", nameof(prompt));
        if (n is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(n), "Image count must be between 1 and 10.");

        ApplyAuthHeader();
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model, ["prompt"] = prompt, ["n"] = n, ["size"] = size,
            ["quality"] = quality, ["style"] = style, ["response_format"] = "b64_json"
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        var timestamp = DateTime.UtcNow;
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"NexosAI image generation failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("NexosAI image response did not contain a data array.");

        var images = new List<NexosImage>();
        foreach (var item in data.EnumerateArray())
        {
            var base64 = item.TryGetProperty("b64_json", out var b64) ? b64.GetString() : null;
            if (string.IsNullOrWhiteSpace(base64) && item.TryGetProperty("url", out var url)
                && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var imageUri))
            {
                using var download = await _client.GetAsync(imageUri, cancellationToken);
                if (!download.IsSuccessStatusCode)
                    throw new InvalidOperationException($"NexosAI image download failed ({(int)download.StatusCode}).");
                base64 = Convert.ToBase64String(await download.Content.ReadAsByteArrayAsync(cancellationToken));
            }
            if (string.IsNullOrWhiteSpace(base64))
                throw new InvalidOperationException("NexosAI returned an image without data.");
            images.Add(new(base64, item.TryGetProperty("revised_prompt", out var revised) ? revised.GetString() : null));
        }
        return new(root, response.GetHeaders(), timestamp, images);
    }

    private string? GetProviderString(Dictionary<string, JsonElement>? options, string name)
        => options is not null && options.TryGetValue(GetIdentifier(), out var provider)
            && provider.ValueKind == JsonValueKind.Object && provider.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static ImageUsageData? ReadImageUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;
        return new ImageUsageData
        {
            InputTokens = usage.TryGetProperty("input_tokens", out var input) && input.TryGetInt32(out var i) ? i : null,
            OutputTokens = usage.TryGetProperty("output_tokens", out var output) && output.TryGetInt32(out var o) ? o : null,
            TotalTokens = usage.TryGetProperty("total_tokens", out var total) && total.TryGetInt32(out var t) ? t : null
        };
    }

    private sealed record NexosImage(string Base64, string? RevisedPrompt);
    private sealed record NexosImagesResult(JsonElement Root, IDictionary<string, string> Headers, DateTime Timestamp, List<NexosImage> Images);
}
