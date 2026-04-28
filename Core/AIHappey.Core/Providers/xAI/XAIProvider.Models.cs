using AIHappey.Core.AI;
using AIHappey.Core.Models;
using System.Text.Json;

namespace AIHappey.Core.Providers.xAI;

public partial class XAIProvider
{
    private const string ProviderName = nameof(xAI);
    private const string BaseSpeechModel = "tts";

    private static readonly XAITtsLanguage[] SupportedTtsLanguages =
    [
        new("auto", "Auto Detect"),
        new("en", "English"),
        new("ar-EG", "Arabic (Egypt)"),
        new("ar-SA", "Arabic (Saudi Arabia)"),
        new("ar-AE", "Arabic (UAE)"),
        new("bn", "Bengali"),
        new("zh", "Chinese"),
        new("fr", "French"),
        new("de", "German"),
        new("hi", "Hindi"),
        new("id", "Indonesian"),
        new("it", "Italian"),
        new("ja", "Japanese"),
        new("ko", "Korean"),
        new("pt-BR", "Portuguese (Brazil)"),
        new("pt-PT", "Portuguese (Portugal)"),
        new("ru", "Russian"),
        new("es-MX", "Spanish (Mexico)"),
        new("es-ES", "Spanish (Spain)"),
        new("tr", "Turkish"),
        new("vi", "Vietnamese")
    ];

    public string GetIdentifier() => XAIRequestExtensions.XAIIdentifier;

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

                using var request = new HttpRequestMessage(HttpMethod.Get, "v1/models");
                using var response = await _client.SendAsync(request, cancellationToken);

                var payload = await response.Content.ReadAsStringAsync(cancellationToken);
                response.EnsureSuccessStatusCode();

                using var doc = JsonDocument.Parse(payload);
                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    return [];

                var models = new List<Model>();
                var voices = await GetTtsVoicesAsync(cancellationToken);

                foreach (var item in data.EnumerateArray())
                {
                    var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(id)) continue;

                    long? createdAt = null;
                    if (item.TryGetProperty("created", out var createdEl) && createdEl.ValueKind == JsonValueKind.Number)
                    {
                        // xAI returns epoch seconds (per your example)
                        if (createdEl.TryGetInt64(out var epoch))
                            createdAt = epoch;
                    }

                    models.Add(new Model
                    {
                        Id = id!.ToModelId(GetIdentifier()),
                        Name = id!,
                        Created = createdAt,
                        OwnedBy = ProviderName
                    });
                }

                models.AddRange(BuildSpeechModels(voices));
                models.AddRange(GetIdentifier().GetModels());

                return [.. models
            .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())];
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private async Task<IReadOnlyList<XAITtsVoice>> GetTtsVoicesAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "v1/tts/voices");
        using var response = await _client.SendAsync(request, cancellationToken);

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(payload);
        return ParseTtsVoices(doc.RootElement);
    }

    private static IReadOnlyList<XAITtsVoice> ParseTtsVoices(JsonElement root)
    {
        if (!TryGetPropertyIgnoreCase(root, "voices", out var voicesElement) || voicesElement.ValueKind != JsonValueKind.Array)
            return [];

        return [.. voicesElement.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item => new XAITtsVoice(
                ReadString(item, "voice_id") ?? string.Empty,
                ReadString(item, "name"),
                ReadString(item, "language")))
            .Where(IsValidTtsVoice)
            .GroupBy(voice => voice.VoiceId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())];
    }

    private static IEnumerable<Model> BuildSpeechModels(IReadOnlyList<XAITtsVoice> voices)
    {
        yield return new Model
        {
            Id = BaseSpeechModel.ToModelId(XAIRequestExtensions.XAIIdentifier),
            OwnedBy = ProviderName,
            Type = "speech",
            Name = "xAI Text-to-Speech"
        };

        foreach (var language in SupportedTtsLanguages)
        {
            yield return new Model
            {
                Id = $"{BaseSpeechModel}/{language.Code}".ToModelId(XAIRequestExtensions.XAIIdentifier),
                OwnedBy = ProviderName,
                Type = "speech",
                Name = $"xAI TTS {language.DisplayName}",
                Description = $"xAI text-to-speech with {language.DisplayName} language."
            };

            foreach (var voice in voices)
            {
                yield return new Model
                {
                    Id = $"{BaseSpeechModel}/{language.Code}/{voice.VoiceId}".ToModelId(XAIRequestExtensions.XAIIdentifier),
                    OwnedBy = ProviderName,
                    Type = "speech",
                    Name = $"xAI TTS {language.DisplayName} {voice.Name}",
                    Description = $"xAI text-to-speech with {language.DisplayName} language and {voice.Name} voice."
                };
            }
        }
    }

    private static bool IsValidTtsVoice(XAITtsVoice voice)
        => !string.IsNullOrWhiteSpace(voice.VoiceId);

    private static string NormalizeTtsLanguage(string language)
    {
        var normalized = language.Trim();
        var known = SupportedTtsLanguages.FirstOrDefault(x => string.Equals(x.Code, normalized, StringComparison.OrdinalIgnoreCase));
        return known?.Code ?? normalized;
    }

    private static string NormalizeTtsVoice(string voiceId)
        => voiceId.Trim().ToLowerInvariant();

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

    private sealed record XAITtsLanguage(string Code, string DisplayName);

    private sealed record XAITtsVoice(string VoiceId, string? Name, string? Language);


}
