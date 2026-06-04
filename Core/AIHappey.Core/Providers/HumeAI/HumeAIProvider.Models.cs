using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.HumeAI;

public partial class HumeAIProvider
{
    private static readonly string[] HumeVoiceProviders = ["HUME_AI", "CUSTOM_VOICE"];

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return BuildBaseModels();

        var cacheKey = this.GetCacheKey(key);

        return await _memoryCache.GetOrCreateAsync<List<Model>>(
            cacheKey,
            async ct =>
            {
                ApplyAuthHeader();

                var models = BuildBaseModels().ToList();
                var voices = new List<HumeVoice>();
                var failures = new List<Exception>();

                foreach (var provider in HumeVoiceProviders)
                {
                    try
                    {
                        voices.AddRange(await GetVoicesAsync(provider, ct));
                    }
                    catch (Exception ex)
                    {
                        failures.Add(ex);
                    }
                }

                if (voices.Count == 0 && failures.Count == HumeVoiceProviders.Length)
                    throw new InvalidOperationException($"{ProviderName} voices list failed for all voice providers.", failures[0]);

                models.AddRange(BuildVoiceShortcutModels(voices));

                return [.. models
                    .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())];
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private IEnumerable<Model> BuildBaseModels()
    {
        yield return new Model
        {
            Id = BaseSpeechModel.ToModelId(GetIdentifier()),
            OwnedBy = ProviderName,
            Type = "speech",
            Name = "HumeAI Octave",
            Description = "HumeAI Octave text-to-speech base model. Voice can be supplied via request.voice, providerOptions.humeai.voice_id, providerOptions.humeai.voice_name, or a shortcut model.",
            Tags = ["tts", "speech", "octave", "base"]
        };
    }

    private async Task<IReadOnlyList<HumeVoice>> GetVoicesAsync(string provider, CancellationToken cancellationToken)
    {
        const int pageSize = 100;
        var pageNumber = 0;
        var voices = new List<HumeVoice>();

        while (true)
        {
            var path = $"v0/tts/voices?provider={Uri.EscapeDataString(provider)}&page_number={pageNumber}&page_size={pageSize}";
            using var resp = await _client.GetAsync(path, cancellationToken);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"{ProviderName} voices list failed ({(int)resp.StatusCode}) for provider '{provider}': {body}");

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var page = ParseVoices(root, provider);
            voices.AddRange(page);

            var totalPages = HumeReadInt(root, "total_pages");
            if (totalPages is not null)
            {
                pageNumber++;
                if (pageNumber >= totalPages.Value)
                    break;
            }
            else
            {
                if (page.Count < pageSize)
                    break;

                pageNumber++;
            }
        }

        return [.. voices
            .Where(IsValidVoice)
            .GroupBy(v => $"{v.Provider}:{v.Id}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())];
    }

    private static IReadOnlyList<HumeVoice> ParseVoices(JsonElement root, string requestedProvider)
    {
        JsonElement array = default;
        if (root.ValueKind == JsonValueKind.Array)
        {
            array = root;
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            if (!HumeTryGetPropertyIgnoreCase(root, "voices_page", out array)
                && !HumeTryGetPropertyIgnoreCase(root, "voices", out array)
                && !HumeTryGetPropertyIgnoreCase(root, "data", out array)
                && !HumeTryGetPropertyIgnoreCase(root, "items", out array))
            {
                return [];
            }
        }
        else
        {
            return [];
        }

        if (array.ValueKind != JsonValueKind.Array)
            return [];

        var voices = new List<HumeVoice>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var id = HumeReadString(item, "id");
            if (string.IsNullOrWhiteSpace(id))
                continue;

            voices.Add(new HumeVoice
            {
                Id = id.Trim(),
                Name = HumeReadString(item, "name"),
                Provider = NormalizeVoiceProvider(HumeReadString(item, "provider")) ?? requestedProvider,
                CompatibleOctaveModels = HumeReadStringArray(item, "compatible_octave_models")
            });
        }

        return voices;
    }

    private IEnumerable<Model> BuildVoiceShortcutModels(IEnumerable<HumeVoice> voices)
        => voices
            .Where(IsValidVoice)
            .Select(v => new Model
            {
                Id = $"{BaseSpeechModel}/{v.Provider}/{v.Id}".ToModelId(GetIdentifier()),
                OwnedBy = ProviderName,
                Type = "speech",
                Name = BuildVoiceDisplayName(v),
                Description = BuildVoiceDescription(v),
                Tags = BuildVoiceTags(v)
            });

    private static string BuildVoiceDisplayName(HumeVoice voice)
    {
        var name = string.IsNullOrWhiteSpace(voice.Name) ? voice.Id : voice.Name.Trim();
        return $"HumeAI Octave · {name}";
    }

    private static string BuildVoiceDescription(HumeVoice voice)
        => $"HumeAI Octave text-to-speech shortcut model using {voice.Provider} voice '{(string.IsNullOrWhiteSpace(voice.Name) ? voice.Id : voice.Name)}'.";

    private static IEnumerable<string> BuildVoiceTags(HumeVoice voice)
    {
        var tags = new List<string>
        {
            "tts",
            "speech",
            "octave",
            "voice",
            $"provider:{voice.Provider.ToLowerInvariant()}",
            $"voice:{voice.Id}"
        };

        foreach (var compatibleModel in voice.CompatibleOctaveModels.Take(10))
        {
            if (!string.IsNullOrWhiteSpace(compatibleModel))
                tags.Add($"octave-version:{compatibleModel.Trim()}");
        }

        return tags;
    }

    private static bool IsValidVoice(HumeVoice voice)
        => !string.IsNullOrWhiteSpace(voice.Id)
            && !string.IsNullOrWhiteSpace(voice.Provider);

    private static string? HumeReadString(JsonElement obj, string propertyName)
    {
        if (!HumeTryGetPropertyIgnoreCase(obj, propertyName, out var el))
            return null;

        if (el.ValueKind == JsonValueKind.String)
            return el.GetString();

        if (el.ValueKind == JsonValueKind.Number)
            return el.GetRawText();

        return null;
    }

    private static int? HumeReadInt(JsonElement obj, string propertyName)
    {
        if (!HumeTryGetPropertyIgnoreCase(obj, propertyName, out var el))
            return null;

        return el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(el.GetString(), out var value) => value,
            _ => null
        };
    }

    private static IReadOnlyList<string> HumeReadStringArray(JsonElement obj, string propertyName)
    {
        if (!HumeTryGetPropertyIgnoreCase(obj, propertyName, out var el) || el.ValueKind != JsonValueKind.Array)
            return [];

        return [.. el.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim())];
    }

    private static bool HumeTryGetPropertyIgnoreCase(JsonElement obj, string propertyName, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in obj.EnumerateObject())
            {
                if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private sealed class HumeVoice
    {
        public string Id { get; set; } = null!;
        public string? Name { get; set; }
        public string Provider { get; set; } = null!;
        public IReadOnlyList<string> CompatibleOctaveModels { get; set; } = [];
    }
}
