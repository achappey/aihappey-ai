using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.CloudRift;

public sealed partial class CloudRiftProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        
        return await Task.FromResult<IEnumerable<Model>>(CloudRiftLanguageModels);
    }

    private IReadOnlyList<Model> CloudRiftLanguageModels =>
    [
        new()
        {
            Id = "deepseek-ai/DeepSeek-V3.1".ToModelId(GetIdentifier()),
            Name = "deepseek-v3.1",
            Description = "DeepSeek V3.1",
            Type = "language",
            OwnedBy = nameof(CloudRift),
            ContextWindow = 163840,
          //  Pricing = new ModelPricing { Input = "0.15", Output = "0.50" }
        },
        new()
        {
            Id = "moonshotai/Kimi-K2-Instruct".ToModelId(GetIdentifier()),
            Name = "kimi-k2-instruct",
            Description = "Kimi K2 Instruct",
            Type = "language",
            OwnedBy = nameof(CloudRift),
            ContextWindow = 131070,
         //   Pricing = new ModelPricing { Input = "0.30", Output = "1.75" }
        },
        new()
        {
            Id = "deepseek-ai/DeepSeek-R1-0528".ToModelId(GetIdentifier()),
            Name = "deepseek-r1-0528",
            Description = "DeepSeek R1 0528",
            Type = "language",
            OwnedBy = nameof(CloudRift),
            ContextWindow = 163840,
          //  Pricing = new ModelPricing { Input = "0.25", Output = "1.00" }
        },
        new()
        {
            Id = "deepseek-ai/DeepSeek-V3".ToModelId(GetIdentifier()),
            Name = "deepseek-v3",
            Description = "DeepSeek V3",
            Type = "language",
            OwnedBy = "CloudRift",
            ContextWindow = 163840,
        //    Pricing = new ModelPricing { Input = "0.15", Output = "0.40" }
        }
    ];
}

