using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.AIHubMix;

public partial class AIHubMixProvider
{

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);

        var options = new OpenAIImageGenerationRequest
        {
            Model = request.Model,
            Prompt = request.Prompt,
            N = request.N,
            Size = request.Size,
            ResponseFormat = "b64_json",
            AdditionalProperties = request.ProviderOptions
        };
        var result = await GenerateAIHubMixImagesAsync(options, cancellationToken);
        return new ImageResponse
        {
            Images = (result.Response.Data ?? []).Select(image => $"data:{ResolveAIHubMixImageMimeType(result.Response.OutputFormat)};base64,{image.B64Json}"),
            Warnings = BuildAIHubMixImageWarnings(request),
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root),
            Usage = result.Response.Usage is null ? null : new ImageUsageData
            {
                InputTokens = result.Response.Usage.InputTokens,
                OutputTokens = result.Response.Usage.OutputTokens,
                TotalTokens = result.Response.Usage.TotalTokens
            },
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
        return (await GenerateAIHubMixImagesAsync(options, cancellationToken)).Response;
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        ApplyAuthHeader();
        await foreach (var streamEvent in _client.OpenAICompatibleImageGenerationStreamingAsync(
            options, cancellationToken: cancellationToken))
            yield return streamEvent;
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("AIHubMix does not document an image editing endpoint.");
    }

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("AIHubMix does not document an image editing endpoint.");
    }

    private async Task<AIHubMixImagesResult> GenerateAIHubMixImagesAsync(
        OpenAIImageGenerationRequest options, CancellationToken cancellationToken)
    {
        // The public compatibility contract only exposes base64 image bodies.
        options.ResponseFormat = "b64_json";
        ApplyAuthHeader();
        var response = await _client.OpenAICompatibleImageGenerationRequestAsync(
            options, cancellationToken: cancellationToken);

        // GPT image models return base64. For compatible upstreams that ignore
        // response_format and return URLs, download immediately while valid.
        if (response.Data is not null)
        {
            foreach (var image in response.Data)
            {
#pragma warning disable CS0618
                if (string.IsNullOrWhiteSpace(image.B64Json) && Uri.TryCreate(image.Url, UriKind.Absolute, out var uri))
                {
                    using var download = await _client.GetAsync(uri, cancellationToken);
                    var bytes = await download.Content.ReadAsByteArrayAsync(cancellationToken);
                    if (!download.IsSuccessStatusCode)
                        throw new InvalidOperationException($"AIHubMix image download failed ({(int)download.StatusCode}).");
                    image.B64Json = Convert.ToBase64String(bytes);
                    image.Url = null;
                }
#pragma warning restore CS0618
            }
        }

        var root = JsonSerializer.SerializeToElement(response);
        return new AIHubMixImagesResult(response, root, []);
    }

    private static List<object> BuildAIHubMixImageWarnings(ImageRequest request)
    {
        List<object> warnings = [];
        if (!string.IsNullOrWhiteSpace(request.AspectRatio)) warnings.Add(new { type = "unsupported", feature = "aspectRatio" });
        if (request.Seed is not null) warnings.Add(new { type = "unsupported", feature = "seed" });
        if (request.Files?.Any() == true || request.Mask is not null) warnings.Add(new { type = "unsupported", feature = "image editing" });
        return warnings;
    }

    private static string ResolveAIHubMixImageMimeType(string? format) => format?.ToLowerInvariant() switch
    {
        "jpeg" or "jpg" => "image/jpeg", "webp" => "image/webp", _ => "image/png"
    };

    private sealed record AIHubMixImagesResult(OpenAIImagesResponse Response, JsonElement Root, Dictionary<string, string> Headers);
}
