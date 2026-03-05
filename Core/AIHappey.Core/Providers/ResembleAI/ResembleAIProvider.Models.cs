using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.ResembleAI;

public partial class ResembleAIProvider
{
    private const string ProviderName = nameof(ResembleAI);

    private async Task<IEnumerable<Model>> ListModelsInternal(CancellationToken cancellationToken)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return [];

        ApplyAuthHeader();

        var staticModels = GetIdentifier().GetModels().ToList();
        var models = new List<Model>(staticModels);

        var voices = await GetVoicesAsync(cancellationToken);
        models.AddRange(BuildDynamicVoiceModels(voices, staticModels));

        return [.. models
            .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())];
    }

    private async Task<IReadOnlyList<ResembleVoice>> GetVoicesAsync(CancellationToken cancellationToken)
    {
        const int pageSize = 1000;

        var voices = new List<ResembleVoice>();
        var page = 1;
        var numPages = 1;

        while (page <= numPages)
        {
            var path = $"api/v2/voices?page_size={pageSize}&page={page}";
            using var resp = await _client.GetAsync(path, cancellationToken);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"{ProviderName} voices list failed ({(int)resp.StatusCode}): {body}");

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            voices.AddRange(ParseVoices(root));

            var currentPage = ReadInt(root, "page") ?? page;
            var parsedNumPages = ReadInt(root, "num_pages") ?? currentPage;
            numPages = Math.Max(parsedNumPages, currentPage);
            page = currentPage + 1;
        }

        return [.. voices
            .Where(IsValidVoice)
            .GroupBy(v => v.Uuid, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())];
    }

    private IEnumerable<Model> BuildDynamicVoiceModels(IEnumerable<ResembleVoice> voices, IEnumerable<Model> staticModels)
    {
        var speechBaseModels = staticModels
            .Where(m => string.Equals(m.Type, "speech", StringComparison.OrdinalIgnoreCase))
            .Select(m => new
            {
                BaseModelId = ExtractProviderLocalModelId(m.Id),
                BaseName = string.IsNullOrWhiteSpace(m.Name) ? ExtractProviderLocalModelId(m.Id) : m.Name
            })
            .Where(m => !string.IsNullOrWhiteSpace(m.BaseModelId))
            .ToList();

        foreach (var voice in voices.Where(IsValidVoice))
        {
            var voiceName = string.IsNullOrWhiteSpace(voice.Name) ? voice.Uuid : voice.Name.Trim();

            foreach (var baseModel in speechBaseModels)
            {
                yield return new Model
                {
                    Id = $"{baseModel.BaseModelId}/{voice.Uuid}".ToModelId(GetIdentifier()),
                    OwnedBy = ProviderName,
                    Type = "speech",
                    Name = $"{baseModel.BaseName} · {voiceName} ({voice.Uuid})",
                    Description = $"{ProviderName} {baseModel.BaseModelId} voice {voice.Uuid}.",
                    Tags = BuildVoiceTags(voice, baseModel.BaseModelId)
                };
            }
        }
    }

    private static IEnumerable<string> BuildVoiceTags(ResembleVoice voice, string baseModelId)
    {
        var tags = new List<string>
        {
            $"model:{baseModelId}",
            $"voice:{voice.Uuid}"
        };

        if (!string.IsNullOrWhiteSpace(voice.DefaultLanguage))
            tags.Add($"language:{voice.DefaultLanguage}");

        return tags;
    }

    private static bool IsValidVoice(ResembleVoice voice)
        => !string.IsNullOrWhiteSpace(voice.Uuid)
           && string.Equals(voice.VoiceStatus, "Ready", StringComparison.OrdinalIgnoreCase)
           && voice.SyncSupported;

    private static IReadOnlyList<ResembleVoice> ParseVoices(JsonElement root)
    {
        if (!TryGetPropertyIgnoreCase(root, "items", out var items) || items.ValueKind != JsonValueKind.Array)
            return [];

        var voices = new List<ResembleVoice>();

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var uuid = ReadString(item, "uuid")?.Trim();
            if (string.IsNullOrWhiteSpace(uuid))
                continue;

            var syncSupported = false;
            if (TryGetPropertyIgnoreCase(item, "api_support", out var apiSupport)
                && apiSupport.ValueKind == JsonValueKind.Object)
            {
                syncSupported = ReadBool(apiSupport, "sync") == true;
            }

            voices.Add(new ResembleVoice
            {
                Uuid = uuid,
                Name = ReadString(item, "name"),
                DefaultLanguage = ReadString(item, "default_language"),
                VoiceStatus = ReadString(item, "voice_status"),
                SyncSupported = syncSupported
            });
        }

        return voices;
    }

    private bool IsSupportedSpeechBaseModel(string baseModelId)
        => GetIdentifier().GetModels()
            .Any(m => string.Equals(m.Type, "speech", StringComparison.OrdinalIgnoreCase)
                && string.Equals(ExtractProviderLocalModelId(m.Id), baseModelId, StringComparison.OrdinalIgnoreCase));

    private string ExtractProviderLocalModelId(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return string.Empty;

        var providerPrefix = GetIdentifier() + "/";
        var trimmed = modelId.Trim();

        return trimmed.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[providerPrefix.Length..]
            : trimmed;
    }

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

    private static int? ReadInt(JsonElement obj, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(obj, propertyName, out var el))
            return null;

        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n))
            return n;

        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out n))
            return n;

        return null;
    }

    private static bool? ReadBool(JsonElement obj, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(obj, propertyName, out var el))
            return null;

        if (el.ValueKind == JsonValueKind.True)
            return true;

        if (el.ValueKind == JsonValueKind.False)
            return false;

        if (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out var parsed))
            return parsed;

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

    private sealed class ResembleVoice
    {
        public string Uuid { get; set; } = null!;
        public string? Name { get; set; }
        public string? DefaultLanguage { get; set; }
        public string? VoiceStatus { get; set; }
        public bool SyncSupported { get; set; }
    }
}

