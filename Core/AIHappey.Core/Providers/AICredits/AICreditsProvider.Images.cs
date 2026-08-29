using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.AICredits;

public partial class AICreditsProvider
{
    private static readonly JsonSerializerOptions AICreditsImageJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateAICreditsImageRequest(request.Model, request.Prompt);

        var warnings = new List<object>();
        if (request.Files?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "files" });
        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });
        if (request.Seed.HasValue)
            warnings.Add(new { type = "unsupported", feature = "seed" });
        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });

        var payload = GetAICreditsOptions(request.ProviderOptions);
        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;
        if (request.N.HasValue) payload["n"] = request.N.Value;
        if (!string.IsNullOrWhiteSpace(request.Size)) payload["size"] = request.Size;
        payload["response_format"] = "b64_json";

        var result = await SendAICreditsImageRequestAsync(payload, cancellationToken);
        var response = await ToAICreditsOpenAIImagesResponseAsync(result.Root, cancellationToken);
        var images = response.Data?
            .Where(static image => !string.IsNullOrWhiteSpace(image.B64Json))
            .Select(static image => $"data:image/png;base64,{image.B64Json}")
            .ToList() ?? [];

        if (images.Count == 0)
            throw new InvalidOperationException("AICredits image generation returned no images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root),
            Response = new HeaderResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = result.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
        OpenAIImageGenerationRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateAICreditsImageRequest(options.Model, options.Prompt);

        var payload = JsonSerializer.SerializeToNode(options, AICreditsImageJsonOptions)?.AsObject()
            ?? throw new InvalidOperationException("Could not serialize the AICredits image request.");
        payload["response_format"] = "b64_json";

        var result = await SendAICreditsImageRequestAsync(payload, cancellationToken);
        var response = await ToAICreditsOpenAIImagesResponseAsync(result.Root, cancellationToken);
        response.Background ??= options.Background;
        response.OutputFormat ??= options.OutputFormat;
        response.Quality ??= options.Quality;
        response.Size ??= options.Size;
        return response;
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageGenerationRequestAsync(options, cancellationToken);
        foreach (var image in response.Data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(image.B64Json)) continue;
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

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("AICredits does not document an image-edit endpoint.");

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await OpenAIImageEditRequestAsync(options, cancellationToken);
        yield break;
    }

    private async Task<AICreditsJsonResult> SendAICreditsImageRequestAsync(JsonObject payload, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
        {
            Content = new StringContent(payload.ToJsonString(AICreditsImageJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"AICredits image generation failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        return new AICreditsJsonResult(document.RootElement.Clone(), response.GetHeaders());
    }

    private async Task<OpenAIImagesResponse> ToAICreditsOpenAIImagesResponseAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var response = JsonSerializer.Deserialize<OpenAIImagesResponse>(root.GetRawText(), AICreditsImageJsonOptions)
            ?? throw new InvalidOperationException("AICredits returned an invalid image response.");

        response.Data ??= [];
        foreach (var image in response.Data)
        {
            if (!string.IsNullOrWhiteSpace(image.B64Json)) continue;
#pragma warning disable CS0618
            if (string.IsNullOrWhiteSpace(image.Url)) continue;
            using var download = await _client.GetAsync(image.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
#pragma warning restore CS0618
            var bytes = await download.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!download.IsSuccessStatusCode)
                throw new InvalidOperationException($"AICredits image download failed ({(int)download.StatusCode}): {Encoding.UTF8.GetString(bytes)}");
            image.B64Json = Convert.ToBase64String(bytes);
        }

        if (response.Created == 0)
            response.Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return response;
    }

    private JsonObject GetAICreditsOptions(Dictionary<string, JsonElement>? providerOptions)
    {
        if (providerOptions is null || !providerOptions.TryGetValue(GetIdentifier(), out var options) || options.ValueKind != JsonValueKind.Object)
            return [];
        return JsonNode.Parse(options.GetRawText())?.AsObject() ?? [];
    }

    private static void ValidateAICreditsImageRequest(string model, string prompt)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("Model is required.", nameof(model));
        if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("Prompt is required.", nameof(prompt));
    }

    private sealed record AICreditsJsonResult(JsonElement Root, IDictionary<string, string> Headers);
}
