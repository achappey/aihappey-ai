using System.Runtime.CompilerServices;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Recraft;

public partial class RecraftProvider
{

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
        OpenAIImageGenerationRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        ValidateRecraftImageCount(options.N);

        var response = await ImageRequest(
            options.ToImageRequest(options.Model, GetIdentifier()),
            cancellationToken);

        return response.ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        ValidateRecraftImageCount(options.N);

        var response = await ImageRequest(
            options.ToImageRequest(options.Model, GetIdentifier()),
            cancellationToken);

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
        options.ValidateOpenAIImageEditRequest();
        ValidateRecraftImageCount(options.N);

        var request = await options.ToImageRequest(options.Model, GetIdentifier(), cancellationToken);
        ValidateRecraftEditRequest(request);
        ApplyAuthHeader();
        var response = await SendRecraftEditRequestAsync(request, request.Mask is not null, cancellationToken);

        return response.ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        ValidateRecraftImageCount(options.N);

        var request = await options.ToImageRequest(options.Model, GetIdentifier(), cancellationToken);
        ValidateRecraftEditRequest(request);
        ApplyAuthHeader();
        var response = await SendRecraftEditRequestAsync(request, request.Mask is not null, cancellationToken);

        foreach (var streamEvent in response.ToOpenAIImageEditCompletedEvents(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return streamEvent;
        }
    }

    private static void ValidateRecraftImageCount(int? count)
    {
        if (count is < 1 or > 6)
            throw new ArgumentOutOfRangeException(nameof(count), count, "Recraft image count must be between 1 and 6.");
    }

    private static void ValidateRecraftEditRequest(AIHappey.Vercel.Models.ImageRequest request)
    {
        if (request.Files?.Count() != 1)
            throw new ArgumentException("Recraft image edits require exactly one input image.", nameof(request));

        if (request.Mask is null)
        {
            var options = GetRecraftOptions(request);
            if (options is null
                || !options.Value.TryGetProperty("strength", out var strength)
                || strength.ValueKind != System.Text.Json.JsonValueKind.Number
                || !strength.TryGetDouble(out var value)
                || value is < 0 or > 1)
                throw new ArgumentException("Unmasked Recraft image edits require a numeric 'strength' between 0 and 1.", nameof(request));
        }
    }
}

