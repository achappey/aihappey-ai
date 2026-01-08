using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Inferencenet;

public partial class InferencenetProvider
{
    public Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
        => Task.FromResult<IEnumerable<Model>>(InferencenetLanguageModels);

    public IReadOnlyList<Model> InferencenetLanguageModels =>
    [
        new()
        {
            Id = "meta-llama/llama-3.1-8b-instruct/fp-8".ToModelId(GetIdentifier()),
            Name = "llama-3.1-8b-instruct/fp-8",
            Description = "Llama 3.1 8B Instruct (FP8)",
            Type = "language",
            OwnedBy = "Meta"
        },
        new()
        {
            Id = "inference-net/schematron-3b".ToModelId(GetIdentifier()),
            Name = "schematron-3b",
            Description = "Schematron 3B",
            Type = "language",
            OwnedBy = "Inference.net"
        },
        new()
        {
            Id = "inference-net/schematron-8b".ToModelId(GetIdentifier()),
            Name = "schematron-8b",
            Description = "Schematron 8B",
            Type = "language",
            OwnedBy = "Inference.net"
        },
        new()
        {
            Id = "google/gemma-3-27b-instruct/bf-16".ToModelId(GetIdentifier()),
            Name = "gemma-3-27b-instruct/bf-16",
            Description = "Gemma 3 27B Instruct (BF16)",
            Type = "language",
            OwnedBy = "Google"
        }
    ];
}

