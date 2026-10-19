using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.LMNT;

public sealed partial class LMNTProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return [];

        using var message = CreateRequest(HttpMethod.Get, "v1/ai/voice/list");
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("Expected an LMNT voice array.");

        var models = new List<Model>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var voice in document.RootElement.EnumerateArray())
        {
            if (voice.ValueKind != JsonValueKind.Object
                || !voice.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(id.GetString())
                || !voice.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String)
                throw new JsonException("Expected an LMNT voice ID and state.");
            if (state.GetString() != "ready" || !seen.Add(id.GetString()!))
                continue;
            models.Add(new Model
            {
                Id = $"{GetIdentifier()}/{id.GetString()}",
                Name = voice.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                    ? name.GetString()! : id.GetString()!,
                Description = voice.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String
                    ? description.GetString() : null,
                OwnedBy = "LMNT",
                Type = "speech",
                Metadata = new() { [GetIdentifier()] = voice.Clone() }
            });
        }
        return models;
    }
}
