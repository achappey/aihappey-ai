using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Core.Extensions;

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

                using var response = await _client.GetAsync("/v1/models", ct);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                var models = document.RootElement.GetProperty("data").EnumerateArray();

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
                            ? (string.IsNullOrWhiteSpace(a.Model) ? null : $"Mistral agent {a.Model}")
                            : a.Description,
                        OwnedBy = GetName(),
                        Type = "language",
                        Created = a.CreatedAt?.ToUnixTimeSeconds(),
                        Tags = ["agent"]
                    });

                var upstreamModels = models
                    .Select(CreateCatalogModel)
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

    private Model CreateCatalogModel(JsonElement model)
    {
        var modelId = model.GetProperty("id").GetString()!;
        var isSpeechModel = modelId.Contains("tts", StringComparison.OrdinalIgnoreCase);
        var isEmbedModel = modelId.Contains("embed", StringComparison.OrdinalIgnoreCase);
        var isRealtimeTranscription = RealtimeCapabilities.IsMistralRealtimeTranscription(modelId);
        var isTranscription = !isSpeechModel && modelId.StartsWith("voxtral-mini-transcribe-", StringComparison.Ordinal);

        var result = new Model()
        {
            Id = modelId.ToModelId(GetIdentifier()),
            Name = modelId,
            OwnedBy = GetName(),
            Type = isSpeechModel ? "speech" : isEmbedModel ? "embedding" : isTranscription ? "transcription" : "language",
            Tags = isRealtimeTranscription ? ["real-time", "realtime-token"] : null,
            Created = model.TryGetProperty("created", out var created)
                && created.ValueKind == JsonValueKind.Number
                && created.TryGetInt64(out var createdTimestamp)
                    ? createdTimestamp
                    : null,
            ContextWindow = model.TryGetProperty("max_context_length", out var contextLength)
                && contextLength.ValueKind == JsonValueKind.Number
                && contextLength.TryGetInt32(out var contextWindow)
                    ? contextWindow
                    : null
        };
      

        return result;
    }

    private void AddSpeechVoiceShortcutModels(List<Model> models, IReadOnlyList<MistralVoiceDefinition> voices)
    {
        var speechBaseModels = models
            .Where(m => string.Equals(m.Type, "speech", StringComparison.OrdinalIgnoreCase))
            .Where(static name => !string.IsNullOrWhiteSpace(name.Name))
            .ToList();

        foreach (var baseModel in speechBaseModels)
        {
            foreach (var voice in voices)
            {
                if (string.IsNullOrWhiteSpace(voice.Slug))
                    continue;

                var shortcut = $"{baseModel.Id}/{voice.Slug}";

                AddModelIfMissing(models, new Model
                {
                    Id = shortcut,
                    Name = shortcut,
                    OwnedBy = GetName(),
                    Pricing = baseModel.Pricing,
                    Type = "speech",
                    Description = $"{GetName()} text-to-speech model '{baseModel.Name}' with preset voice slug '{voice.Slug}' (voice_id: {voice.Id}).",
                    Tags = ["voice"]
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
