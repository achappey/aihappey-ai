using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.LLMAPI;

public partial class LLMAPIProvider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("Prompt is required.", nameof(request));
        if (request.Files?.Any() == true || request.Mask is not null)
            throw new NotSupportedException("LLMAPI image edits are not documented.");

        var imageConfig = GetLLMAPIImageConfig(request.ProviderOptions);
        if (!string.IsNullOrWhiteSpace(request.Size)) imageConfig["image_size"] = request.Size;
        if (!string.IsNullOrWhiteSpace(request.AspectRatio)) imageConfig["aspect_ratio"] = request.AspectRatio;
        if (request.N is not null) imageConfig["n"] = request.N;
        if (request.Seed is not null) imageConfig["seed"] = request.Seed;

        var result = await GenerateLLMAPIImagesAsync(request.Model, request.Prompt, imageConfig, cancellationToken);
        return new ImageResponse
        {
            Images = result.DataUrls,
            Usage = ReadLLMAPIImageUsage(result.Root),
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root),
            Response = new HeaderResponseData
            {
                Timestamp = ReadLLMAPICreated(result.Root),
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
        var imageConfig = options.AdditionalProperties is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : options.AdditionalProperties.ToDictionary(x => x.Key, x => (object?)x.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(options.Size)) imageConfig["image_size"] = options.Size;
        if (options.N is not null) imageConfig["n"] = options.N;
        if (!string.IsNullOrWhiteSpace(options.Quality)) imageConfig["quality"] = options.Quality;

        var result = await GenerateLLMAPIImagesAsync(options.Model, options.Prompt, imageConfig, cancellationToken);
        return new OpenAIImagesResponse
        {
            Created = new DateTimeOffset(ReadLLMAPICreated(result.Root)).ToUnixTimeSeconds(),
            Background = options.Background,
            OutputFormat = options.OutputFormat,
            Quality = options.Quality,
            Size = options.Size,
            Data = result.DataUrls.Select(url => new OpenAIImageData { B64Json = ExtractLLMAPIImageBase64(url) }).ToList(),
            Usage = ReadLLMAPIOpenAIImageUsage(result.Root)
        };
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
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

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("LLMAPI image edits are not documented.");

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("LLMAPI image edits are not documented.");

    private async Task<LLMAPIImageResult> GenerateLLMAPIImagesAsync(
        string model,
        string prompt,
        Dictionary<string, object?> imageConfig,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = new[] { new { role = "user", content = prompt } }
        };
        if (imageConfig.Count > 0) payload["image_config"] = imageConfig;

        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"LLMAPI image generation failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var images = new List<string>();
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            foreach (var choice in choices.EnumerateArray())
            {
                if (!choice.TryGetProperty("message", out var message)
                    || !message.TryGetProperty("images", out var messageImages)
                    || messageImages.ValueKind != JsonValueKind.Array) continue;
                foreach (var image in messageImages.EnumerateArray())
                {
                    if (image.TryGetProperty("image_url", out var imageUrl)
                        && imageUrl.TryGetProperty("url", out var url)
                        && url.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(url.GetString())) images.Add(url.GetString()!);
                }
            }
        }
        if (images.Count == 0) throw new InvalidOperationException("LLMAPI image response did not contain assistant message images.");
        return new LLMAPIImageResult(root, response.GetHeaders(), images);
    }

    private Dictionary<string, object?> GetLLMAPIImageConfig(Dictionary<string, JsonElement>? providerOptions)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var options = GetLLMAPIProviderOptions(providerOptions);
        if (options is null) return result;
        foreach (var option in options)
        {
            if (option.Key.Equals("image_config", StringComparison.OrdinalIgnoreCase)
                && option.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in option.Value.EnumerateObject()) result[property.Name] = property.Value.Clone();
            }
            else result[option.Key] = option.Value.Clone();
        }
        return result;
    }

    private static string ExtractLLMAPIImageBase64(string dataUrl)
    {
        if (!dataUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("LLMAPI returned a non-data image URL where base64 image data was required.");
        var comma = dataUrl.IndexOf(',');
        if (comma < 0) throw new InvalidOperationException("LLMAPI returned an invalid image data URL.");
        return dataUrl[(comma + 1)..];
    }

    private static DateTime ReadLLMAPICreated(JsonElement root)
        => root.TryGetProperty("created", out var created) && created.TryGetInt64(out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
            : DateTime.UtcNow;

    private static ImageUsageData? ReadLLMAPIImageUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;
        return new ImageUsageData
        {
            InputTokens = ReadLLMAPIInt(usage, "prompt_tokens"),
            OutputTokens = ReadLLMAPIInt(usage, "completion_tokens"),
            TotalTokens = ReadLLMAPIInt(usage, "total_tokens")
        };
    }

    private static OpenAIImageUsage? ReadLLMAPIOpenAIImageUsage(JsonElement root)
    {
        var usage = ReadLLMAPIImageUsage(root);
        return usage is null ? null : new OpenAIImageUsage
        {
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens,
            TotalTokens = usage.TotalTokens
        };
    }

    private static int? ReadLLMAPIInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;

    private sealed record LLMAPIImageResult(JsonElement Root, Dictionary<string, string> Headers, List<string> DataUrls);
}
