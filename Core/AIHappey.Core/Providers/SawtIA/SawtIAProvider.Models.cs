using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.SawtIA;

public partial class SawtIAProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var cacheKey = this.GetCacheKey();

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                RemoveAuthHeader();

                var models = await GetModelsAsync(ct);
                var voices = await GetVoicesAsync(ct);

                var resolved = new List<Model>();
                resolved.AddRange(BuildBaseSpeechModels(models));
                resolved.AddRange(BuildVoiceShortcutModels(models, voices));

                return resolved
                    .Where(m => !string.IsNullOrWhiteSpace(m.Id))
                    .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToArray();
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private async Task<IReadOnlyList<SawtIAModelDefinition>> GetModelsAsync(CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync("v1/models", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} models list failed ({(int)response.StatusCode}): {body}");

        using var document = JsonDocument.Parse(body);
        var parsed = ParseModels(document.RootElement);

        if (parsed.Count > 0)
            return parsed;

        return
        [
            new SawtIAModelDefinition
            {
                Id = DefaultSpeechModel,
                Name = ToDisplayName(DefaultSpeechModel),
                Description = $"{ProviderName} default text-to-speech model."
            }
        ];
    }

    private async Task<IReadOnlyList<SawtIAVoiceDefinition>> GetVoicesAsync(CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync("v1/voices", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} voices list failed ({(int)response.StatusCode}): {body}");

        using var document = JsonDocument.Parse(body);
        return ParseVoices(document.RootElement);
    }

    private IEnumerable<Model> BuildBaseSpeechModels(IEnumerable<SawtIAModelDefinition> models)
        => models
            .Where(m => !string.IsNullOrWhiteSpace(m.Id))
            .Select(m => new Model
            {
                Id = m.Id.ToModelId(GetIdentifier()),
                OwnedBy = ProviderName,
                Type = "speech",
                Name = string.IsNullOrWhiteSpace(m.Name) ? ToDisplayName(m.Id) : m.Name,
                Description = BuildBaseModelDescription(m),
                Tags =
                [
                    "tts",
                    $"model:{m.Id}",
                    "base"
                ]
            });

    private IEnumerable<Model> BuildVoiceShortcutModels(
        IEnumerable<SawtIAModelDefinition> models,
        IEnumerable<SawtIAVoiceDefinition> voices)
        => models
            .Where(m => !string.IsNullOrWhiteSpace(m.Id))
            .SelectMany(m => voices
                .Where(v => !string.IsNullOrWhiteSpace(v.VoiceId))
                .Select(v => new Model
                {
                    Id = $"{m.Id}/{v.VoiceId}".ToModelId(GetIdentifier()),
                    OwnedBy = ProviderName,
                    Type = "speech",
                    Name = $"{(string.IsNullOrWhiteSpace(m.Name) ? ToDisplayName(m.Id) : m.Name)} · {BuildVoiceDisplayName(v)}",
                    Description = BuildVoiceModelDescription(m, v),
                    Tags = BuildVoiceTags(m.Id, v)
                }));

    private static IReadOnlyList<SawtIAModelDefinition> ParseModels(JsonElement root)
    {
        var models = new List<SawtIAModelDefinition>();

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (TryGetPropertyIgnoreCase(root, "models", out var modelsEl) && modelsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in modelsEl.EnumerateArray())
                    ParseModelObject(item, models);
            }
            else
            {
                foreach (var property in root.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        models.Add(new SawtIAModelDefinition
                        {
                            Id = property.Name.Trim(),
                            Name = ToDisplayName(property.Name),
                            Description = property.Value.GetString()
                        });
                    }
                    else if (property.Value.ValueKind == JsonValueKind.Object)
                    {
                        var item = property.Value;
                        var id = ReadString(item, "id") ?? property.Name;
                        models.Add(new SawtIAModelDefinition
                        {
                            Id = id.Trim(),
                            Name = ReadString(item, "name") ?? ToDisplayName(id),
                            Description = ReadString(item, "description")
                        });
                    }
                }
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
                ParseModelObject(item, models);
        }

        return [.. models
            .Where(m => !string.IsNullOrWhiteSpace(m.Id))
            .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())];
    }

    private static void ParseModelObject(JsonElement item, List<SawtIAModelDefinition> models)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return;

        var id = ReadString(item, "id")
            ?? ReadString(item, "model")
            ?? ReadString(item, "name");

        if (string.IsNullOrWhiteSpace(id))
            return;

        models.Add(new SawtIAModelDefinition
        {
            Id = id.Trim(),
            Name = ReadString(item, "name") ?? ToDisplayName(id),
            Description = ReadString(item, "description")
        });
    }

    private static IReadOnlyList<SawtIAVoiceDefinition> ParseVoices(JsonElement root)
    {
        IEnumerable<JsonElement> array = root.ValueKind switch
        {
            JsonValueKind.Array => root.EnumerateArray(),
            JsonValueKind.Object when TryGetPropertyIgnoreCase(root, "voices", out var voicesEl) && voicesEl.ValueKind == JsonValueKind.Array => voicesEl.EnumerateArray(),
            _ => Enumerable.Empty<JsonElement>()
        };

        var voices = new List<SawtIAVoiceDefinition>();

        foreach (var item in array)
        {
            var voiceId = ReadString(item, "voice_id")
                ?? ReadString(item, "id")
                ?? ReadString(item, "name");

            if (string.IsNullOrWhiteSpace(voiceId))
                continue;

            voices.Add(new SawtIAVoiceDefinition
            {
                VoiceId = voiceId.Trim(),
                Name = ReadString(item, "display_name") ?? ReadString(item, "name"),
                Gender = ReadString(item, "gender"),
                Language = ReadString(item, "language"),
                PreviewUrl = ReadString(item, "audio_url") ?? ReadString(item, "preview_url")
            });
        }

        return [.. voices
            .Where(v => !string.IsNullOrWhiteSpace(v.VoiceId))
            .GroupBy(v => v.VoiceId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())];
    }

    private static string BuildBaseModelDescription(SawtIAModelDefinition model)
    {
        var description = string.IsNullOrWhiteSpace(model.Description)
            ? $"{ProviderName} text-to-speech model."
            : model.Description.Trim();

        return $"{description} Voice may be supplied via request.voice, providerOptions.sawtia.voice, or model id.";
    }

    private static string BuildVoiceModelDescription(SawtIAModelDefinition model, SawtIAVoiceDefinition voice)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(model.Description))
            parts.Add(model.Description.Trim());

        parts.Add($"Pinned to voice '{voice.VoiceId}'.");

        if (!string.IsNullOrWhiteSpace(voice.Language))
            parts.Add($"Language: {voice.Language.Trim()}.");

        if (!string.IsNullOrWhiteSpace(voice.Gender))
            parts.Add($"Gender: {voice.Gender.Trim()}.");

        if (!string.IsNullOrWhiteSpace(voice.PreviewUrl))
            parts.Add($"Preview: {voice.PreviewUrl.Trim()}.");

        return string.Join(" ", parts);
    }

    private static string BuildVoiceDisplayName(SawtIAVoiceDefinition voice)
    {
        var name = string.IsNullOrWhiteSpace(voice.Name) ? voice.VoiceId : voice.Name.Trim();
        var qualifiers = new List<string>();

        if (!string.IsNullOrWhiteSpace(voice.Gender))
            qualifiers.Add(voice.Gender.Trim());

        if (!string.IsNullOrWhiteSpace(voice.Language))
            qualifiers.Add(voice.Language.Trim());

        return qualifiers.Count == 0
            ? name
            : $"{name} ({string.Join(", ", qualifiers)})";
    }

    private static IEnumerable<string> BuildVoiceTags(string modelId, SawtIAVoiceDefinition voice)
    {
        var tags = new List<string>
        {
            "tts",
            $"model:{modelId}",
            $"voice:{voice.VoiceId}"
        };

        if (!string.IsNullOrWhiteSpace(voice.Language))
            tags.Add($"language:{voice.Language.Trim().ToLowerInvariant()}");

        if (!string.IsNullOrWhiteSpace(voice.Gender))
            tags.Add($"gender:{voice.Gender.Trim().ToLowerInvariant()}");

        return tags;
    }

    private static string ToDisplayName(string value)
        => string.Join(
            ' ',
            value
                .Split(['_', '-'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));

    private static string? ReadString(JsonElement obj, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(obj, propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string propertyName, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in obj.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private sealed class SawtIAModelDefinition
    {
        public string Id { get; set; } = null!;
        public string Name { get; set; } = null!;
        public string? Description { get; set; }
    }

    private sealed class SawtIAVoiceDefinition
    {
        public string VoiceId { get; set; } = null!;
        public string? Name { get; set; }
        public string? Gender { get; set; }
        public string? Language { get; set; }
        public string? PreviewUrl { get; set; }
    }
}
