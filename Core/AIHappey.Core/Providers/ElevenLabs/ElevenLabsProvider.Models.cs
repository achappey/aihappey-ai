using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.ElevenLabs;

public partial class ElevenLabsProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        using var response = await _client.GetAsync("v1/models", cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return [];

        var owner = nameof(ElevenLabs);
        var models = new List<Model>();

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var id = el.TryGetProperty("model_id", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(id))
                continue;

            var canTts = el.TryGetProperty("can_do_text_to_speech", out var ttsEl) && ttsEl.ValueKind == JsonValueKind.True;

            if (!canTts)
                continue;

            models.Add(new Model
            {
                Id = id!.ToModelId(GetIdentifier()),
                Name = el.TryGetProperty("name", out var nameEl) ? (nameEl.GetString() ?? id!) : id!,
                OwnedBy = owner,
                Description = el.TryGetProperty("description", out var descEl) ? descEl.GetString() : null,
                Type = "speech",
            });
        }

        // ElevenLabs STT models are not exposed via GET /v1/models.
        models.Add(new Model { Id = "scribe_v1".ToModelId(GetIdentifier()), Name = "scribe_v1", OwnedBy = owner, Type = "transcription" });
        models.Add(new Model { Id = "scribe_v1_experimental".ToModelId(GetIdentifier()), Name = "scribe_v1_experimental", OwnedBy = owner, Type = "transcription" });
        models.Add(new Model
        {
            Id = "scribe_v2_realtime".ToModelId(GetIdentifier()),
            Tags = ["real-time"],
            Name = "scribe_v2_realtime",
            OwnedBy = owner,
            Type = "transcription"
        });

        models.Add(new Model
        {
            Id = "music_v1".ToModelId(GetIdentifier()),
            Name = "music_v1",
            OwnedBy = owner,
            Type = "speech"
        });


        return models;
    }
}

