using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.WellSaid;

public sealed partial class WellSaidProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return [];

        using var request = new HttpRequestMessage(HttpMethod.Get, "avatars");
        request.Headers.Add("X-Api-Key", key);
        using var response = await _client.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("avatars", out var avatars) || avatars.ValueKind != JsonValueKind.Array)
            throw new JsonException("Expected a WellSaid avatars array.");

        var models = new List<Model>();
        foreach (var avatar in avatars.EnumerateArray())
        {
            if (avatar.ValueKind != JsonValueKind.Object ||
                !avatar.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number ||
                !id.TryGetInt32(out var speakerId))
                throw new JsonException("Expected an integer WellSaid avatar id.");
            var nativeId = speakerId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            models.Add(new Model
            {
                Id = $"{GetIdentifier()}/{nativeId}",
                Name = avatar.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                    ? name.GetString() ?? nativeId : nativeId,
                OwnedBy = "WellSaid",
                Type = "speech"
            });
        }
        return models.DistinctBy(model => model.Id).ToArray();
    }
}
