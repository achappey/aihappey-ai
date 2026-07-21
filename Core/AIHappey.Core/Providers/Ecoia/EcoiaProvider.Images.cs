using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Ecoia;

public partial class EcoiaProvider
{
    private static readonly JsonSerializerOptions EcoiaImageJsonOptions = new(JsonSerializerDefaults.Web)
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

        ApplyAuthHeader();

        var warnings = new List<object>();
        var payload = BuildImagePayload(request, warnings);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/image")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, EcoiaImageJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Ecoia image request failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        ThrowIfEcoiaFailure(root);

        var images = ExtractImages(root);
        if (images.Count == 0)
            throw new InvalidOperationException("Ecoia image response did not contain usable images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Usage = ExtractUsage(root),
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
        OpenAIImageGenerationRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var result = await ImageRequest(options.ToImageRequest(
            options.ResolveOpenAIImageGenerationModel(),
            GetIdentifier()), cancellationToken);
        return result.ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var result = await ImageRequest(options.ToImageRequest(
            options.ResolveOpenAIImageGenerationModel(),
            GetIdentifier()), cancellationToken);

        foreach (var streamEvent in result.ToOpenAIImageGenerationCompletedEvents(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return streamEvent;
        }
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        var request = await options.ToImageRequest(
            options.ResolveOpenAIImageEditModel(),
            GetIdentifier(),
            cancellationToken);
        var result = await ImageRequest(request, cancellationToken);
        return result.ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        var request = await options.ToImageRequest(
            options.ResolveOpenAIImageEditModel(),
            GetIdentifier(),
            cancellationToken);
        var result = await ImageRequest(request, cancellationToken);

        foreach (var streamEvent in result.ToOpenAIImageEditCompletedEvents(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return streamEvent;
        }
    }

    public async Task<OpenAIImagesResponse> OpenAIImageVariationRequestAsync(
        OpenAIImageVariationRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageVariationRequest();
        var request = await options.ToImageRequest(
            options.ResolveOpenAIImageVariationModel(),
            GetIdentifier(),
            cancellationToken);
        var result = await ImageRequest(request, cancellationToken);
        return result.ToOpenAIImagesResponse(options);
    }

    private Dictionary<string, object?> BuildImagePayload(ImageRequest request, List<object> warnings)
    {
        var content = new List<object>
        {
            new { type = "text", text = request.Prompt }
        };
        var inputFiles = request.Files?.Where(file => file is not null).ToList() ?? [];
        foreach (var file in inputFiles)
        {
            content.Add(new
            {
                type = "image_url",
                image_url = new { url = ToDataUrl(file) }
            });
        }

        if (request.Mask is not null)
        {
            content.Add(new
            {
                type = "image_url",
                image_url = new { url = ToDataUrl(request.Mask) }
            });
            warnings.Add(new
            {
                type = "compatibility",
                feature = "mask",
                details = "Ecoia has no documented mask field; the mask was included as an additional image input."
            });
        }

        if (request.N is > 1)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "n",
                details = "Ecoia does not document a generated image count; its returned image set is used as-is."
            });
        }
        if (request.Seed.HasValue)
            warnings.Add(new { type = "unsupported", feature = "seed" });

        var settings = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(request.Size))
            settings["size"] = request.Size.Trim();
        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            settings["aspectRatio"] = request.AspectRatio.Trim();

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = request.Model.Trim(),
            ["messages"] = new[] { new { role = "user", content } },
            ["settings"] = settings
        };

        MergeProviderOptions(payload, settings, request.GetProviderMetadata<JsonElement>(GetIdentifier()));
        return payload;
    }

    private static void MergeProviderOptions(
        Dictionary<string, object?> payload,
        Dictionary<string, object?> settings,
        JsonElement providerOptions)
    {
        if (providerOptions.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in providerOptions.EnumerateObject())
        {
            if (property.NameEquals("settings") && property.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var setting in property.Value.EnumerateObject())
                    settings[setting.Name] = setting.Value.Clone();
                continue;
            }

            if (property.NameEquals("model") || property.NameEquals("messages"))
                continue;

            payload[property.Name] = property.Value.Clone();
        }
    }

    private static string ToDataUrl(ImageFile file)
    {
        if (string.IsNullOrWhiteSpace(file.Data))
            throw new ArgumentException("Input image data cannot be empty.", nameof(file));
        if (file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || file.Data.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || file.Data.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return file.Data;
        return file.Data.ToDataUrl(string.IsNullOrWhiteSpace(file.MediaType) ? MediaTypeNames.Image.Png : file.MediaType);
    }

    private static void ThrowIfEcoiaFailure(JsonElement root)
    {
        if (!root.TryGetProperty("success", out var success) || success.ValueKind is not JsonValueKind.False)
            return;

        var message = GetString(root, "error")
            ?? GetString(root, "message")
            ?? "Ecoia image request failed.";
        throw new InvalidOperationException(message);
    }

    private static List<string> ExtractImages(JsonElement root)
    {
        List<string> images = [];
        if (!root.TryGetProperty("images", out var imageArray) || imageArray.ValueKind != JsonValueKind.Array)
            return images;

        foreach (var image in imageArray.EnumerateArray())
        {
            if (image.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(image.GetString()))
                continue;

            var value = image.GetString()!.Trim();
            images.Add(value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    ? value
                    : value.ToDataUrl(MediaTypeNames.Image.Png));
        }

        return images;
    }

    private static ImageUsageData? ExtractUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;

        var input = GetInt32(usage, "input_tokens", "inputTokens");
        var output = GetInt32(usage, "output_tokens", "outputTokens");
        var total = GetInt32(usage, "total_tokens", "totalTokens") ?? (input.HasValue || output.HasValue ? (input ?? 0) + (output ?? 0) : null);
        return input.HasValue || output.HasValue || total.HasValue
            ? new ImageUsageData { InputTokens = input, OutputTokens = output, TotalTokens = total }
            : null;
    }

    private static string? GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? GetInt32(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var property))
                continue;
            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number))
                return number;
            if (property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out number))
                return number;
        }

        return null;
    }
}
