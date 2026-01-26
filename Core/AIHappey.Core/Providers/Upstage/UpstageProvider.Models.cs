using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Upstage;

public partial class UpstageProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return
        [
            new Model()
            {
                OwnedBy = nameof(Upstage),
                Name = "Solar Pro 3",
                ContextWindow = 128_000,
                Type = "language",
                Created = new DateTimeOffset(2026, 1, 26, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
                Id = "solar-pro3".ToModelId(GetIdentifier())
            },
            new Model()
            {
                OwnedBy = nameof(Upstage),
                Name = "Solar Pro 3 260126",
                Type = "language",
                Created = new DateTimeOffset(2026, 1, 26, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
                Id = "solar-pro3-260126".ToModelId(GetIdentifier())
            },
            
            new Model()
            {
                OwnedBy = nameof(Upstage),
                Name = "Solar Pro 2 250710",
                Type = "language",
                Created = new DateTimeOffset(2025, 7, 10, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
                Id = "solar-pro2-250710".ToModelId(GetIdentifier())
            },
            new Model()
            {
                OwnedBy = nameof(Upstage),
                Type = "language",
                Name = "Solar Pro 2 250909",
                Created = new DateTimeOffset(2025, 9, 9, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
                Id = "solar-pro2-250909".ToModelId(GetIdentifier())
            },
            new Model()
            {
                OwnedBy = nameof(Upstage),
                Type = "language",
                Name = "Solar Pro 2 251215",
                Created = new DateTimeOffset(2025, 12, 15, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
                Id = "solar-pro2-251215".ToModelId(GetIdentifier())
            },
             new Model()
            {
                OwnedBy = nameof(StabilityAI),
                Type = "language",
                Name = "Solar Pro 2",
                Created = new DateTimeOffset(2025, 12, 15, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
                Id = "solar-pro2".ToModelId(GetIdentifier())
            },
               new Model()
            {
                OwnedBy = nameof(Upstage),
                Name = "Solar Mini 240612",
                Type = "language",
                Created = new DateTimeOffset(2024, 6, 12, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
                Id = "solar-mini-240612".ToModelId(GetIdentifier())
            },
            new Model()
            {
                OwnedBy = nameof(Upstage),
                Type = "language",
                Name = "Solar Mini 250123",
                Created = new DateTimeOffset(2025, 1, 23, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
                Id = "solar-mini-250123".ToModelId(GetIdentifier())
            },
            new Model()
            {
                OwnedBy = nameof(Upstage),
                Type = "language",
                Name = "Solar Mini 250422",
                Created = new DateTimeOffset(2025, 4, 22, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
                Id = "solar-mini-250422".ToModelId(GetIdentifier())
            },
             new Model()
            {
                OwnedBy = nameof(Upstage),
                Type = "language",
                Name = "Solar Mini",
                Created = new DateTimeOffset(2025, 4, 22, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
                Id = "solar-mini".ToModelId(GetIdentifier())
            }
        ];
    }
}