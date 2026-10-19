using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Neuphonic;

public sealed partial class NeuphonicProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return [];

        using var request = new HttpRequestMessage(HttpMethod.Get, "voices");
        request.Headers.Add("x-api-key", key);
        using var response = await _client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Neuphonic discovery failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(cancellationToken)}",
                null, response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("voices", out var voices) || voices.ValueKind != JsonValueKind.Array)
            throw new JsonException("Expected a Neuphonic data.voices array.");

        var models = new List<Model>();
        foreach (var voice in voices.EnumerateArray())
        {
            var id = GetString(voice, "voice_id");
            var language = GetString(voice, "lang_code");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(language))
                throw new JsonException("Expected a Neuphonic voice_id and lang_code.");
            models.Add(new Model
            {
                Id = $"{GetIdentifier()}/{language}/{id}",
                Name = GetString(voice, "name") ?? id,
                OwnedBy = "Neuphonic",
                Type = "speech",
                Metadata = new() { [GetIdentifier()] = voice.Clone() }
            });
        }
        return models.DistinctBy(model => model.Id).ToArray();
    }

    private static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}
