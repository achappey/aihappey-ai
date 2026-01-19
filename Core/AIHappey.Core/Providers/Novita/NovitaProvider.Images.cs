using AIHappey.Core.AI;
using AIHappey.Common.Model;

namespace AIHappey.Core.Providers.Novita;

public partial class NovitaProvider : IModelProvider
{

    public async Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        if (IsSeedream45Model(imageRequest.Model))
            return await ImageRequestSeedream45(imageRequest, cancellationToken);

        if (IsRemoveModel(imageRequest.Model))
            return await ImageRequestRemove(imageRequest, cancellationToken);

        if (IsQwenImageTxt2ImgModel(imageRequest.Model))
            return await ImageRequestQwenImageTxt2Img(imageRequest, cancellationToken);

        if (IsCleanupModel(imageRequest.Model))
            return await ImageRequestCleanup(imageRequest, cancellationToken);

        if (IsHunyuanImage3Model(imageRequest.Model))
            return await ImageRequestHunyuanImage3(imageRequest, cancellationToken);

        if (IsFlux2ProModel(imageRequest.Model))
            return await ImageRequestFlux2Pro(imageRequest, cancellationToken);

        throw new NotImplementedException();
    }
}
