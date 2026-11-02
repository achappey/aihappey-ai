using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Xpectrum;

public sealed partial class XpectrumProvider
{
    private static readonly string[] Modes = ["chatbot", "agent", "flow", "workflow"];

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return [];
        return await memoryCache.GetOrCreateAsync(this.GetCacheKey(key), async ct =>
        {
            ApplyAuthHeader();
            using var request = new HttpRequestMessage(HttpMethod.Get, "models");
            request.Headers.Accept.Add(new("application/json"));
            using var response = await _client.SendAsync(request, ct);
            await ThrowForStatusAsync(response, ct);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var models = new List<Model>();
            // A key belongs to one app; the list is still enumerated as returned without inventing entries.
            foreach (var item in document.RootElement.GetProperty("data").EnumerateArray())
            {
                var id = Str(item, "id") ?? throw new InvalidOperationException("Xpectrum returned a model without id.");
                var mode = Str(item, "mode");
                models.Add(new Model
                {
                    Id = id.ToModelId(GetIdentifier()),
                    Object = Str(item, "object") ?? "model",
                    Created = item.TryGetProperty("created", out var created) && created.TryGetInt64(out var seconds) ? seconds : null,
                    OwnedBy = Str(item, "owned_by") ?? GetIdentifier(),
                    Name = Str(item, "name") ?? Str(item, "title") ?? id,
                    Description = Str(item, "description"),
                    Type = "language",
                    Tags = mode is null ? null : [mode]
                });
            }
            return (IEnumerable<Model>)models;
        }, baseTtl: TimeSpan.FromHours(4), jitterMinutes: 480, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Resolves the app type from discovery. The key selects the app, so a model id that does not match
    /// still resolves to the single discovered app; chatbot is the conservative default.
    /// </summary>
    private async Task<string> ResolveModeAsync(string? model, CancellationToken cancellationToken)
    {
        List<Model> models;
        try
        {
            models = (await ListModels(cancellationToken)).ToList();
        }
        catch (HttpRequestException)
        {
            // Mode lookup is an optimisation; chat still works and agent apps fall back to streaming on rejection.
            return "chatbot";
        }
        var native = NativeModel(model);
        var match = models.FirstOrDefault(item => item.Id == native.ToModelId(GetIdentifier()))
            ?? (models.Count == 1 ? models[0] : null);
        return match?.Tags?.FirstOrDefault(tag => Modes.Contains(tag, StringComparer.Ordinal)) ?? "chatbot";
    }
}
