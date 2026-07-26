using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Aether;

public partial class AetherProvider
{
    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
        OpenAIImageGenerationRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        ApplyAuthHeader();

        var payload = new Dictionary<string, object?>
        {
            ["model"] = options.Model,
            ["prompt"] = options.Prompt,
            ["n"] = options.N,
            ["size"] = options.Size,
            ["quality"] = options.Quality,
            ["style"] = options.Style,
            // AIHappey image contracts only expose base64 data. Ask Aether for it
            // directly, but still support URL responses below for provider resilience.
            ["response_format"] = "b64_json"
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, AetherImageJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Aether image generation failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Aether image generation response did not include a data array.");

        var images = new List<OpenAIImageData>();
        foreach (var item in data.EnumerateArray())
        {
            var base64 = item.TryGetProperty("b64_json", out var b64) && b64.ValueKind == JsonValueKind.String
                ? b64.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(base64)
                && item.TryGetProperty("url", out var url)
                && url.ValueKind == JsonValueKind.String
                && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var imageUri))
            {
                using var imageResponse = await _client.GetAsync(imageUri, cancellationToken);
                if (!imageResponse.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Aether image download failed ({(int)imageResponse.StatusCode}): {imageUri}");

                base64 = Convert.ToBase64String(await imageResponse.Content.ReadAsByteArrayAsync(cancellationToken));
            }

            if (string.IsNullOrWhiteSpace(base64))
                throw new InvalidOperationException("Aether image generation returned an image without base64 data or a downloadable URL.");

            images.Add(new OpenAIImageData
            {
                B64Json = base64,
                RevisedPrompt = item.TryGetProperty("revised_prompt", out var revisedPrompt) && revisedPrompt.ValueKind == JsonValueKind.String
                    ? revisedPrompt.GetString()
                    : null
            });
        }

        return new OpenAIImagesResponse
        {
            Created = root.TryGetProperty("created", out var created) && created.TryGetInt64(out var createdAt)
                ? createdAt
                : DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Data = images,
            Background = options.Background,
            OutputFormat = options.OutputFormat,
            Quality = options.Quality,
            Size = options.Size
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
            if (string.IsNullOrWhiteSpace(image.B64Json))
                continue;

            yield return new OpenAIImageGenerationCompleted
            {
                B64Json = image.B64Json,
                CreatedAt = response.Created,
                Background = response.Background,
                OutputFormat = response.OutputFormat,
                Quality = response.Quality,
                Size = response.Size,
                Usage = response.Usage
            };
        }
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Aether does not document an image-edit endpoint.");
    }

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Aether does not document an image-edit endpoint.");
    }



}
