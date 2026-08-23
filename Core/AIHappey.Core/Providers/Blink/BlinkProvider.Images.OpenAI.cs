using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Blink;

public partial class BlinkProvider
{
    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
        OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var request = options.ToImageRequest(options.Model, GetIdentifier());
        ApplyBlinkOutputOptions(request, options.OutputFormat, options.OutputCompression);
        var response = await ImageRequest(request, cancellationToken);
        return response.ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var request = options.ToImageRequest(options.Model, GetIdentifier());
        ApplyBlinkOutputOptions(request, options.OutputFormat, options.OutputCompression);
        var response = await ImageRequest(request, cancellationToken);
        foreach (var streamEvent in response.ToOpenAIImageGenerationCompletedEvents(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return streamEvent;
        }
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(
        OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        var request = await options.ToImageRequest(options.Model, GetIdentifier(), cancellationToken);
        ApplyBlinkOutputOptions(request, options.OutputFormat, options.OutputCompression);
        var response = await ImageRequest(request, cancellationToken);
        return response.ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        var request = await options.ToImageRequest(options.Model, GetIdentifier(), cancellationToken);
        ApplyBlinkOutputOptions(request, options.OutputFormat, options.OutputCompression);
        var response = await ImageRequest(request, cancellationToken);
        foreach (var streamEvent in response.ToOpenAIImageEditCompletedEvents(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return streamEvent;
        }
    }

    private void ApplyBlinkOutputOptions(ImageRequest request, string? outputFormat, int? outputCompression)
    {
        var values = new Dictionary<string, object?>();
        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var existing) == true
            && existing.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in existing.EnumerateObject())
                values[property.Name] = property.Value.Clone();
        }

        if (!string.IsNullOrWhiteSpace(outputFormat))
            values["output_format"] = outputFormat;
        if (outputCompression is not null)
            values["output_compression"] = outputCompression.Value;

        if (values.Count > 0)
        {
            request.ProviderOptions ??= [];
            request.ProviderOptions[GetIdentifier()] = JsonSerializer.SerializeToElement(values, JsonSerializerOptions.Web);
        }
    }
}
