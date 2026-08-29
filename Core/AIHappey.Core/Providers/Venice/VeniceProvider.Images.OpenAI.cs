using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.MCP.Media;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.Venice;

public partial class VeniceProvider
{
    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
        OpenAIImageGenerationRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        if (string.IsNullOrWhiteSpace(options.Model))
            throw new ArgumentException("'model' is a required field");

        var request = options.ToImageRequest(options.Model, GetIdentifier());
        request.ProviderOptions = CreateVeniceImageProviderOptions(
            options.AdditionalProperties,
            ("format", NormalizeVeniceImageFormat(options.OutputFormat)),
            ("quality", options.Quality),
            ("style_preset", options.Style));

        // Venice resolution-tier models use values such as 1K/2K/4K rather than pixels.
        if (IsVeniceResolutionTier(options.Size))
        {
            AddVeniceImageProviderOption(request, "resolution", options.Size!.ToUpperInvariant());
            request.Size = null;
        }

        var response = await ImageRequest(request, cancellationToken);
        return response.ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Venice image generation is synchronous. Adapt the completed response to OpenAI events.
        options.ValidateOpenAIImageGenerationRequest();
        if (string.IsNullOrWhiteSpace(options.Model))
            throw new ArgumentException("'model' is a required field");

        var request = options.ToImageRequest(options.Model, GetIdentifier());
        request.ProviderOptions = CreateVeniceImageProviderOptions(
            options.AdditionalProperties,
            ("format", NormalizeVeniceImageFormat(options.OutputFormat)),
            ("quality", options.Quality),
            ("style_preset", options.Style));

        if (IsVeniceResolutionTier(options.Size))
        {
            AddVeniceImageProviderOption(request, "resolution", options.Size!.ToUpperInvariant());
            request.Size = null;
        }

        var response = await ImageRequest(request, cancellationToken);
        foreach (var streamEvent in response.ToOpenAIImageGenerationCompletedEvents(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return streamEvent;
        }
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken = default)
    {
        ValidateVeniceOpenAIImageEditRequest(options);

        var request = await options.ToImageRequest(options.Model, GetIdentifier(), cancellationToken);
        request.ProviderOptions = CreateVeniceImageProviderOptions(
            options.AdditionalProperties,
            ("output_format", NormalizeVeniceImageFormat(options.OutputFormat)),
            ("quality", options.Quality));

        if (IsVeniceResolutionTier(options.Size))
        {
            AddVeniceImageProviderOption(request, "resolution", options.Size!.ToUpperInvariant());
            request.Size = null;
        }

        var response = await ImageRequest(request, cancellationToken);
        return response.ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Venice edits are synchronous. Adapt each resulting image to a completed event.
        ValidateVeniceOpenAIImageEditRequest(options);

        var request = await options.ToImageRequest(options.Model, GetIdentifier(), cancellationToken);
        request.ProviderOptions = CreateVeniceImageProviderOptions(
            options.AdditionalProperties,
            ("output_format", NormalizeVeniceImageFormat(options.OutputFormat)),
            ("quality", options.Quality));

        if (IsVeniceResolutionTier(options.Size))
        {
            AddVeniceImageProviderOption(request, "resolution", options.Size!.ToUpperInvariant());
            request.Size = null;
        }

        var response = await ImageRequest(request, cancellationToken);
        foreach (var streamEvent in response.ToOpenAIImageEditCompletedEvents(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return streamEvent;
        }
    }

    private static void ValidateVeniceOpenAIImageEditRequest(OpenAIImageEditRequest options)
    {
        options.ValidateOpenAIImageEditRequest();
        if (string.IsNullOrWhiteSpace(options.Model))
            throw new ArgumentException("'model' is a required field");
        if (options.Mask is not null || options.MaskFile is not null)
            throw new NotSupportedException("Venice image editing does not support OpenAI mask inputs.");

#pragma warning disable CS0618
        if (options.Images?.Any(image => !string.IsNullOrWhiteSpace(image.FileId)) == true)
            throw new NotSupportedException("Venice image editing does not support OpenAI file_id references. Use an upload, base64/data URL, or HTTP URL.");
#pragma warning restore CS0618
    }

    private static Dictionary<string, JsonElement>? CreateVeniceImageProviderOptions(
        Dictionary<string, JsonElement>? additionalProperties,
        params (string Name, object? Value)[] primaryProperties)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in additionalProperties ?? [])
            values[name] = JsonSerializer.Deserialize<object?>(value.GetRawText(), JsonSerializerOptions.Web);

        foreach (var (name, value) in primaryProperties)
        {
            if (value is not null && (value is not string text || !string.IsNullOrWhiteSpace(text)))
                values[name] = value;
        }

        return values.Count == 0
            ? null
            : new Dictionary<string, JsonElement>
            {
                ["venice"] = JsonSerializer.SerializeToElement(values, JsonSerializerOptions.Web)
            };
    }

    private static void AddVeniceImageProviderOption(ImageRequest request, string name, object value)
    {
        var metadata = request.GetProviderMetadata<JsonElement>("venice");
        var payload = metadata.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(metadata.GetRawText()) as JsonObject ?? []
            : [];
        payload[name] = JsonSerializer.SerializeToNode(value, JsonSerializerOptions.Web);
        request.ProviderOptions = new Dictionary<string, JsonElement>
        {
            ["venice"] = JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web)
        };
    }

    private static bool IsVeniceResolutionTier(string? size)
        => size?.Trim().ToUpperInvariant() is "1K" or "2K" or "4K";

    private static string? NormalizeVeniceImageFormat(string? format)
        => format?.Trim().ToLowerInvariant() switch
        {
            "jpg" => "jpeg",
            { Length: > 0 } normalized => normalized,
            _ => null
        };

}
