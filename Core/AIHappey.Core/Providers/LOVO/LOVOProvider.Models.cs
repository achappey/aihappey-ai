using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.LOVO;

public partial class LOVOProvider
{
    private const string LovoTtsModelPrefix = "tts/";

    private async Task<IEnumerable<Model>> ListModelsInternal(CancellationToken cancellationToken)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return [];

        ApplyAuthHeader();

        var models = new List<Model>();
        var speakers = await GetSpeakersAsync(cancellationToken);

        models.AddRange(BuildDynamicSpeakerModels(speakers));
        return models;
    }

    private async Task<IReadOnlyList<LovoSpeaker>> GetSpeakersAsync(CancellationToken cancellationToken)
    {
        const int limit = 1000;
        var page = 0;
        var speakers = new List<LovoSpeaker>();

        while (true)
        {
            using var resp = await _client.GetAsync($"api/v1/speakers?sort=displayName%3A1&page={page}&limit={limit}", cancellationToken);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"{ProviderName} speakers list failed ({(int)resp.StatusCode}): {body}");

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var pageSpeakers = ParseSpeakers(root);
            if (pageSpeakers.Count == 0)
                break;

            speakers.AddRange(pageSpeakers);

            var hasMore = HasMorePages(root, page, limit, pageSpeakers.Count);
            if (!hasMore)
                break;

            page++;
            if (page > 200)
                break;
        }

        return [.. speakers
            .Where(IsValidSpeaker)
            .GroupBy(s => s.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())];
    }

    private static IReadOnlyList<LovoSpeaker> ParseSpeakers(JsonElement root)
    {
        JsonElement array = default;

        if (root.ValueKind == JsonValueKind.Array)
        {
            array = root;
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            if (!TryGetPropertyIgnoreCase(root, "data", out array)
                && !TryGetPropertyIgnoreCase(root, "items", out array)
                && !TryGetPropertyIgnoreCase(root, "speakers", out array)
                && !TryGetPropertyIgnoreCase(root, "results", out array))
                return [];
        }
        else
        {
            return [];
        }

        if (array.ValueKind != JsonValueKind.Array)
            return [];

        var speakers = new List<LovoSpeaker>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var speakerId = ReadString(item, "id")
                ?? ReadString(item, "speakerId")
                ?? ReadString(item, "speaker_id")
                ?? ReadString(item, "_id");

            if (string.IsNullOrWhiteSpace(speakerId))
                continue;

            var styles = ParseStyleIds(item);

            speakers.Add(new LovoSpeaker
            {
                Id = speakerId.Trim(),
                DisplayName = ReadString(item, "displayName") ?? ReadString(item, "name"),
                Locale = ReadString(item, "locale") ?? ReadString(item, "language"),
                Gender = ReadString(item, "gender"),
                StyleIds = styles
            });
        }

        return speakers;
    }

    private static bool HasMorePages(JsonElement root, int currentPage, int pageSize, int currentCount)
    {
        if (TryReadInt(root, "totalPages") is int totalPages)
            return currentPage + 1 < totalPages;

        if (TryReadInt(root, "pageCount") is int pageCount)
            return currentPage + 1 < pageCount;

        if (TryReadInt(root, "total") is int total)
            return (currentPage + 1) * pageSize < total;

        if (TryReadInt(root, "count") is int count)
            return (currentPage + 1) * pageSize < count;

        return currentCount >= pageSize;
    }

    private IEnumerable<Model> BuildDynamicSpeakerModels(IEnumerable<LovoSpeaker> speakers)
        => speakers
            .Where(IsValidSpeaker)
            .Select(BuildSpeakerModel);

    private Model BuildSpeakerModel(LovoSpeaker speaker)
    {
        var styleSummary = speaker.StyleIds.Count == 0
            ? "No speaker styles exposed."
            : $"Available styles: {string.Join(", ", speaker.StyleIds)}. Set providerOptions.lovo.speakerStyle to use one.";

        return new Model
        {
            Id = $"{LovoTtsModelPrefix}{speaker.Id}".ToModelId(GetIdentifier()),
            OwnedBy = ProviderName,
            Type = "speech",
            Name = BuildSpeakerDisplayName(speaker),
            Description = $"{ProviderName} TTS speaker {speaker.Id}. {styleSummary}",
            Tags = BuildSpeakerTags(speaker)
        };
    }

    private static IEnumerable<string> BuildSpeakerTags(LovoSpeaker speaker)
    {
        var tags = new List<string>();

        if (!string.IsNullOrWhiteSpace(speaker.Locale))
            tags.Add($"locale:{speaker.Locale}");

        if (!string.IsNullOrWhiteSpace(speaker.Gender))
            tags.Add($"gender:{speaker.Gender}");

        foreach (var styleId in speaker.StyleIds.Take(20))
            tags.Add($"style:{styleId}");

        return tags;
    }

    private static bool IsValidSpeaker(LovoSpeaker speaker)
        => !string.IsNullOrWhiteSpace(speaker.Id);

    private static string BuildSpeakerDisplayName(LovoSpeaker speaker)
    {
        var name = string.IsNullOrWhiteSpace(speaker.DisplayName) ? speaker.Id : speaker.DisplayName.Trim();
        var locale = string.IsNullOrWhiteSpace(speaker.Locale) ? "und" : speaker.Locale.Trim();
        var gender = string.IsNullOrWhiteSpace(speaker.Gender) ? "unknown" : speaker.Gender.Trim();
        return $"{name} ({locale}, {gender})";
    }

    private static List<string> ParseStyleIds(JsonElement speaker)
    {
        if (!TryGetPropertyIgnoreCase(speaker, "speakerStyles", out var styles)
            && !TryGetPropertyIgnoreCase(speaker, "styles", out styles))
            return [];

        if (styles.ValueKind != JsonValueKind.Array)
            return [];

        var ids = new List<string>();

        foreach (var style in styles.EnumerateArray())
        {
            string? id = null;

            if (style.ValueKind == JsonValueKind.String)
                id = style.GetString();
            else if (style.ValueKind == JsonValueKind.Object)
                id = ReadString(style, "id") ?? ReadString(style, "styleId") ?? ReadString(style, "speakerStyleId") ?? ReadString(style, "_id");

            if (!string.IsNullOrWhiteSpace(id))
                ids.Add(id.Trim());
        }

        return [.. ids.Distinct(StringComparer.OrdinalIgnoreCase)];
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

    private static int? TryReadInt(JsonElement obj, string name)
    {
        if (!TryGetPropertyIgnoreCase(obj, name, out var el))
            return null;

        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n))
            return n;

        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var parsed))
            return parsed;

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

    private sealed class LovoSpeaker
    {
        public string Id { get; set; } = null!;
        public string? DisplayName { get; set; }
        public string? Locale { get; set; }
        public string? Gender { get; set; }
        public List<string> StyleIds { get; set; } = [];
    }
}

