using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Speechmatics;

public partial class SpeechmaticsProvider
{
    public static IReadOnlyList<Model> SpeechmaticsModels =>
    [
        new() { Id = "sarah".ToModelId(nameof(Speechmatics).ToLowerInvariant()),
            Name = "Sarah: English Female (UK)",
            Type = "speech",
            OwnedBy = nameof(Speechmatics) },
        new() { Id = "theo".ToModelId(nameof(Speechmatics).ToLowerInvariant()),
            Name = "Theo: English Male (UK)",
            Type = "speech",
            OwnedBy = nameof(Speechmatics) },
        new() { Id = "megan".ToModelId(nameof(Speechmatics).ToLowerInvariant()),
            Name = "Megan: English Female (US)",
            Type = "speech",
            OwnedBy = nameof(Speechmatics) },
        new() { Id = "jack".ToModelId(nameof(Speechmatics).ToLowerInvariant()),
            Name = "Jack: English Male (US)",
            Type = "speech",
            OwnedBy = nameof(Speechmatics) },

        new() { Id = "standard".ToModelId(nameof(Speechmatics).ToLowerInvariant()),
            Name = "Speechmatics Standard",
            Type = "transcription",
            OwnedBy = nameof(Speechmatics) },

        new() { Id = "enhanced".ToModelId(nameof(Speechmatics).ToLowerInvariant()),
            Name = "Speechmatics Enhanced",
            Type = "transcription",
            OwnedBy = nameof(Speechmatics) },

    ];

}

