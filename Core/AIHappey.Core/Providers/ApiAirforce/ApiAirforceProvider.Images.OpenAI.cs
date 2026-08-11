using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.ApiAirforce;

public partial class ApiAirforceProvider
{

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var payload = new Dictionary<string, object?>
        {
            ["model"] = NormalizeModelId(options.Model), ["prompt"] = options.Prompt,
            ["n"] = options.N, ["size"] = options.Size, ["quality"] = options.Quality,
            ["response_format"] = options.ResponseFormat ?? "b64_json"
        };
        MergeAdditionalProperties(payload, options.AdditionalProperties, "model", "prompt", "n", "size", "quality", "response_format", "sse");
        return await SendOpenAIImagesAsync(payload, options.Background, options.OutputFormat, options.Quality, options.Size, cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageGenerationRequestAsync(options, cancellationToken);
        foreach (var image in response.Data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(image.B64Json))
                yield return new OpenAIImageGenerationCompleted { B64Json = image.B64Json, CreatedAt = response.Created, Background = response.Background, OutputFormat = response.OutputFormat, Quality = response.Quality, Size = response.Size, Usage = response.Usage };
        }
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        if (options.Mask is not null || options.MaskFile is not null)
            throw new NotSupportedException("ApiAirforce does not document mask-based image editing.");

        var inputs = new List<Dictionary<string, string>>();
        foreach (var image in options.Images ?? [])
        {
            var value = image.ImageUrl ?? image.FileId;
            if (!string.IsNullOrWhiteSpace(value))
                inputs.Add(ToApiAirforceImageInput(value));
        }
        foreach (var file in options.ImageFiles ?? [])
        {
            await using var stream = file.OpenReadStream();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, cancellationToken);
            inputs.Add(new Dictionary<string, string> { ["b64_json"] = Convert.ToBase64String(memory.ToArray()) });
        }
        if (inputs.Count == 0)
            throw new ArgumentException("At least one input image is required.", nameof(options));

        var payload = new Dictionary<string, object?>
        {
            ["model"] = NormalizeModelId(options.Model), ["prompt"] = options.Prompt,
            ["n"] = options.N, ["size"] = options.Size, ["quality"] = options.Quality,
            ["response_format"] = "b64_json", ["input_images"] = inputs
        };
        MergeAdditionalProperties(payload, options.AdditionalProperties, "model", "prompt", "n", "size", "quality", "response_format", "input_images", "sse");
        return await SendOpenAIImagesAsync(payload, options.Background, options.OutputFormat, options.Quality, options.Size, cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageEditRequestAsync(options, cancellationToken);
        foreach (var image in response.Data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(image.B64Json))
                yield return new OpenAIImageGenerationCompleted { B64Json = image.B64Json, CreatedAt = response.Created, Background = response.Background, OutputFormat = response.OutputFormat, Quality = response.Quality, Size = response.Size, Usage = response.Usage };
        }
    }

    private async Task<OpenAIImagesResponse> SendOpenAIImagesAsync(Dictionary<string, object?> payload, string? background, string? outputFormat, string? quality, string? size, CancellationToken cancellationToken)
    {
        var root = await SendMediaGenerationAsync(payload, cancellationToken);
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("ApiAirforce image generation returned no data array.");

        var images = new List<OpenAIImageData>();
        foreach (var item in data.EnumerateArray())
        {
            var b64 = TryGetString(item, "b64_json");
            var url = TryGetString(item, "url");
            if (string.IsNullOrWhiteSpace(b64) && !string.IsNullOrWhiteSpace(url))
            {
                var download = await TryFetchAsBase64Async(url, cancellationToken);
                b64 = download?.Base64;
            }
            if (!string.IsNullOrWhiteSpace(b64))
                images.Add(new OpenAIImageData { B64Json = b64, RevisedPrompt = TryGetString(item, "revised_prompt") });
        }
        return new OpenAIImagesResponse
        {
            Created = root.TryGetProperty("created", out var created) && created.TryGetInt64(out var timestamp) ? timestamp : DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Data = images, Background = background, OutputFormat = outputFormat, Quality = quality, Size = size
        };
    }

    private static Dictionary<string, string> ToApiAirforceImageInput(string value)
        => value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? new Dictionary<string, string> { ["url"] = value }
            : new Dictionary<string, string> { ["b64_json"] = StripDataUrl(value) };

    private static void MergeAdditionalProperties(Dictionary<string, object?> payload, Dictionary<string, JsonElement>? additional, params string[] blockedNames)
    {
        if (additional is null) return;
        var blocked = new HashSet<string>(blockedNames, StringComparer.OrdinalIgnoreCase);
        foreach (var property in additional)
            if (!blocked.Contains(property.Key)) payload[property.Key] = JsonSerializer.Deserialize<object?>(property.Value.GetRawText(), ApiAirforceMediaJsonOptions);
    }

}
