using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.OpenAI;

public partial class OpenAIProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            return await Task.FromResult<IEnumerable<Model>>([]);

        var cacheKey = this.GetCacheKey(key);

        return await _memoryCache.GetOrCreateAsync<IEnumerable<Model>>(
            cacheKey,
            async ct =>
            {
                ApplyAuthHeader();

                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    "https://api.openai.com/v1/models");

                using var response = await _client.SendAsync(request, ct);

                response.EnsureSuccessStatusCode();

                await using var stream =
                    await response.Content.ReadAsStreamAsync(ct);

                using var document =
                    await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                var models = document.RootElement
                    .GetProperty("data")
                    .EnumerateArray()
                  .Where(model =>
                    {
                        var id = model.GetProperty("id").GetString();

                        var hasShutdownDate =
                            model.TryGetProperty("shutdown_date", out var shutdownDate)
                            && shutdownDate.ValueKind != JsonValueKind.Null
                            && !string.IsNullOrWhiteSpace(shutdownDate.GetString());

                        return !string.IsNullOrWhiteSpace(id)
                            && !hasShutdownDate;
                    })
                    .Select(model =>
                    {
                        var id = model.GetProperty("id").GetString()!;

                        return new Model
                        {
                            Id = id.ToModelId(GetIdentifier()),
                            Name = id,
                            Created = model.TryGetProperty("created", out var created)
                                ? created.GetInt64()
                                : null,
                            Tags = id.Contains("transcribe", StringComparison.OrdinalIgnoreCase)
                            || id.Contains("whisper", StringComparison.OrdinalIgnoreCase)
                                ? ["real-time"]
                                : null,
                            OwnedBy = model.TryGetProperty("owned_by", out var ownedBy)
                                ? ownedBy.GetString() ?? "OpenAI"
                                : "OpenAI"
                        };
                    })
                    .ToList()
                    .WithPricing(GetIdentifier());

                return
                [
                    .. models,

                    new Model
                    {
                        Id = "whisper-1/translate".ToModelId(GetIdentifier()),
                        Description = "Translate audio to English",
                        Name = "whisper-1 Translate to English",
                        OwnedBy = nameof(OpenAI),
                        Type = "transcription"
                    }
                ];
            },
        baseTtl: TimeSpan.FromHours(4),
        jitterMinutes: 480,
        cancellationToken: cancellationToken);
    }
}
