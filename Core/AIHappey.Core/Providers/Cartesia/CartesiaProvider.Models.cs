using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Cartesia;

public partial class CartesiaProvider
{
    private const string CartesiaTtsModelPrefix = "tts/";
    private const string CartesiaTranscriptionModelPrefix = "transcription/";

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken)
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


                var voices = await GetVoicesAsync(cancellationToken);
                var models = new List<Model>();

                models.AddRange(BuildDynamicVoiceModels(voices));
                models.AddRange(BuildTranscriptionModels());
                models.AddRange(BuildRealtimeTranscriptionModels());
                models.AddRange(await GetAgentModelsAsync(cancellationToken));

                return models;

            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private async Task<IReadOnlyList<CartesiaVoice>> GetVoicesAsync(CancellationToken cancellationToken)
    {
        const int limit = 100;

        var voices = new List<CartesiaVoice>();
        string? startingAfter = null;

        while (true)
        {
            var path = string.IsNullOrWhiteSpace(startingAfter)
                ? $"voices?limit={limit}"
                : $"voices?limit={limit}&starting_after={Uri.EscapeDataString(startingAfter)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            ApplyVersionHeader(request, DefaultApiVersion);

            using var resp = await _client.SendAsync(request, cancellationToken);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"{ProviderName} voices list failed ({(int)resp.StatusCode}): {body}");

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var pageVoices = ParseVoices(root);
            if (pageVoices.Count == 0)
                break;

            voices.AddRange(pageVoices);

            var hasMore = root.TryGetProperty("has_more", out var hasMoreEl)
                          && hasMoreEl.ValueKind == JsonValueKind.True;
            if (!hasMore)
                break;

            startingAfter = pageVoices.Last().Id;
            if (string.IsNullOrWhiteSpace(startingAfter))
                break;
        }

        return [.. voices
            .Where(IsValidVoice)
            .GroupBy(v => v.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())];
    }

    private static IReadOnlyList<CartesiaVoice> ParseVoices(JsonElement root)
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
                && !TryGetPropertyIgnoreCase(root, "voices", out array)
                && !TryGetPropertyIgnoreCase(root, "results", out array))
                return [];
        }
        else
        {
            return [];
        }

        if (array.ValueKind != JsonValueKind.Array)
            return [];

        var voices = new List<CartesiaVoice>();

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var id = ReadString(item, "id")
                ?? ReadString(item, "voiceId")
                ?? ReadString(item, "voice_id");

            if (string.IsNullOrWhiteSpace(id))
                continue;

            voices.Add(new CartesiaVoice
            {
                Id = id.Trim(),
                Name = ReadString(item, "name"),
                Gender = ReadString(item, "gender"),
                Language = ReadString(item, "language"),
                IsOwner = ReadBool(item, "is_owner") ?? ReadBool(item, "isOwner"),
                IsPublic = ReadBool(item, "is_public") ?? ReadBool(item, "isPublic")
            });
        }

        return voices;
    }

    private IEnumerable<Model> BuildDynamicVoiceModels(IEnumerable<CartesiaVoice> voices)
    {
        foreach (var voice in voices.Where(IsValidVoice))
        {
            foreach (var ttsModelId in SupportedTtsModelIds)
            {
                yield return new Model
                {
                    Id = $"{CartesiaTtsModelPrefix}{ttsModelId}/{voice.Id}".ToModelId(GetIdentifier()),
                    OwnedBy = ProviderName,
                    Type = "speech",
                    Name = $"{ttsModelId} {BuildVoiceDisplayName(voice)}",
                    Description = $"{ProviderName} TTS voice on {ttsModelId}.",
                    Tags = BuildVoiceTags(voice, ttsModelId)
                };
            }
        }
    }

    private IEnumerable<Model> BuildTranscriptionModels()
        => SupportedTranscriptionModelIds.Select(modelId => new Model
        {
            Id = $"{CartesiaTranscriptionModelPrefix}{modelId}".ToModelId(GetIdentifier()),
            OwnedBy = ProviderName,
            Type = "transcription",
            Name = modelId,
            Description = $"{ProviderName} STT model {modelId}."
        });

    private IEnumerable<Model> BuildRealtimeTranscriptionModels()
    {
        foreach (var modelId in new[] { "ink-2", "ink-preview" })
        {
            foreach (var mode in new[] { "auto", "manual" })
                yield return new Model
                {
                    Id = $"transcription/realtime/{mode}/{modelId}".ToModelId(GetIdentifier()),
                    OwnedBy = ProviderName,
                    Type = "transcription",
                    Name = $"{modelId} realtime ({mode})",
                    Tags = ["real-time"],
                    Description = $"Cartesia realtime {mode} turn-detection transcription."
                };
        }
        yield return new Model
        {
            Id = "transcription/realtime/manual/ink-whisper".ToModelId(GetIdentifier()),
            OwnedBy = ProviderName,
            Type = "transcription",
            Name = "ink-whisper realtime (manual)",
            Tags = ["real-time"],
        };
    }

    private async Task<IEnumerable<Model>> GetAgentModelsAsync(CancellationToken cancellationToken)
    {
        var result = new List<Model>();
        string? cursor = null;
        try
        {
            do
            {
                var path = "v1/agents?limit=100" + (cursor is null ? "" : $"&starting_after={Uri.EscapeDataString(cursor)}");
                using var request = new HttpRequestMessage(HttpMethod.Get, path);
                ApplyVersionHeader(request, RealtimeApiVersion);
                using var response = await _client.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode) break;
                using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                var root = doc.RootElement;
                if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) break;
                foreach (var item in data.EnumerateArray())
                {
                    var id = ReadString(item, "id");
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    result.Add(new Model
                    {
                        Id = $"agents/{id}".ToModelId(GetIdentifier()),
                        OwnedBy = ProviderName,
                        Name = ReadString(item, "name") ?? id,
                        Description = ReadString(item, "description") ?? "Cartesia managed realtime agent.",
                        Type = "audio",
                        Tags = ["real-time", "agent"]
                    });
                }
                cursor = root.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True
                    ? ReadString(root, "next_page") : null;
            } while (!string.IsNullOrWhiteSpace(cursor));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (HttpRequestException) { /* Agent listing is optional; retain speech/STT models. */ }
        catch (JsonException) { /* Retain speech/STT models on malformed agent pages. */ }
        return result;
    }

    private static IEnumerable<string> BuildVoiceTags(CartesiaVoice voice, string ttsModelId)
    {
        var tags = new List<string> { $"voice" };

        if (!string.IsNullOrWhiteSpace(voice.Language))
            tags.Add($"{voice.Language.NormalizeLanguageCode()}");

        if (!string.IsNullOrWhiteSpace(voice.Gender))
            tags.Add($"{voice.Gender}");

        return tags;
    }

    private static bool IsValidVoice(CartesiaVoice voice)
        => !string.IsNullOrWhiteSpace(voice.Id);

    private static string BuildVoiceDisplayName(CartesiaVoice voice)
    {
        var name = string.IsNullOrWhiteSpace(voice.Name) ? voice.Id : voice.Name.Trim();
        var gender = string.IsNullOrWhiteSpace(voice.Gender) ? "unknown" : voice.Gender.Trim();
        var language = string.IsNullOrWhiteSpace(voice.Language) ? "und" : voice.Language.Trim();
        return $"{name} ({gender}, {language})";
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

    private static bool? ReadBool(JsonElement obj, string name)
    {
        if (!TryGetPropertyIgnoreCase(obj, name, out var el))
            return null;

        if (el.ValueKind == JsonValueKind.True) return true;
        if (el.ValueKind == JsonValueKind.False) return false;

        if (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out var parsed))
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

    private sealed class CartesiaVoice
    {
        public string Id { get; set; } = null!;
        public string? Name { get; set; }
        public string? Gender { get; set; }
        public string? Language { get; set; }
        public bool? IsOwner { get; set; }
        public bool? IsPublic { get; set; }
    }
}

