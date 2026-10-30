using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.KalpaLabs;

public partial class KalpaLabsProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await _cache.GetOrCreateAsync<IEnumerable<Model>>(
            this.GetCacheKey(_keyResolver.Resolve(ProviderId)),
            async ct =>
            {
                var registry = await SendJsonAsync(HttpMethod.Get, "v1/models", null, ct);
                var voices = await SendJsonAsync(HttpMethod.Get, "v1/voices", null, ct);
                var models = new List<Model>();
                foreach (var model in registry.GetProperty("data").EnumerateArray())
                {
                    var id = model.GetProperty("id").GetString()!;
                    var modes = Strings(model, "modes");
                    if (!modes.Contains("tts") && !modes.Contains("converse"))
                        continue;
                    var tags = modes.Concat(Strings(model, "languages"))
                        .Concat(Strings(model, "speakers").Select(s => $"speaker:{s}")).ToList();
                    if (model.TryGetProperty("default", out var isDefault) && isDefault.GetBoolean())
                        tags.Add("default");
                    var name = model.GetProperty("display_name").GetString()!;
                    var description = model.GetProperty("description").GetString();
                    models.Add(new Model
                    {
                        Id = $"{ProviderId}/{id}", Name = name, Description = description,
                        OwnedBy = ProviderName, Type = "speech", Tags = tags
                    });
                    if (!modes.Contains("tts"))
                        continue;
                    foreach (var voice in voices.GetProperty("data").EnumerateArray())
                    {
                        if (!Strings(voice, "models").Contains(id))
                            continue;
                        models.Add(new Model
                        {
                            Id = $"{ProviderId}/{id}/{voice.GetProperty("id").GetString()}",
                            Name = $"{name} / {voice.GetProperty("name").GetString()}",
                            Description = description, OwnedBy = ProviderName, Type = "speech",
                            Tags = tags.Concat(["voice", voice.GetProperty("gender").GetString()!]).ToArray()
                        });
                    }
                }
                // The public registry does not advertise prices; do not report a zero cost.
                return models;
            },
            baseTtl: TimeSpan.FromHours(1), jitterMinutes: 15, cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static string[] Strings(JsonElement value, string property)
        => value.GetProperty(property).EnumerateArray().Select(v => v.GetString()!).ToArray();

    private static (string Model, string? Voice) ParseModel(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (model.StartsWith(ProviderId + "/", StringComparison.OrdinalIgnoreCase))
            model = model[(ProviderId.Length + 1)..];
        var parts = model.Split('/');
        if (parts.Length > 2 || parts.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("KalpaLabs model must be '[model]' or '[model]/[voice_id]'.", nameof(model));
        return (parts[0], parts.Length == 2 ? parts[1] : null);
    }
}
