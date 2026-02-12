using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Perplexity;

public static class PerplexityModels
{
    private static readonly string DisplayName = nameof(Perplexity);
    private static readonly string Identifier = DisplayName.ToLowerInvariant();

    public static List<Model> SonarModels { get; } =
    [
        new Model
            {
                Id = "sonar".ToModelId(Identifier),
                Name = "sonar",
                Created = DateTimeOffset.Parse("2025-01-21T00:00:00Z").ToUnixTimeSeconds(),
                OwnedBy = DisplayName
            },
            new Model
            {
                Id = "sonar-pro".ToModelId(Identifier),
                Name = "sonar-pro",
                Created = DateTimeOffset.Parse("2025-01-21T00:00:00Z").ToUnixTimeSeconds(),
                OwnedBy = DisplayName
            },
            new Model
            {
                Id = "sonar-reasoning-pro".ToModelId(Identifier),
                Name = "sonar-reasoning-pro",
                Created = DateTimeOffset.Parse("2025-01-29T00:00:00Z").ToUnixTimeSeconds(),
                OwnedBy = DisplayName
            },
            new Model
            {
                Id = "sonar-deep-research".ToModelId(Identifier),
                Name = "sonar-deep-research",
                Created = DateTimeOffset.Parse("2025-02-14T00:00:00Z").ToUnixTimeSeconds(),
                OwnedBy = DisplayName
            }
        ];

    public static List<Model> AgentModels { get; } =
[
        new Model
        {
            Id = "perplexity/sonar".ToModelId(Identifier),
            Name = "perplexity/sonar",
            OwnedBy = DisplayName,
            Type = "language",
            Pricing = new()
            {
                Input = 0.25m,
                Output = 2.50m,
                InputCacheRead = 0.0625m
            }
        },

        new Model
        {
            Id = "anthropic/claude-opus-4-6".ToModelId(Identifier),
            Name = "Claude Opus 4.6",
            OwnedBy = DisplayName,
            Type = "language",
            Pricing = new()
            {
                Input = 5m,
                Output = 25m,
                InputCacheRead = 0.50m
            }
        },

        new Model
        {
            Id = "anthropic/claude-opus-4-5".ToModelId(Identifier),
            Name = "Claude Opus 4.5",
            OwnedBy = DisplayName,
            Type = "language",
            Pricing = new()
            {
                Input = 5m,
                Output = 25m,
                InputCacheRead = 0.50m
            }
        },

        new Model
        {
            Id = "anthropic/claude-sonnet-4-5".ToModelId(Identifier),
            Name = "Claude Sonnet 4.5",
            OwnedBy = DisplayName,
            Type = "language",
            Pricing = new()
            {
                Input = 3m,
                Output = 15m,
                InputCacheRead = 0.30m
            }
        },

        new Model
        {
            Id = "anthropic/claude-haiku-4-5".ToModelId(Identifier),
            Name = "Claude Haiku 4.5",
            OwnedBy = DisplayName,
            Type = "language",
            Pricing = new()
            {
                Input = 1m,
                Output = 5m,
                InputCacheRead = 0.10m
            }
        },

        new Model
        {
            Id = "openai/gpt-5.2".ToModelId(Identifier),
            Name = "GPT-5.2",
            OwnedBy = DisplayName,
            Type = "language",
            Pricing = new()
            {
                Input = 1.75m,
                Output = 14m,
                InputCacheRead = 0.175m
            }
        },

        new Model
        {
            Id = "openai/gpt-5.1".ToModelId(Identifier),
            Name = "GPT-5.1",

            OwnedBy = DisplayName,
            Type = "language",
            Pricing = new()
            {
                Input = 1.25m,
                Output = 10m,
                InputCacheRead = 0.125m
            }
        },

        new Model
        {
            Id = "openai/gpt-5-mini".ToModelId(Identifier),
            Name = "GPT-5 Mini",

            OwnedBy = DisplayName,
            Type = "language",
            Pricing = new()
            {
                Input = 0.25m,
                Output = 2m,
                InputCacheRead = 0.025m
            }
        },

        new Model
        {
            Id = "google/gemini-3-pro-preview".ToModelId(Identifier),
            Name = "Gemini 3 Pro Preview",

            OwnedBy = DisplayName,
            Type = "language",
            Pricing = new()
            {
                Input = 2m,
                Output = 12m
            }
        },

        new Model
        {
            Id = "google/gemini-3-flash-preview".ToModelId(Identifier),
            Name = "Gemini 3 Flash Preview",
            Created = DateTimeOffset.Parse("2025-02-14T00:00:00Z").ToUnixTimeSeconds(),
            OwnedBy = DisplayName,
            Type = "language",
            Pricing = new()
            {
                Input = 0.50m,
                Output = 3m
            }
        },

        new Model
        {
            Id = "google/gemini-2.5-pro".ToModelId(Identifier),
            Name = "Gemini 2.5 Pro",
            OwnedBy = DisplayName,
            Type = "language",
            Pricing = new()
            {
                Input = 1.25m,
                Output = 10m
            }
        },

        new Model
        {
            Id = "google/gemini-2.5-flash".ToModelId(Identifier),
            Name = "Gemini 2.5 Flash",
            OwnedBy = DisplayName,
            Type = "language",
            Pricing = new()
            {
                Input = 0.30m,
                Output = 2.50m
            }
        },

        new Model
        {
            Id = "xai/grok-4-1-fast-non-reasoning".ToModelId(Identifier),
            Name = "Grok 4.1 Fast",
            OwnedBy = DisplayName,
            Type = "language",
            Pricing = new()
            {
                Input = 0.20m,
                Output = 0.50m,
                InputCacheRead = 0.05m
            }
        }

   ];

}
