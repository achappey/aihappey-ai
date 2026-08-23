using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.Novita;

public partial class NovitaProvider
{

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
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
        var response = await ImageRequest(
            options.ToImageRequest(options.Model, GetIdentifier()),
            cancellationToken);
        foreach (var streamEvent in response.ToOpenAIImageGenerationCompletedEvents(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return streamEvent;
        }
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        var request = await options.ToImageRequest(options.Model, GetIdentifier(), cancellationToken);
        var response = await ImageRequest(request, cancellationToken);
        return response.ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        var request = await options.ToImageRequest(options.Model, GetIdentifier(), cancellationToken);
        var response = await ImageRequest(request, cancellationToken);
        foreach (var streamEvent in response.ToOpenAIImageEditCompletedEvents(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return streamEvent;
        }
    }

    public async Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        if (IsSeedream45Model(imageRequest.Model))
            return await ImageRequestSeedream45(imageRequest, cancellationToken);

        if (IsRemoveModel(imageRequest.Model))
            return await ImageRequestRemove(imageRequest, cancellationToken);

        if (IsQwenImageTxt2ImgModel(imageRequest.Model))
            return await ImageRequestQwenImageTxt2Img(imageRequest, cancellationToken);

        if (IsQwenImageEditModel(imageRequest.Model))
            return await ImageRequestQwenImageEdit(imageRequest, cancellationToken);

        if (IsCleanupModel(imageRequest.Model))
            return await ImageRequestCleanup(imageRequest, cancellationToken);

        if (IsHunyuanImage3Model(imageRequest.Model))
            return await ImageRequestHunyuanImage3(imageRequest, cancellationToken);

        if (IsFlux2ProModel(imageRequest.Model))
            return await ImageRequestFlux2Pro(imageRequest, cancellationToken);

        throw new NotImplementedException();
    }
}
