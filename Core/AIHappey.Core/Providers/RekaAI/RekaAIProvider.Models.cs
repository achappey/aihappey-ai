using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;

namespace AIHappey.Core.Providers.RekaAI;

public partial class RekaAIProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return [];

        ApplyAuthHeader();

        using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"RekaAI API error: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var models = new List<Model>();

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return models;

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            if (!el.TryGetProperty("id", out var idEl))
                continue;

            var id = idEl.GetString();
            if (string.IsNullOrWhiteSpace(id))
                continue;

            models.Add(new Model
            {
                Id = id.ToModelId(GetIdentifier()),
                Name = id,
                OwnedBy = GetIdentifier(),
                Type = "language"
            });
        }

        models.Add(new Model
        {
            Id = "transcription_or_translation".ToModelId(GetIdentifier()),
            Name = "transcription_or_translation",
            OwnedBy = GetIdentifier(),
            Type = "transcription"
        });

        return models;
    }

}