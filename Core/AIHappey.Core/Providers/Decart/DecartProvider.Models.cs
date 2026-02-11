using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Decart;

public partial class DecartProvider
{

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {

        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return await Task.FromResult<IEnumerable<Model>>([]);

        ApplyAuthHeader();

        return await Task.FromResult(DecartModels);
    }

    public static IReadOnlyList<Model> DecartModels =>
 [
     new()
    {
        Id = "decart/lucy-pro-t2i",
        Name = "Lucy Pro T2I",
        Type = "image",
        Description = "Generate images from text.",
    },
    new()
    {
        Id = "decart/lucy-pro-i2i",
        Name = "Lucy Pro I2I",
        Type = "image",
        Description = "Edit and transform images.",
    },
    new()
    {
        Id = "decart/lucy-pro-t2v",
        Name = "Lucy Pro T2V",
        Type = "video",
        Description = "Generate videos from text.",
    },
    new()
    {
        Id = "decart/lucy-pro-i2v",
        Name = "Lucy Pro I2V",
        Type = "video",
        Description = "Animate images.",
    },
    new()
    {
        Id = "decart/lucy-pro-v2v",
        Name = "Lucy Pro V2V",
        Type = "video",
        Description = "Transform and edit videos.",
    },
    new()
    {
        Id = "decart/lucy-fast-v2v",
        Name = "Lucy Fast V2V",
        Type = "video",
        Description = "Transform and edit videos (fast).",
    },
    new()
    {
        Id = "decart/lucy-dev-i2v",
        Name = "Lucy Dev I2V",
        Type = "video",
        Description = "Animate images (faster).",
    },
    new()
    {
        Id = "decart/lucy-motion",
        Name = "Lucy Motion",
        Type = "video",
        Description = "Trajectory-based image animation.",
    },
    new()
    {
        Id = "decart/lucy-restyle-v2v",
        Name = "Lucy Restyle V2V",
        Type = "video",
        Description = "Long-form video restyling.",
    }
 ];

}
