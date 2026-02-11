using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Euqai;

public partial class EuqaiProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
          if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return await Task.FromResult<IEnumerable<Model>>([]);
            
        ApplyAuthHeader();

        return EuqaiLanguageModels;
    }

    public static IReadOnlyList<Model> EuqaiLanguageModels =>
    [
        new()
        {
            Id = "euqai/euqai-fusion-v1",
            Name = "Euqai Fusion v1",
            Type = "language",
            OwnedBy = nameof(Euqai),
            Pricing = new() { Input = 0.8m, Output = 4.0m }
        },
        new()
        {
            Id = "euqai/euqai-fusion-code-v1",
            Name = "Euqai Fusion Code v1",
            Type = "language",
            OwnedBy = nameof(Euqai),
            Pricing = new() { Input = 1.0m, Output = 3.8m }
        },
        new()
        {
            Id = "euqai/qwen3-235b",
            Name = "Qwen 3 235B",
            Type = "language",
            OwnedBy = nameof(Euqai),
            Pricing = new() { Input = 1.0m, Output = 3.0m }
        },
        new()
        {
            Id = "euqai/mistral-small-24b",
            Name = "Mistral Small 24b",
            Type = "language",
            OwnedBy = nameof(Euqai),
            Pricing = new() { Input = 0.2m, Output = 0.5m }
        },
        new()
        {
            Id = "euqai/qwen3-30b",
            Name = "Qwen 3 30B",
            Type = "language",
            OwnedBy =nameof(Euqai),
            Pricing = new() { Input = 0.8m, Output = 2.4m }
        },
        new()
        {
            Id = "euqai/flux-schnell",
            Name = "FLUX.1 Schnell",
            Type = "image",
            OwnedBy = nameof(Euqai),
        }
    ];

}