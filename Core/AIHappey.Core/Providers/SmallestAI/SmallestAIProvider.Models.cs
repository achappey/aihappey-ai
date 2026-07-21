using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.SmallestAI;

public partial class SmallestAIProvider
{
    private const string TtsModelPrefix = "smallestai/";
    private const string AtomsAgentsEndpoint = "https://api.smallest.ai/atoms/v1/agent";
    private const int AtomsAgentsPageSize = 100;
    private const string LightningV31Model = "lightning_v3.1";
    private const string LightningV31ProModel = "lightning_v3.1_pro";
    private const string LightningV31VoiceCatalogModel = "lightning-v3.1";
    private const string PulseModel = "pulse";

    private async Task<IEnumerable<Model>> ListModelsInternal(CancellationToken cancellationToken)
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

                var models = GetIdentifier().GetModels().ToList();

                // The upstream endpoint deliberately returns a union of the standard and
                // Pro voice catalogs without identifying each voice's pool. Therefore only
                // expose standard-pool voice shortcuts. The catalog-backed Pro alias remains
                // available and requires callers to provide an appropriate Pro voice explicitly.
                var v31Voices = await GetVoicesAsync(LightningV31VoiceCatalogModel, ct);
                var agents = await GetAllAgentsAsync(ct);

                models.AddRange(BuildDynamicVoiceModels(LightningV31Model, v31Voices));
                models.AddRange(BuildDynamicAgentModels(agents));

