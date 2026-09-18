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
                    .ToList();

                try
                {
                    models.AddRange(await ListOpenAiAgentModelsAsync(ct));
                }
                catch
                {
                    // Agent listing is beta and separately permissioned. Standard model
                    // discovery must remain available when api.agents.read is absent.
                }

                return models
                    .GroupBy(static model => model.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(static group => group.First())
                    .OrderByDescending(static model => model.Created ?? 0)
                    .WithPricing(GetIdentifier());
            },
        baseTtl: TimeSpan.FromHours(4),
        jitterMinutes: 480,
        cancellationToken: cancellationToken);
    }

    private async Task<IEnumerable<Model>> ListOpenAiAgentModelsAsync(CancellationToken cancellationToken)
    {
        var models = new List<Model>();
        string? after = null;

        do
        {
            var uri = $"{AgentsEndpoint}?order=desc&limit={AgentPageSize}"
                      + (after is null ? string.Empty : $"&after={Uri.EscapeDataString(after)}");
            var page = await SendOpenAiAgentsJsonAsync(
                HttpMethod.Get,
                uri,
                null,
                "OpenAI agents list",
                cancellationToken);

            if (TryGetOpenAiProperty(page, "data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var agent in data.EnumerateArray())
                {
                    var id = TryGetOpenAiString(agent, "id");
                    if (string.IsNullOrWhiteSpace(id))
                        continue;

                    var name = TryGetOpenAiString(agent, "name");
                    var backingModel = TryGetOpenAiString(agent, "model");
                    var instructions = TryGetOpenAiString(agent, "instructions");
                    var description = string.IsNullOrWhiteSpace(instructions)
                        ? $"OpenAI managed agent backed by {backingModel ?? "an OpenAI model"}."
                        : instructions;

                    models.Add(new Model
                    {
                        Id = $"{AgentModelPrefix}{id}".ToModelId(GetIdentifier()),
                        Name = string.IsNullOrWhiteSpace(name) ? id : name,
                        Description = description,
                        OwnedBy = nameof(OpenAI),
                        Type = "language",
                        Tags = ["agent"],
                        Created = TryGetOpenAiInt64(agent, "created_at")
                    });
                }
            }

            after = TryGetOpenAiBool(page, "has_more") == true
                ? TryGetOpenAiString(page, "last_id")
                : null;
        } while (!string.IsNullOrWhiteSpace(after));

        return models;
    }
}
