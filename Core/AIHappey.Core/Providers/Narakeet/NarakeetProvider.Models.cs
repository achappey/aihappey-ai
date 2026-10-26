using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Narakeet;

public sealed partial class NarakeetProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return [];
        using var request = CreateRequest(HttpMethod.Get, "voices");
        using var response = await _client.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("Expected a Narakeet voice array.");
        var models = new List<Model>();
        foreach (var voice in document.RootElement.EnumerateArray())
        {
            if (voice.ValueKind != JsonValueKind.Object ||
                !voice.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(name.GetString()))
                throw new JsonException("Expected a Narakeet voice name.");
            models.Add(new Model
            {
                Id = $"{GetIdentifier()}/{name.GetString()}",
                Name = name.GetString()!,
                OwnedBy = "Narakeet",
                Type = "speech"
            });
        }
        return models.DistinctBy(model => model.Id).ToArray();
    }
}
