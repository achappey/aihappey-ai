using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Maxlayer;

public partial class MaxlayerProvider
{
    private static readonly JsonSerializerOptions MaxlayerImageJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> ImageRequest(
        ImageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateMaxlayerImageRequest(request.Model, request.Prompt, nameof(request));

        var payload = CreateMaxlayerImagePayload(request.ProviderOptions);
        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;
        payload["response_format"] = "b64_json";
        SetMaxlayerImageValue(payload, "n", request.N);
        SetMaxlayerImageValue(payload, "size", request.Size);
        SetMaxlayerImageValue(payload, "aspect_ratio", request.AspectRatio);
        SetMaxlayerImageValue(payload, "seed", request.Seed);

        var result = await GenerateMaxlayerImagesAsync(payload, cancellationToken);
        var mediaType = ResolveMaxlayerImageMediaType(payload, result.Root);
        var warnings = new List<object>();
        if (request.Files?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "files", details = "Maxlayer image generation does not document image inputs." });
        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask", details = "Maxlayer image generation does not document masks." });

        return new ImageResponse
        {
            Images = result.Images.Select(image => $"data:{mediaType};base64,{image.B64Json}"),
            Warnings = warnings,
            Usage = ToMaxlayerImageUsage(result.Response.Usage),
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root),
            Response = new HeaderResponseData
            {
                Timestamp = FromMaxlayerCreated(result.Response.Created),
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
        options.ValidateOpenAIImageGenerationRequest();

        var payload = new JsonObject();
        MergeMaxlayerImageFields(payload, options.AdditionalProperties);
        payload["model"] = options.Model;
        payload["prompt"] = options.Prompt;
        payload["response_format"] = "b64_json";
        SetMaxlayerImageValue(payload, "background", options.Background);
        SetMaxlayerImageValue(payload, "moderation", options.Moderation);
        SetMaxlayerImageValue(payload, "n", options.N);
        SetMaxlayerImageValue(payload, "output_compression", options.OutputCompression);
        SetMaxlayerImageValue(payload, "output_format", options.OutputFormat);
        SetMaxlayerImageValue(payload, "partial_images", options.PartialImages);
        SetMaxlayerImageValue(payload, "quality", options.Quality);
        SetMaxlayerImageValue(payload, "size", options.Size);
        SetMaxlayerImageValue(payload, "style", options.Style);
        SetMaxlayerImageValue(payload, "user", options.User);

        var result = await GenerateMaxlayerImagesAsync(payload, cancellationToken);
        return result.Response;
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
                Size = response.Size
            };
        }
    }

    private async Task<MaxlayerImagesResult> GenerateMaxlayerImagesAsync(
        JsonObject payload,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/images")
        {
            Content = new StringContent(
                payload.ToJsonString(MaxlayerImageJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"Maxlayer image generation failed ({(int)response.StatusCode})."
                : $"Maxlayer image generation failed ({(int)response.StatusCode}): {raw}");

        JsonElement root;
        OpenAIImagesResponse parsed;
        try
        {
            using var document = JsonDocument.Parse(raw);
            root = document.RootElement.Clone();
            parsed = JsonSerializer.Deserialize<OpenAIImagesResponse>(raw, MaxlayerImageJsonOptions)
                ?? throw new JsonException("The response body was empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Maxlayer image generation returned invalid JSON.", exception);
        }

        var images = parsed.Data?
            .Where(image => !string.IsNullOrWhiteSpace(image.B64Json))
            .ToList() ?? [];
        if (images.Count == 0)
            throw new InvalidOperationException("Maxlayer image response did not contain data entries with b64_json.");

        parsed.Data = images;
        if (parsed.Created <= 0)
            parsed.Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return new MaxlayerImagesResult(root, response.GetHeaders(), parsed, images);
    }

    private JsonObject CreateMaxlayerImagePayload(Dictionary<string, JsonElement>? providerOptions)
    {
        var payload = new JsonObject();
        if (providerOptions is not null
            && providerOptions.TryGetValue(GetIdentifier(), out var options)
            && options.ValueKind == JsonValueKind.Object)
        {
            foreach (var option in options.EnumerateObject())
                payload[option.Name] = JsonNode.Parse(option.Value.GetRawText());
        }
        return payload;
    }

    private static void MergeMaxlayerImageFields(
        JsonObject payload,
        Dictionary<string, JsonElement>? fields)
    {
        foreach (var field in fields ?? [])
            payload[field.Key] = JsonNode.Parse(field.Value.GetRawText());
    }

    private static void SetMaxlayerImageValue(JsonObject payload, string name, object? value)
    {
        if (value is null || value is string text && string.IsNullOrWhiteSpace(text))
            return;
        payload[name] = JsonSerializer.SerializeToNode(value, MaxlayerImageJsonOptions);
    }

    private static void ValidateMaxlayerImageRequest(string? model, string? prompt, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", parameterName);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("Prompt is required.", parameterName);
    }

    private static string ResolveMaxlayerImageMediaType(JsonObject payload, JsonElement root)
    {
        var format = GetMaxlayerImageString(root, "output_format")
            ?? GetMaxlayerImageString(root, "format")
            ?? payload["output_format"]?.GetValue<string>();
        return format?.Trim().ToLowerInvariant() switch
        {
            "jpg" or "jpeg" => "image/jpeg",
            "webp" => "image/webp",
            "gif" => "image/gif",
            _ => "image/png"
        };
    }

    private static string? GetMaxlayerImageString(JsonElement root, string property)
        => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private static DateTime FromMaxlayerCreated(long created)
    {
        if (created <= 0)
            return DateTime.UtcNow;
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(created).UtcDateTime;
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTime.UtcNow;
        }
    }

    private static ImageUsageData? ToMaxlayerImageUsage(OpenAIImageUsage? usage)
        => usage is null ? null : new ImageUsageData
        {
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens,
            TotalTokens = usage.TotalTokens
        };

    private sealed record MaxlayerImagesResult(
        JsonElement Root,
        Dictionary<string, string> Headers,
        OpenAIImagesResponse Response,
        List<OpenAIImageData> Images);
}
