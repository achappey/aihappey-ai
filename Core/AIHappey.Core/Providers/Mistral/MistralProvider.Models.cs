using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using MIS = Mistral.SDK;

namespace AIHappey.Core.Providers.Mistral;

public partial class MistralProvider
{
    private const string AgentModelPrefix = "agent/";
    private const int VoicePageSize = 100;


    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            return await Task.FromResult<IEnumerable<Model>>([]);

        var cacheKey = this.GetCacheKey(key);

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                ApplyAuthHeader();

                var client = new MIS.MistralClient(
                  _keyResolver.Resolve(GetIdentifier()),
                  _client
                );

                var models = await client.Models
                    .GetModelsAsync(cancellationToken: ct);

                var agents = await ListAgentsAsync(ct);
                var voices = await ListVoicesAsync(ct);

                List<Model> imageModels = [new Model()
                        {
                            Id = "mistral-medium-latest".ToModelId(GetIdentifier()),
                            Name = "mistral-medium-latest",
                            OwnedBy = GetName(),
                            Type = "image"
                        }, new Model()
                        {
                            Id = "mistral-large-latest".ToModelId(GetIdentifier()),
                            Name = "mistral-large-latest",
                            OwnedBy = GetName(),
                            Type = "image"
                        }];

                var agentModels = agents
                    .Where(a => !string.IsNullOrWhiteSpace(a.Id))
                    .Select(a => new Model()
                    {
                        Id = $"{AgentModelPrefix}{a.Id}".ToModelId(GetIdentifier()),
                        Name = string.IsNullOrWhiteSpace(a.Name) ? $"{AgentModelPrefix}{a.Id}" : a.Name!,
                        Description = string.IsNullOrWhiteSpace(a.Description)
                            ? (string.IsNullOrWhiteSpace(a.Model) ? null : $"Mistral agent backed by {a.Model}")
                            : a.Description,
                        OwnedBy = GetName(),
                        Type = "language",
                        Created = a.CreatedAt?.ToUnixTimeSeconds(),
                        Tags = string.IsNullOrWhiteSpace(a.Model)
                            ? ["agent"]
                            : ["agent", a.Model!]
                    });

                var upstreamModels = models.Data
                    .Select(a => CreateCatalogModel(a.Id))
                    .ToList();

                AddSpeechVoiceShortcutModels(upstreamModels, voices);

                return upstreamModels
                    .Concat(imageModels)
                    .Concat(agentModels)
                    .OrderByDescending(a => a.Created ?? 0)
                    .WithPricing(GetIdentifier());
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private async Task<IReadOnlyList<MistralAgentDefinition>> ListAgentsAsync(CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        using var req = new HttpRequestMessage(HttpMethod.Get, "/v1/agents");
        using var resp = await _client.SendAsync(req, cancellationToken);
        if (!resp.IsSuccessStatusCode)
            return [];

        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<List<MistralAgentDefinition>>(body, JsonSerializerOptions.Web) ?? [];
    }

    private async Task<IReadOnlyList<MistralVoiceDefinition>> ListVoicesAsync(CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        var results = new List<MistralVoiceDefinition>();
        var offset = 0;

        while (true)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"/v1/audio/voices?limit={VoicePageSize}&offset={offset}");
            using var resp = await _client.SendAsync(req, cancellationToken);
            if (!resp.IsSuccessStatusCode)
                return results;

            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            var page = JsonSerializer.Deserialize<MistralVoiceListResponse>(body, JsonSerializerOptions.Web);
            if (page?.Items is null || page.Items.Count == 0)
                break;

            results.AddRange(page.Items.Where(static voice => !string.IsNullOrWhiteSpace(voice.Id)));

            offset += page.Items.Count;
            if (page.Total > 0 && offset >= page.Total)
                break;
            if (page.Items.Count < VoicePageSize)
                break;
        }

        return results;
    }

    private Model CreateCatalogModel(string modelId)
    {
        var isSpeechModel = modelId.Contains("tts", StringComparison.OrdinalIgnoreCase);

        return new Model()
        {
            Id = modelId.ToModelId(GetIdentifier()),
            Name = modelId,
            OwnedBy = GetName(),
            Type = isSpeechModel ? "speech" : string.Empty
        };
    }

    private void AddSpeechVoiceShortcutModels(List<Model> models, IReadOnlyList<MistralVoiceDefinition> voices)
    {
        var speechBaseModels = models
            .Where(m => string.Equals(m.Type, "speech", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Name)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var baseModel in speechBaseModels)
        {
            foreach (var voice in voices)
            {
                if (string.IsNullOrWhiteSpace(voice.Slug))
                    continue;

                var shortcut = $"{baseModel}/{voice.Slug}";
                AddModelIfMissing(models, new Model
                {
                    Id = shortcut.ToModelId(GetIdentifier()),
                    Name = shortcut,
                    OwnedBy = GetName(),
                    Type = "speech",
                    Description = $"{GetName()} text-to-speech model '{baseModel}' with preset voice slug '{voice.Slug}' (voice_id: {voice.Id}).",
                    Tags = ["tts", $"model:{baseModel}", $"voice:{voice.Slug}", "shortcut"]
                });
            }
        }
    }

    private static void AddModelIfMissing(List<Model> models, Model model)
    {
        if (models.Any(existing => string.Equals(existing.Id, model.Id, StringComparison.OrdinalIgnoreCase)))
            return;

        models.Add(model);
    }

    private async Task<MistralVoiceDefinition?> ResolveVoiceBySlugAsync(string slug, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return null;

        var voices = await ListVoicesAsync(cancellationToken);
        return voices.FirstOrDefault(voice =>
            !string.IsNullOrWhiteSpace(voice.Slug)
            && string.Equals(voice.Slug, slug.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private sealed class MistralAgentDefinition
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("created_at")]
        public DateTimeOffset? CreatedAt { get; set; }
    }

    private sealed class MistralVoiceListResponse
    {
        [JsonPropertyName("items")]
        public List<MistralVoiceDefinition> Items { get; set; } = [];

        [JsonPropertyName("total")]
        public int Total { get; set; }
    }

    private sealed class MistralVoiceDefinition
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("slug")]
        public string? Slug { get; set; }
    }

}
