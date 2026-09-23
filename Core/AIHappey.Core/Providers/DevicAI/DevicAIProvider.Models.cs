using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.DevicAI;

public partial class DevicAIProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return [];

        return await _memoryCache.GetOrCreateAsync(
            this.GetCacheKey(key),
            async ct =>
            {
                var models = new List<Model>();
                foreach (var agent in await ListAllAgentsAsync(ct))
                {
                    if (GetBoolean(agent, "archived") == true || GetBoolean(agent, "disabled") == true)
                        continue;

                    var id = GetString(agent, "_id", "id");
                    if (string.IsNullOrWhiteSpace(id))
                        continue;

                    var name = GetString(agent, "name") ?? id;
                    var maxInput = GetInt(agent, "maxExecutionInputTokens");
                    models.Add(new Model
                    {
                        Id = $"agents/{id}".ToModelId(GetIdentifier()),
                        Name = name,
                        Description = GetString(agent, "description") ?? $"DevicAI agent '{name}'.",
                        OwnedBy = nameof(DevicAI),
                        Type = "language",
                        Created = MillisecondsToSeconds(GetLong(agent, "creationTimestampMs")),
                        ContextWindow = maxInput,
                        Tags = ["agent", "thread", "stateful", "tools"]
                    });
                }

                var assistants = await SendJsonAsync(HttpMethod.Get, "assistants", null, "list assistants", ct);
                if (assistants.ValueKind == JsonValueKind.Array)
                {
                    foreach (var assistant in assistants.EnumerateArray())
                    {
                        var identifier = GetString(assistant, "identifier");
                        if (string.IsNullOrWhiteSpace(identifier))
                            continue;
                        var name = GetString(assistant, "name") ?? identifier;
                        models.Add(new Model
                        {
                            Id = $"assistants/{identifier}".ToModelId(GetIdentifier()),
                            Name = name,
                            Description = GetString(assistant, "description") ?? $"DevicAI assistant '{name}'.",
                            OwnedBy = nameof(DevicAI),
                            Type = "language",
                            Created = GetDate(assistant, "createdAt")?.ToUnixTimeSeconds(),
                            Tags = ["assistant", "chat", "stateful", "tools", "streaming"]
                        });
                    }
                }

                models.Add(new Model
                {
                    Id = "whisper".ToModelId(GetIdentifier()),
                    Name = "DevicAI Whisper",
                    Description = "DevicAI speech-to-text transcription with reusable transcript identifiers.",
                    OwnedBy = nameof(DevicAI),
                    Type = "transcription",
                    Tags = ["transcription", "speech-to-text", "whisper"]
                });

                return (IEnumerable<Model>)models
                    .GroupBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();
            },
            baseTtl: TimeSpan.FromMinutes(15),
            jitterMinutes: 2,
            cancellationToken: cancellationToken);
    }

    private async Task<List<JsonElement>> ListAllAgentsAsync(CancellationToken cancellationToken)
    {
        var agents = new List<JsonElement>();
        var offset = 0;
        const int limit = 100;
        while (true)
        {
            var root = await SendJsonAsync(
                HttpMethod.Get,
                $"agents?offset={offset}&limit={limit}",
                null,
                "list agents",
                cancellationToken);
            var page = GetArray(root, "agents");
            agents.AddRange(page);
            var hasMore = GetBoolean(root, "hasMore") == true;
            if (!hasMore || page.Count == 0)
                break;
            offset += page.Count;
        }
        return agents;
    }

    private static long? MillisecondsToSeconds(long? value)
        => value.HasValue ? value.Value / 1000 : null;
}
