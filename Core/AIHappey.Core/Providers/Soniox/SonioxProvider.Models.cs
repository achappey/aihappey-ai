using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Soniox;

public partial class SonioxProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return [];

        return await _memoryCache.GetOrCreateAsync<List<Model>>(
            this.GetCacheKey(key),
            async ct =>
            {
                ApplyAuthHeader();
                var models = new List<Model>();

                using (var stt = await GetJsonAsync("v1/models", ct))
                {
                    foreach (var item in ReadArray(stt.RootElement, "models"))
                    {
                        var id = ReadString(item, "id");
                        if (string.IsNullOrWhiteSpace(id))
                            continue;

                        var mode = ReadString(item, "transcription_mode");
                        models.Add(new Model
                        {
                            Id = id.ToModelId(GetIdentifier()),
                            Name = ReadString(item, "name") ?? id,
                            OwnedBy = nameof(Soniox),
                            Type = "transcription",
                            Description = $"Soniox {mode ?? "speech-to-text"} transcription model.",
                            Tags = BuildLanguageTags(item, mode)
                        });
                    }
                }

                using (var tts = await GetJsonAsync("v1/tts-models", ct))
                {
                    foreach (var item in ReadArray(tts.RootElement, "models"))
                    {
                        var id = ReadString(item, "id");
                        if (string.IsNullOrWhiteSpace(id))
                            continue;

                        var languageTags = BuildLanguageTags(item, null).ToArray();
                        models.Add(new Model
                        {
                            Id = id.ToModelId(GetIdentifier()),
                            Name = ReadString(item, "name") ?? id,
                            OwnedBy = nameof(Soniox),
                            Type = "speech",
                            Description = "Soniox text-to-speech model.",
                            Tags = languageTags
                        });

                        foreach (var voice in ReadArray(item, "voices"))
                        {
                            var voiceId = ReadString(voice, "id");
                            if (string.IsNullOrWhiteSpace(voiceId))
                                continue;

                            models.Add(new Model
                            {
                                Id = $"{id}/{voiceId}".ToModelId(GetIdentifier()),
                                Name = $"{voiceId} ({ReadString(item, "name") ?? id})",
                                OwnedBy = nameof(Soniox),
                                Type = "speech",
                                Description = ReadString(voice, "description"),
                                Tags = languageTags.Concat(BuildVoiceTags(voice)).Distinct(StringComparer.OrdinalIgnoreCase)
                            });
                        }
                    }
                }

                foreach (var voice in await GetProjectVoicesAsync(ct))
                {
                    foreach (var voiceModel in ReadArray(voice, "models")
                        .Where(x => string.Equals(ReadString(x, "status"), "ready", StringComparison.OrdinalIgnoreCase)))
                    {
                        var modelId = ReadString(voiceModel, "model");
                        var voiceId = ReadString(voice, "id");
                        if (string.IsNullOrWhiteSpace(modelId) || string.IsNullOrWhiteSpace(voiceId))
                            continue;

                        models.Add(new Model
                        {
                            Id = $"{modelId}/{voiceId}".ToModelId(GetIdentifier()),
                            Name = ReadString(voice, "name") ?? voiceId,
                            OwnedBy = nameof(Soniox),
                            Type = "speech",
                            Description = $"Soniox cloned voice for {modelId}.",
                            Tags = ["voice", "cloned"]
                        });
                    }
                }

                return [.. models.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Select(x => x.First())];
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(path, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Soniox request to {path} failed ({(int)response.StatusCode}): {body}");
        return JsonDocument.Parse(body);
    }

    private async Task<IReadOnlyList<JsonElement>> GetProjectVoicesAsync(CancellationToken cancellationToken)
    {
        var result = new List<JsonElement>();
        string? cursor = null;
        do
        {
            var path = "v1/voices?limit=100" + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}");
            using var page = await GetJsonAsync(path, cancellationToken);
            result.AddRange(ReadArray(page.RootElement, "voices").Select(x => x.Clone()));
            cursor = ReadString(page.RootElement, "next_page_cursor");
        } while (!string.IsNullOrWhiteSpace(cursor));
        return result;
    }

    private static IEnumerable<JsonElement> ReadArray(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : [];

    private static string? ReadString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IEnumerable<string> BuildLanguageTags(JsonElement model, string? mode)
    {
        if (!string.IsNullOrWhiteSpace(mode))
            yield return mode.Replace('_', '-');
        foreach (var language in ReadArray(model, "languages"))
        {
            var code = ReadString(language, "code");
            if (!string.IsNullOrWhiteSpace(code))
                yield return code;
        }
    }

    private static IEnumerable<string> BuildVoiceTags(JsonElement voice)
    {
        yield return "voice";
        var gender = ReadString(voice, "gender");
        if (!string.IsNullOrWhiteSpace(gender))
            yield return gender;
    }
}
