using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Deepbricks;

public partial class DeepbricksProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return await Task.FromResult<IEnumerable<Model>>([]);

        ApplyAuthHeader();

        return DeepbricksModels;
    }

    public static IReadOnlyList<Model> DeepbricksModels =>
    [
        new()
        {
            Id = "deepbricks/Claude-Sonnet-4.5",
            Name = "Claude Sonnet 4.5",
            Type = "language",
            OwnedBy = nameof(Deepbricks),
            Pricing = new() { Input = 1.50m, Output = 7.50m }
        },
        new()
        {
            Id = "deepbricks/Claude-HaiKu-4.5",
            Name = "Claude HaiKu 4.5",
            Type = "language",
            OwnedBy = nameof(Deepbricks),
            Pricing = new() { Input = 0.50m, Output = 2.50m }
        },
        new()
        {
            Id = "deepbricks/GPT-5.1",
            Name = "GPT-5.1",
            Type = "language",
            OwnedBy = nameof(Deepbricks),
            Pricing = new() { Input = 0.65m, Output = 5.00m }
        },
        new()
        {
            Id = "deepbricks/GPT-5-Chat",
            Name = "GPT-5 Chat",
            Type = "language",
            OwnedBy = nameof(Deepbricks),
            Pricing = new() { Input = 0.65m, Output = 5.00m }
        },
        new()
        {
            Id = "deepbricks/gemini-2.5-pro",
            Name = "gemini-2.5-pro",
            Type = "language",
            OwnedBy = nameof(Deepbricks),
            Pricing = new() { Input = 0.50m, Output = 4.00m }
        },
        new()
        {
            Id = "deepbricks/gemini-2.5-flash",
            Name = "gemini-2.5-flash",
            Type = "language",
            OwnedBy = nameof(Deepbricks),
            Pricing = new() { Input = 0.10m, Output = 1.00m }
        },
        new()
        {
            Id = "deepbricks/GPT-4o-2024-08-06",
            Name = "GPT-4o-2024-08-06",
            Type = "language",
            OwnedBy = nameof(Deepbricks),
            Pricing = new() { Input = 1.00m, Output = 4.00m }
        },
        new()
        {
            Id = "deepbricks/GPT-4o",
            Name = "GPT-4o",
            Type = "language",
            OwnedBy = nameof(Deepbricks),
            Pricing = new() { Input = 1.00m, Output = 4.00m }
        },
        new()
        {
            Id = "deepbricks/o1-mini",
            Name = "o1-mini",
            Type = "language",
            OwnedBy = nameof(Deepbricks),
            Pricing = new() { Input = 0.55m, Output = 2.20m }
        },
        new()
        {
            Id = "deepbricks/o3-mini",
            Name = "o3-mini",
            Type = "language",
            OwnedBy = nameof(Deepbricks),
            Pricing = new() { Input = 0.55m, Output = 2.20m }
        },
        new()
        {
            Id = "deepbricks/o4-mini",
            Name = "o4-mini",
            Type = "language",
            OwnedBy = nameof(Deepbricks),
            Pricing = new() { Input = 0.55m, Output = 2.20m }
        },
        new()
        {
            Id = "deepbricks/GPT-4-turbo",
            Name = "GPT-4-turbo",
            Type = "language",
            OwnedBy = nameof(Deepbricks),
            Pricing = new() { Input = 4.00m, Output = 10.00m }
        },
        new()
        {
            Id = "deepbricks/GPT-4.1",
            Name = "GPT-4.1",
            Type = "language",
            OwnedBy = nameof(Deepbricks),
            Pricing = new() { Input = 1.00m, Output = 4.00m }
        },
        new()
        {
            Id = "deepbricks/o1",
            Name = "o1",
            Type = "language",
            OwnedBy = nameof(Deepbricks),
            Pricing = new() { Input = 7.50m, Output = 30.00m }
        },
        new()
        {
            Id = "deepbricks/Claude-3.5-Sonnet",
            Name = "Claude 3.5 Sonnet",
            Type = "language",
            OwnedBy = nameof(Deepbricks),
            Pricing = new() { Input = 1.50m, Output = 7.50m }
        },
        new()
        {
            Id = "deepbricks/GPT-4.1-mini",
            Name = "GPT-4.1-mini",
            Type = "language",
            OwnedBy = nameof(Deepbricks),
            Pricing = new() { Input = 0.125m, Output = 0.50m }
        },
        new()
        {
            Id = "deepbricks/GPT-3.5-turbo",
            Name = "GPT-3.5-turbo",
            Type = "language",
            OwnedBy = nameof(Deepbricks),
            Pricing = new() { Input = 0.20m, Output = 0.60m }
        },
        new()
        {
            Id = "deepbricks/GPT-3.5-turbo-instruct",
            Name = "GPT-3.5-turbo-instruct",
            Type = "language",
            OwnedBy = nameof(Deepbricks),
            Pricing = new() { Input = 0.60m, Output = 0.80m }
        },
        new()
        {
            Id = "deepbricks/dall-e-3",
            Name = "dall-e-3",
            Type = "image",
            OwnedBy = nameof(Deepbricks)
        }
    ];
}
