using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.ElectronHub;

public partial class ElectronHubProvider
{
    private const string ElectronHubImageGenerationsEndpoint = "v1/images/generations";
    private const string ElectronHubImageEditsEndpoint = "v1/images/edits";

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);

        var payload = ElectronHubRawOptions(request.ProviderOptions);
        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;
        payload["response_format"] = "b64_json";
        if (request.N.HasValue) payload["n"] = request.N.Value;
        if (!string.IsNullOrWhiteSpace(request.Size)) payload["size"] = request.Size;

        ApplyAuthHeader();
        using var response = await _client.PostAsync(ElectronHubImageGenerationsEndpoint,
            new StringContent(payload.ToJsonString(), Encoding.UTF8, MediaTypeNames.Application.Json), cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"ElectronHub image generation failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var images = await ElectronHubReadImagesAsync(root, cancellationToken);
        if (images.Count == 0) throw new InvalidOperationException("ElectronHub returned no generated images.");

        var warnings = new List<object>();
        if (request.Files?.Any() == true) warnings.Add(new { type = "unsupported", feature = "files" });
        if (request.Mask is not null) warnings.Add(new { type = "unsupported", feature = "mask" });
        if (request.Seed.HasValue) warnings.Add(new { type = "unsupported", feature = "seed" });
        if (!string.IsNullOrWhiteSpace(request.AspectRatio)) warnings.Add(new { type = "unsupported", feature = "aspectRatio" });

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new HeaderResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ApplyAuthHeader();
        return _client.OpenAICompatibleImageGenerationRequestAsync(options, ElectronHubImageGenerationsEndpoint, cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        await foreach (var item in _client.OpenAICompatibleImageGenerationNonStreamingAsStreamAsync(
            options, ElectronHubImageGenerationsEndpoint, cancellationToken)) yield return item;
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ApplyAuthHeader();
        return _client.OpenAICompatibleImageEditRequestAsync(options, ElectronHubImageEditsEndpoint, cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        await foreach (var item in _client.OpenAICompatibleImageEditNonStreamingAsStreamAsync(
            options, ElectronHubImageEditsEndpoint, cancellationToken)) yield return item;
    }

    private static JsonObject ElectronHubRawOptions(IReadOnlyDictionary<string, JsonElement>? options)
    {
        if (options is null || !options.TryGetValue("electronhub", out var value) || value.ValueKind != JsonValueKind.Object)
            return [];
        return JsonNode.Parse(value.GetRawText())?.AsObject() ?? [];
    }

    private async Task<List<string>> ElectronHubReadImagesAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var result = new List<string>();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return result;
        foreach (var item in data.EnumerateArray())
        {
            if (item.TryGetProperty("b64_json", out var b64) && b64.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(b64.GetString()))
            {
                result.Add($"data:image/png;base64,{b64.GetString()}");
                continue;
            }
            if (!item.TryGetProperty("url", out var urlElement) || !Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var uri)) continue;
            using var download = await _client.GetAsync(uri, cancellationToken);
            var bytes = await download.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!download.IsSuccessStatusCode) throw new InvalidOperationException($"ElectronHub image download failed ({(int)download.StatusCode}).");
            result.Add($"data:{download.Content.Headers.ContentType?.MediaType ?? "image/png"};base64,{Convert.ToBase64String(bytes)}");
        }
        return result;
    }
}
