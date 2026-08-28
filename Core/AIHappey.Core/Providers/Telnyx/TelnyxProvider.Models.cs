using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Telnyx;

public partial class TelnyxProvider
{
    public async Task<IEnumerable<Model>> ListModels(
        CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            return [];

        var cacheKey = this.GetCacheKey(key);

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                ApplyAuthHeader();

                var models = new List<Model>();

                // Regular Telnyx AI models
                using (var req = new HttpRequestMessage(HttpMethod.Get, "ai/models"))
                using (var resp = await _client.SendAsync(req, ct))
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);

                    if (!resp.IsSuccessStatusCode)
                        throw new InvalidOperationException(
                            $"Telnyx models failed ({(int)resp.StatusCode}): {body}");

                    using var doc = JsonDocument.Parse(body);

                    if (doc.RootElement.TryGetProperty("data", out var data)
                        && data.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in data.EnumerateArray())
                        {
                            var id = el.TryGetProperty("id", out var idEl)
                                ? idEl.GetString()
                                : null;

                            if (string.IsNullOrWhiteSpace(id))
                                continue;

                            var fullId = id.ToModelId(GetIdentifier());

                            models.Add(new Model
                            {
                                Id = fullId,
                                Name = id,
                                OwnedBy = el.TryGetProperty("owned_by", out var ownedBy)
                                    ? ownedBy.GetString() ?? ""
                                    : "",
                                Created = el.TryGetProperty("created", out var created)
                                    && created.ValueKind == JsonValueKind.Number
                                        ? created.GetInt64()
                                        : null,
                                Type = fullId.GuessModelType()
                            });
                        }
                    }
                }

                // OpenAI-compatible embedding models
                using (var req = new HttpRequestMessage(
                           HttpMethod.Get,
                           "ai/openai/embeddings/models"))
                using (var resp = await _client.SendAsync(req, ct))
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);

                    if (!resp.IsSuccessStatusCode)
                        throw new InvalidOperationException(
                            $"Telnyx embedding models failed ({(int)resp.StatusCode}): {body}");

                    using var doc = JsonDocument.Parse(body);

                    if (doc.RootElement.TryGetProperty("data", out var data)
                        && data.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in data.EnumerateArray())
                        {
                            var id = el.TryGetProperty("id", out var idEl)
                                ? idEl.GetString()
                                : null;

                            if (string.IsNullOrWhiteSpace(id))
                                continue;

                            var fullId = id.ToModelId(GetIdentifier());

                            models.Add(new Model
                            {
                                Id = fullId,
                                Name = id,
                                OwnedBy = el.TryGetProperty("owned_by", out var ownedBy)
                                    ? ownedBy.GetString() ?? ""
                                    : "",
                                Created = el.TryGetProperty("created", out var created)
                                    && created.ValueKind == JsonValueKind.Number
                                        ? created.GetInt64()
                                        : null,
                                Type = "embedding"
                            });
                        }
                    }
                }

                // Telnyx can proxy voices from every supported TTS provider. Expose
                // each one as a selectable model slug while retaining the bare model.
                using (var req = new HttpRequestMessage(HttpMethod.Get, "text-to-speech/voices"))
                using (var resp = await _client.SendAsync(req, ct))
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);

                    if (!resp.IsSuccessStatusCode)
                        throw new InvalidOperationException(
                            $"Telnyx voices failed ({(int)resp.StatusCode}): {body}");

                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("voices", out var voices)
                        && voices.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var voice in voices.EnumerateArray())
                        {
                            var voiceId = ReadVoiceString(voice, "voice_id")?.Trim();
                            if (string.IsNullOrWhiteSpace(voiceId))
                                continue;

                            var provider = ReadVoiceString(voice, "provider");
                            var name = ReadVoiceString(voice, "name");
                            var language = ReadVoiceString(voice, "language");
                            var gender = ReadVoiceString(voice, "gender");
                            var hosted = voice.TryGetProperty("hosted", out var hostedElement)
                                && hostedElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                                    ? hostedElement.GetBoolean()
                                    : (bool?)null;

                            var tags = new List<string> { "voice" };
                            AddVoiceTag(tags, provider);
                            AddVoiceTag(tags, language);
                            AddVoiceTag(tags, gender);
                            if (hosted is not null)
                                tags.Add(hosted.Value ? "hosted" : "third-party");

                            var details = new List<string>();
                            AddVoiceDetail(details, "Provider", provider);
                            AddVoiceDetail(details, "Language", language);
                            AddVoiceDetail(details, "Gender", gender);
                            if (hosted is not null)
                                details.Add($"Hosted: {hosted.Value.ToString().ToLowerInvariant()}");

                            models.Add(new Model
                            {
                                Id = $"text-to-speech/{voiceId}".ToModelId(GetIdentifier()),
                                Name = string.IsNullOrWhiteSpace(name) ? voiceId : name,
                                OwnedBy = string.IsNullOrWhiteSpace(provider) ? nameof(Telnyx) : provider,
                                Type = "speech",
                                Description = $"Telnyx text-to-speech voice {voiceId}. {string.Join("; ", details)}",
                                Tags = tags
                            });
                        }
                    }
                }

                models.AddRange(
                    await this.ListModels(
                        _keyResolver.Resolve(GetIdentifier())));

                return models
                    .GroupBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private static string? ReadVoiceString(JsonElement voice, string propertyName)
        => voice.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static void AddVoiceTag(ICollection<string> tags, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            tags.Add(value.Trim());
    }

    private static void AddVoiceDetail(ICollection<string> details, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            details.Add($"{label}: {value.Trim()}");
    }
}
