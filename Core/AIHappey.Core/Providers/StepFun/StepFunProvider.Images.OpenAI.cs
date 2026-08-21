using System.Runtime.CompilerServices;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.StepFun;

public partial class StepFunProvider
{

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
        OpenAIImageGenerationRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        ValidateStepFunOpenAIImageCount(options.N);

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
        ValidateStepFunOpenAIImageCount(options.N);

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
        ValidateStepFunOpenAIImageCount(options.N);

        var request = await options.ToImageRequest(options.Model, GetIdentifier(), cancellationToken);
        ValidateStepFunOpenAIImageEdit(request);
        var response = await ImageRequest(request, cancellationToken);

        return response.ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        ValidateStepFunOpenAIImageCount(options.N);

        var request = await options.ToImageRequest(options.Model, GetIdentifier(), cancellationToken);
        ValidateStepFunOpenAIImageEdit(request);
        var response = await ImageRequest(request, cancellationToken);

        foreach (var streamEvent in response.ToOpenAIImageEditCompletedEvents(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return streamEvent;
        }
    }

    private static void ValidateStepFunOpenAIImageCount(int? count)
    {
        if (count is not null and not 1)
            throw new ArgumentOutOfRangeException(nameof(count), count, "StepFun currently supports exactly one image per request.");
    }

    private static void ValidateStepFunOpenAIImageEdit(AIHappey.Vercel.Models.ImageRequest request)
    {
        if (request.Files?.Count() != 1)
            throw new ArgumentException("StepFun image edits require exactly one input image.", nameof(request));

        if (request.Mask is not null)
            throw new NotSupportedException("StepFun image edits do not support masks.");
    }
}
