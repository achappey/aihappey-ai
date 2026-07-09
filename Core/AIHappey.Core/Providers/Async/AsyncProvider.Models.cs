using System.Text;
using System.Text.Json;
using AIHappey.Common.Model.Providers.Async;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Async;

public partial class AsyncProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var models = GetIdentifier().GetModels();
        var key = keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return models.WithPricing(GetIdentifier());

        ApplyAuthHeader();

        foreach (var speechModelId in AsyncSpeechModelIds)
        {
            var voices = await ListVoicesAsync(speechModelId, cancellationToken);
            models.AddRange(BuildSpeechVoiceModels(speechModelId, voices));
        }

        return models
            .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .WithPricing(GetIdentifier());
    }

    private async Task<IReadOnlyList<AsyncVoiceInfo>> ListVoicesAsync(string modelId, CancellationToken cancellationToken)
    {
        const int limit = 100;
        var voices = new List<AsyncVoiceInfo>();
        string? after = null;

        while (true)
        {
            var body = new Dictionary<string, object?>
            {
                ["limit"] = limit,
                ["model_id"] = modelId
            };

            if (!string.IsNullOrWhiteSpace(after))
                body["after"] = after;

            using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var resp = await _client.PostAsync("voices", content, cancellationToken);
            var json = await resp.Content.ReadAsStringAsync(cancellationToken);

            if (!resp.IsSuccessStatusCode)
                return [];

            using var doc = JsonDocument.Parse(json);
            var pageVoices = ParseAsyncVoices(doc.RootElement);
            if (pageVoices.Count == 0)
                break;

            voices.AddRange(pageVoices);

            var nextCursor = ReadString(doc.RootElement, "next_cursor");
            if (string.IsNullOrWhiteSpace(nextCursor) || string.Equals(nextCursor, after, StringComparison.OrdinalIgnoreCase))
                break;

            after = nextCursor;
        }

        return [.. voices
            .Where(IsValidAsyncVoice)
            .GroupBy(v => v.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())];
    }

    private IEnumerable<Model> BuildSpeechVoiceModels(string modelId, IEnumerable<AsyncVoiceInfo> voices)
        => voices
            .Where(IsValidAsyncVoice)
            .Select(v => new Model
            {
                Id = $"{modelId}/{v.Id}".ToModelId(GetIdentifier()),
                Name = $"{modelId}/{v.Id}",
                OwnedBy = "async",
                Type = "speech",
                Description = BuildAsyncVoiceDescription(modelId, v),
                Tags = BuildAsyncVoiceTags(modelId, v)
            });

    private static IReadOnlyList<AsyncVoiceInfo> ParseAsyncVoices(JsonElement root)
    {
        if (!TryGetPropertyIgnoreCase(root, "voices", out var voicesEl) || voicesEl.ValueKind != JsonValueKind.Array)
            return [];

        var voices = new List<AsyncVoiceInfo>();
        foreach (var item in voicesEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var id = ReadString(item, "voice_id") ?? ReadString(item, "voiceId") ?? ReadString(item, "id");
            if (string.IsNullOrWhiteSpace(id))
                continue;

            voices.Add(new AsyncVoiceInfo
            {
                Id = id.Trim(),
                Name = ReadString(item, "name"),
                Description = ReadString(item, "description"),
                Language = ReadString(item, "language"),
                Gender = ReadString(item, "gender"),
                VoiceType = ReadString(item, "voice_type") ?? ReadString(item, "voiceType"),
                Accent = ReadString(item, "accent"),
                Style = ReadString(item, "style")
            });
        }

        return voices;
    }

    private static string BuildAsyncVoiceDescription(string modelId, AsyncVoiceInfo voice)
    {
        var name = string.IsNullOrWhiteSpace(voice.Name) ? voice.Id : voice.Name.Trim();
        var prefix = $"Async text-to-speech shortcut model using voice {name} ({voice.Id}) on {modelId}.";

        return string.IsNullOrWhiteSpace(voice.Description)
            ? prefix
            : $"{prefix} {voice.Description.Trim()}";
    }

    private static IEnumerable<string> BuildAsyncVoiceTags(string modelId, AsyncVoiceInfo voice)
    {
        var tags = new List<string>
        {
            $"model:{modelId}",
            $"voice:{voice.Id}"
        };

        foreach (var language in SplitCommaSeparated(voice.Language).Take(10))
            tags.Add($"language:{language}");

        if (!string.IsNullOrWhiteSpace(voice.Gender))
            tags.Add($"gender:{voice.Gender.Trim()}");

        if (!string.IsNullOrWhiteSpace(voice.VoiceType))
            tags.Add($"voice-type:{voice.VoiceType.Trim()}");

        if (!string.IsNullOrWhiteSpace(voice.Accent))
            tags.Add($"accent:{voice.Accent.Trim()}");

        foreach (var style in SplitCommaSeparated(voice.Style).Take(10))
            tags.Add($"style:{style}");

        return tags;
    }

    private static IEnumerable<string> SplitCommaSeparated(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsValidAsyncVoice(AsyncVoiceInfo voice)
        => !string.IsNullOrWhiteSpace(voice.Id);

    private static string? ReadString(JsonElement obj, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(obj, propertyName, out var el))
            return null;

        if (el.ValueKind == JsonValueKind.String)
            return el.GetString();

        if (el.ValueKind == JsonValueKind.Number)
            return el.GetRawText();

        return null;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string propertyName, out JsonElement value)
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

    private sealed class AsyncVoiceInfo
    {
        public string Id { get; set; } = null!;
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Language { get; set; }
        public string? Gender { get; set; }
        public string? VoiceType { get; set; }
        public string? Accent { get; set; }
        public string? Style { get; set; }
    }
}

