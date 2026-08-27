using AIHappey.Core.AI;
using AIHappey.Common.Extensions;
using AIHappey.Core.Extensions;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Impossibl;

public partial class ImpossiblProvider
{
    private static readonly JsonSerializerOptions ImpossiblImageJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("Prompt is required.", nameof(request));
        if (request.Files?.Any() == true || request.Mask is not null)
            throw new NotSupportedException("Impossibl does not support image edits.");

        var payload = CopyImpossiblOptions(request.ProviderOptions, "model", "prompt", "n", "size", "response_format");
        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;
        if (request.N is not null) payload["n"] = request.N;
        if (!string.IsNullOrWhiteSpace(request.Size)) payload["size"] = request.Size;
        payload["response_format"] = "b64_json";

        var warnings = new List<object>();
        if (request.Seed is not null) warnings.Add(new { type = "unsupported", feature = "seed" });
        if (!string.IsNullOrWhiteSpace(request.AspectRatio) && string.IsNullOrWhiteSpace(request.Size))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });

        ApplyAuthHeader();
        using var response = await _client.PostAsync("v1/images/generations",
            new StringContent(JsonSerializer.Serialize(payload, ImpossiblImageJson), Encoding.UTF8, MediaTypeNames.Application.Json),
            cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Impossibl image generation failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var format = payload.TryGetValue("output_format", out var output) ? output?.ToString() : null;
        var mimeType = format?.ToLowerInvariant() switch
        {
            "jpeg" or "jpg" => MediaTypeNames.Image.Jpeg,
            "webp" => "image/webp",
            _ => MediaTypeNames.Image.Png
        };
        var images = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray()
                .Where(item => item.TryGetProperty("b64_json", out var value) && value.ValueKind == JsonValueKind.String)
                .Select(item => item.GetProperty("b64_json").GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.ToDataUrl(mimeType))
                .ToList()
            : [];
        if (images.Count == 0) throw new InvalidOperationException("Impossibl image response did not contain images.");

        ImageUsageData? usage = null;
        if (root.TryGetProperty("usage", out var usageElement) && usageElement.ValueKind == JsonValueKind.Object)
        {
            usage = new ImageUsageData
            {
                InputTokens = ReadInt32(usageElement, "input_tokens"),
                OutputTokens = ReadInt32(usageElement, "output_tokens"),
                TotalTokens = ReadInt32(usageElement, "total_tokens")
            };
        }

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Usage = usage,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new HeaderResponseData
            {
                Timestamp = root.TryGetProperty("created", out var created) && created.TryGetInt64(out var seconds)
                    ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime : DateTime.UtcNow,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return _client.OpenAICompatibleImageGenerationRequestAsync(options, "v1/images/generations", cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        await foreach (var streamEvent in _client.OpenAICompatibleImageGenerationNonStreamingAsStreamAsync(
            options, "v1/images/generations", cancellationToken))
            yield return streamEvent;
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Impossibl does not support image edits.");

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Impossibl does not support image edits.");

    private Dictionary<string, object?> CopyImpossiblOptions(
        Dictionary<string, JsonElement>? providerOptions,
        params string[] reserved)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (providerOptions is null || !providerOptions.TryGetValue(GetIdentifier(), out var options)
            || options.ValueKind != JsonValueKind.Object)
            return result;

        var reservedNames = reserved.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var property in options.EnumerateObject())
            if (!reservedNames.Contains(property.Name)) result[property.Name] = property.Value.Clone();
        return result;
    }

    private static int? ReadInt32(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;

}