                return [.. models
            .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())];

            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);

    }

    private async Task<IReadOnlyList<SmallestAIVoice>> GetVoicesAsync(string model, CancellationToken cancellationToken)
    {
        using var resp = await _client.GetAsync($"v1/{model}/get_voices", cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} voices list failed for model '{model}' ({(int)resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        return ParseVoices(doc.RootElement);
    }

    private async Task<IReadOnlyList<SmallestAIAgent>> GetAllAgentsAsync(CancellationToken cancellationToken)
    {
        var agents = new List<SmallestAIAgent>();
        var page = 1;

        while (true)
        {
            var result = await GetAgentsPageAsync(page, AtomsAgentsPageSize, cancellationToken);
            if (result.Agents.Count == 0)
                break;

            agents.AddRange(result.Agents);

            if (result.Agents.Count < AtomsAgentsPageSize
                || (result.Total > 0 && agents.Count >= result.Total))
                break;

            page++;
        }

        return agents;
    }

    private async Task<SmallestAIAgentsPage> GetAgentsPageAsync(int page, int offset, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(
            $"{AtomsAgentsEndpoint}?page={page}&offset={offset}&archived=false",
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} agents list failed ({(int)response.StatusCode}): {body}");

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        if (!TryGetPropertyIgnoreCase(root, "data", out var data)
            || data.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"{ProviderName} agents list response did not include a data object.");

        var total = TryGetPropertyIgnoreCase(data, "total", out var totalElement)
            && totalElement.TryGetInt32(out var parsedTotal)
            ? parsedTotal
            : 0;

        var agents = new List<SmallestAIAgent>();
        if (TryGetPropertyIgnoreCase(data, "agents", out var agentsElement)
            && agentsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var agentElement in agentsElement.EnumerateArray())
            {
                if (agentElement.ValueKind != JsonValueKind.Object)
                    continue;

                var id = ReadString(agentElement, "_id") ?? ReadString(agentElement, "id");
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                var archived = TryGetPropertyIgnoreCase(agentElement, "archived", out var archivedElement)
                    && archivedElement.ValueKind == JsonValueKind.True;
                if (archived)
                    continue;

                agents.Add(new SmallestAIAgent
                {
                    Id = id.Trim(),
                    Name = ReadString(agentElement, "name"),
                    Description = ReadString(agentElement, "description"),
                    WorkflowType = ReadString(agentElement, "workflowType"),
                    SlmModel = ReadString(agentElement, "slmModel")
                });
            }
        }

        return new SmallestAIAgentsPage(agents, total);
    }

    private static IReadOnlyList<SmallestAIVoice> ParseVoices(JsonElement root)
    {
        if (!TryGetPropertyIgnoreCase(root, "voices", out var voicesEl)
            || voicesEl.ValueKind != JsonValueKind.Array)
            return [];

        var voices = new List<SmallestAIVoice>();

        foreach (var item in voicesEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var voiceId = ReadString(item, "voiceId") ?? ReadString(item, "voice_id") ?? ReadString(item, "id");
            if (string.IsNullOrWhiteSpace(voiceId))
                continue;

            string? displayName = ReadString(item, "displayName") ?? ReadString(item, "name");
            string? gender = null;
            string? accent = null;
            List<string> languages = [];

            if (TryGetPropertyIgnoreCase(item, "tags", out var tagsEl)
                && tagsEl.ValueKind == JsonValueKind.Object)
            {
                gender = ReadString(tagsEl, "gender");
                accent = ReadString(tagsEl, "accent");

                if (TryGetPropertyIgnoreCase(tagsEl, "language", out var langEl))
                {
                    if (langEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var le in langEl.EnumerateArray())
                        {
                            if (le.ValueKind == JsonValueKind.String)
                            {
                                var l = le.GetString();
                                if (!string.IsNullOrWhiteSpace(l))
                                    languages.Add(l.Trim());
                            }
                        }
                    }
                    else if (langEl.ValueKind == JsonValueKind.String)
                    {
                        var l = langEl.GetString();
                        if (!string.IsNullOrWhiteSpace(l))
                            languages.Add(l.Trim());
                    }
                }
            }

            voices.Add(new SmallestAIVoice
            {
                VoiceId = voiceId.Trim(),
                DisplayName = displayName,
                Gender = gender,
                Accent = accent,
                Languages = [.. languages.Distinct(StringComparer.OrdinalIgnoreCase)]
            });
        }

        return voices;
    }

    private IEnumerable<Model> BuildDynamicVoiceModels(string model, IEnumerable<SmallestAIVoice> voices)
        => voices
            .Where(v => !string.IsNullOrWhiteSpace(v.VoiceId))
            .Select(v => BuildVoiceModel(model, v));

    private IEnumerable<Model> BuildDynamicAgentModels(IEnumerable<SmallestAIAgent> agents)
        => agents
            .Where(a => !string.IsNullOrWhiteSpace(a.Id))
            .Select(BuildAgentModel);

    private Model BuildAgentModel(SmallestAIAgent agent)
    {
        var name = string.IsNullOrWhiteSpace(agent.Name) ? agent.Id : agent.Name.Trim();
        var description = string.IsNullOrWhiteSpace(agent.Description)
            ? $"{ProviderName} Atoms realtime voice agent '{name}'."
            : agent.Description.Trim();

        return new Model
        {
            Id = $"{TtsModelPrefix}{agent.Id}",
            OwnedBy = ProviderName,
            Type = "audio",
            Name = $"Atoms / {name}",
            Description = description,
            Tags = BuildAgentTags(agent)
        };
    }

    private static IEnumerable<string> BuildAgentTags(SmallestAIAgent agent)
    {
        var tags = new List<string> { "agent", "realtime", "dynamic" };

        if (!string.IsNullOrWhiteSpace(agent.WorkflowType))
            tags.Add(agent.WorkflowType.Trim());

        if (!string.IsNullOrWhiteSpace(agent.SlmModel))
            tags.Add(agent.SlmModel.Trim());

        return tags;
    }

    private Model BuildVoiceModel(string model, SmallestAIVoice voice)
    {
        var normalizedModelId = $"{TtsModelPrefix}{model}/{voice.VoiceId}";
        var languageText = voice.Languages.Count == 0
            ? "und"
            : string.Join(", ", voice.Languages);
        var genderText = string.IsNullOrWhiteSpace(voice.Gender) ? "unknown" : voice.Gender.Trim();
        var accentText = string.IsNullOrWhiteSpace(voice.Accent) ? "unspecified" : voice.Accent.Trim();
        var displayName = string.IsNullOrWhiteSpace(voice.DisplayName) ? voice.VoiceId : voice.DisplayName.Trim();

        return new Model
        {
            Id = normalizedModelId,
            OwnedBy = ProviderName,
            Type = "speech",
            Name = $"{model} / {displayName} ({genderText}, {languageText}, accent: {accentText})",
            Description = $"{ProviderName} {model} voice '{displayName}' (voiceId={voice.VoiceId}, gender={genderText}, language={languageText}, accent={accentText}).",
            Tags = BuildVoiceTags(model, voice)
        };
    }

    private static IEnumerable<string> BuildVoiceTags(string model, SmallestAIVoice voice)
    {
        var tags = new List<string>
        {
            $"voice"
        };

        if (!string.IsNullOrWhiteSpace(voice.Gender))
            tags.Add($"{voice.Gender}");

        foreach (var language in voice.Languages)
            tags.Add($"{language.NormalizeLanguageCode()}");

        return tags;
    }

    private static string? ReadString(JsonElement obj, string name)
    {
        if (!TryGetPropertyIgnoreCase(obj, name, out var el))
            return null;

        if (el.ValueKind == JsonValueKind.String)
            return el.GetString();

        if (el.ValueKind == JsonValueKind.Number)
            return el.GetRawText();

        return null;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string propertyName, out JsonElement value)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private sealed class SmallestAIVoice
    {
        public string VoiceId { get; set; } = null!;
        public string? DisplayName { get; set; }
        public string? Gender { get; set; }
        public string? Accent { get; set; }
        public List<string> Languages { get; set; } = [];
    }

    private sealed class SmallestAIAgent
    {
        public string Id { get; set; } = null!;
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? WorkflowType { get; set; }
        public string? SlmModel { get; set; }
    }

    private sealed record SmallestAIAgentsPage(IReadOnlyList<SmallestAIAgent> Agents, int Total);
}

