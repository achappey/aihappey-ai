using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Perplexity;

public static class PerplexityModels
{
    private static readonly string DisplayName = nameof(Perplexity);
    private static readonly string Identifier = DisplayName.ToLowerInvariant();

    public static List<Model> AllModels { get; } =
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
                Id = "sonar-reasoning".ToModelId(Identifier),
                Name = "sonar-reasoning",
                Created = DateTimeOffset.Parse("2025-01-29T00:00:00Z").ToUnixTimeSeconds(),
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

}
