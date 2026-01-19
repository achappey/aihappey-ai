using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Sarvam;

public sealed partial class SarvamProvider : IModelProvider
{
    public Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
        => Task.FromResult<IEnumerable<Model>>(
        [
            new Model
            {
                Id = "sarvam-m".ToModelId(GetIdentifier()),
                Name = "sarvam-m",
                OwnedBy = nameof(Sarvam),
                Description = "sarvam-m is a multilingual, hybrid-reasoning, text-only language model built on Mistral-Small. This post-trained version delivers exceptional improvements over the base model. Performance gains are even more impressive at the intersection of Indian languages and mathematics, with an outstanding +86% improvement in romanized Indian language GSM-8K benchmarks.",
                Type = "language",
                Pricing = new ModelPricing() {
                    Input = 0,
                    Output = 0
                },
                Created = new DateTimeOffset(2025, 5, 23, 0, 0, 0, TimeSpan.Zero)
                    .ToUnixTimeSeconds()
            },
             new Model
            {
                Id = "saarika:v2.5".ToModelId(GetIdentifier()),
                Name = "saarika:v2.5",
                OwnedBy = nameof(Sarvam),
                Description = "The Saarika model can be used for converting speech to text across different scenarios. It supports basic transcription, code-mixed speech, and automatic language detection for Indian languages.",
                Type = "transcription",
                Created = new DateTimeOffset(2025, 5, 23, 0, 0, 0, TimeSpan.Zero)
                    .ToUnixTimeSeconds()

            },
             new Model
            {
                Id = "bulbul:v2".ToModelId(GetIdentifier()),
                Name = "bulbul:v2",
                OwnedBy = nameof(Sarvam),
                Type = "speech",
                Description = "Bulbul-v2 is our flagship text-to-speech model, specifically designed for Indian languages and accents. It excels in natural-sounding speech synthesis with human-like prosody, multiple voice personalities, and comprehensive support for multiple Indian languages",
                Created = new DateTimeOffset(2025, 4, 1, 0, 0, 0, TimeSpan.Zero)
                    .ToUnixTimeSeconds()

            }
        ]);

}

