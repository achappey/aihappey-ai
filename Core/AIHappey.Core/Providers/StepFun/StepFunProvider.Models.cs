using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.StepFun;

public partial class StepFunProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return await Task.FromResult<IEnumerable<Model>>([]);

        ApplyAuthHeader();

        using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"StepFun API error: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var models = new List<Model>();
        var root = doc.RootElement;

        // ✅ root is already an array
        var arr = root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array
                ? dataEl.EnumerateArray()
                : Enumerable.Empty<JsonElement>();

        foreach (var el in arr)
        {
            Model model = new();

            if (el.TryGetProperty("id", out var idEl))
            {
                model.Id = idEl.GetString()?.ToModelId(GetIdentifier()) ?? "";
                model.Name = idEl.GetString() ?? "";
            }

            if (el.TryGetProperty("created", out var createdEl) && createdEl.ValueKind == JsonValueKind.Number)
                model.Created = createdEl.GetInt64();

            if (el.TryGetProperty("owned_by", out var orgEl))
                model.OwnedBy = orgEl.GetString() ?? "";

            if (!string.IsNullOrEmpty(model.Id))
                models.Add(model);

            if (model.Name == "step-tts-2")
            {
                foreach (var voice in List)
                {
                    models.Add(new Model
                    {
                        Id = $"{voice.SupportedModels}/{voice.Id}".ToModelId(GetIdentifier()),
                        Name = voice.Name,
                        Description = $"{voice.Language} voice for {voice.SupportedModels}. Use cases: {voice.UseCases}.",
                        OwnedBy = model.OwnedBy,
                        Created = model.Created
                    });
                }
            }
        }

        return models;
    }

    public static readonly IReadOnlyList<TtsVoice> List =
    [
        new("English", "Lively Girl", "lively-girl", "step-tts-2", "Audiobook, video dubbing"),
        new("English", "Vibrant Youth", "vibrant-youth", "step-tts-2", "Audiobook, video dubbing"),
        new("English", "Soft-spoken Gentleman", "soft-spoken-gentleman", "step-tts-2", "Audiobook, emotional companionship"),
        new("English", "Magnetic-voiced Male", "magnetic-voiced-male", "step-tts-2", "Audiobook, video dubbing"),
        new("Chinese", "Gentle Lady", "elegantgentle-female", "step-tts-2", "Customer service and transaction handling, voice-over and broadcasting, education and training, emotional companionship"),
        new("Chinese", "Breezy Girl", "livelybreezy-female", "step-tts-2", "Emotional companionship, customer service and transaction handling, education and training, advertising"),
        new("Chinese", "Confident Gentleman", "zixinnansheng", "step-tts-2", "Audiobook, video dubbing")
    ];

    public record TtsVoice(
        string Language,
        string Name,
        string Id,
        string SupportedModels,
        string UseCases
    );
}