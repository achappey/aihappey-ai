using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.StepFun;

public partial class StepFunProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            return await Task.FromResult<IEnumerable<Model>>([]);

        var cacheKey = this.GetCacheKey(key);

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
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

                var arr = doc.RootElement.TryGetProperty("data", out var dataEl)
                          && dataEl.ValueKind == JsonValueKind.Array
                    ? dataEl.EnumerateArray()
                    : Enumerable.Empty<JsonElement>();

                foreach (var el in arr)
                {
                    var model = ParseModel(el);

                    if (string.IsNullOrWhiteSpace(model.Id))
                        continue;

                    models.Add(model);

                    if (!model.Name.Contains("tts", StringComparison.OrdinalIgnoreCase))
                        continue;

                    AddVoicesForModel(models, model);
                }

                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);

    }

    private static Model ParseModel(JsonElement el)
    {
        var model = new Model();

        if (el.TryGetProperty("id", out var idEl))
        {
            var id = idEl.GetString() ?? string.Empty;

            model.Id = id.ToModelId("stepfun");
            model.Name = id;
            model.Type = id.GuessModelType();
        }

        if (el.TryGetProperty("created", out var createdEl) &&
            createdEl.ValueKind == JsonValueKind.Number)
        {
            model.Created = createdEl.GetInt64();
        }

        if (el.TryGetProperty("owned_by", out var orgEl))
            model.OwnedBy = orgEl.GetString() ?? string.Empty;

        return model;
    }

    private static void AddVoicesForModel(List<Model> models, Model baseModel)
    {
        foreach (var voice in Voices.Where(v => v.SupportedModels.Contains(baseModel.Name)))
        {
            models.Add(new Model
            {
                Id = $"{baseModel.Name}/{voice.Id}".ToModelId(nameof(StepFun).ToLowerInvariant()),
                Name = $"{baseModel.Name} - {voice.Name}",
                Description = $"Use cases: {voice.UseCases}.",
                Type = "speech",
                OwnedBy = baseModel.OwnedBy,
                Created = baseModel.Created
            });
        }
    }

    public static readonly IReadOnlyList<TtsVoice> Voices =
    [
        new("Vibrant Youth", "vibrant-youth", ["stepaudio-2.5-tts", "step-tts-2"], "Audiobook, video dubbing"),
        new("Lively Girl", "lively-girl", ["stepaudio-2.5-tts", "step-tts-2"], "Audiobook, video dubbing"),
        new("Soft-spoken Gentleman", "soft-spoken-gentleman", ["stepaudio-2.5-tts", "step-tts-2"], "Emotional companionship, audiobook"),
        new("Magnetic-voiced Male", "magnetic-voiced-male", ["stepaudio-2.5-tts", "step-tts-2"], "Audiobook, video dubbing"),
        new("Confident Male", "zixinnansheng", ["stepaudio-2.5-tts", "step-tts-2"], "Audiobook, emotional companionship, education, marketing"),

        new("Elegant Gentle", "elegantgentle-female", ["stepaudio-2.5-tts", "step-tts-2"], "Customer service, voice-over, education, emotional companionship"),
        new("Lively Breezy", "livelybreezy-female", ["stepaudio-2.5-tts", "step-tts-2"], "Emotional companionship, customer service, education, marketing"),
        new("Gentle Male", "wenrounansheng", ["stepaudio-2.5-tts", "step-tts-2"], "Voice-over, emotional companionship, customer service, education"),
        new("Tender Gentleman", "wenrougongzi", ["stepaudio-2.5-tts", "step-tts-2"], "Emotional companionship, audiobook"),
        new("Spirited Male", "yuanqinansheng", ["stepaudio-2.5-tts", "step-tts-2"], "Audiobook, voice-over, customer service"),
        new("Classic Female", "jingdiannvsheng", ["stepaudio-2.5-tts", "step-tts-2"], "Customer service, emotional companionship"),
        new("Mature Gentle", "wenroushunv", ["stepaudio-2.5-tts", "step-tts-2"], "Customer service, voice-over, education"),
        new("Sweet Female", "tianmeinvsheng", ["stepaudio-2.5-tts", "step-tts-2"], "Emotional companionship, customer service"),
        new("Pure Girl", "qingchunshaonv", ["stepaudio-2.5-tts", "step-tts-2"], "Customer service, voice assistant"),
        new("Magnetic Male", "cixingnansheng", ["stepaudio-2.5-tts", "step-tts-2"], "Audiobook, emotional companionship"),
        new("Spirited Girl", "yuanqishaonv", ["stepaudio-2.5-tts", "step-tts-2"], "Audiobook, emotional companionship, voice assistant"),
        new("Girl Next Door", "linjiajiejie", ["stepaudio-2.5-tts", "step-tts-2"], "Voice-over, emotional companionship, voice assistant, video dubbing"),
        new("Upright Youth", "zhengpaiqingnian", ["stepaudio-2.5-tts", "step-tts-2"], "Marketing, audiobook"),
        new("College Student", "qingniandaxuesheng", ["stepaudio-2.5-tts", "step-tts-2"], "Voice-over"),
        new("Broadcast Male", "boyinnansheng", ["stepaudio-2.5-tts", "step-tts-2"], "Audiobook, voice-over"),
        new("Scholarly Gentleman", "ruyananshi", ["stepaudio-2.5-tts", "step-tts-2"], "Audiobook, emotional companionship, voice-over, voice assistant"),
        new("Deep Male", "shenchennanyin", ["stepaudio-2.5-tts", "step-tts-2"], "Emotional companionship, audiobook"),
        new("Friendly Female", "qinqienvsheng", ["stepaudio-2.5-tts", "step-tts-2"], "Voice-over"),
        new("Gentle Female", "wenrounvsheng", ["stepaudio-2.5-tts", "step-tts-2"], "Audiobook, emotional companionship"),
        new("Clever Girl", "jilingshaonv", ["stepaudio-2.5-tts", "step-tts-2"], "Voice assistant, voice-over"),
        new("Cute Soft Female", "ruanmengnvsheng", ["stepaudio-2.5-tts", "step-tts-2"], "Emotional companionship, voice assistant, video dubbing"),
        new("Elegant Female", "youyanvsheng", ["stepaudio-2.5-tts", "step-tts-2"], "Video dubbing"),
        new("Cool Beauty", "lengyanyujie", ["stepaudio-2.5-tts", "step-tts-2"], "Video dubbing"),
        new("Bold Sister", "shuangkuaijiejie", ["stepaudio-2.5-tts", "step-tts-2"], "Voice-over"),
        new("Quiet Scholar", "wenjingxuejie", ["stepaudio-2.5-tts", "step-tts-2"], "Voice-over"),
        new("Kid Sister", "linjiameimei", ["stepaudio-2.5-tts", "step-tts-2"], "Video dubbing, voice-over, voice assistant"),
        new("Intellectual Lady", "zhixingjiejie", ["stepaudio-2.5-tts", "step-tts-2"], "Video dubbing, voice-over, voice assistant"),

        new("Straightforward Male", "shuangkuainansheng", ["stepaudio-2.5-tts", "step-tts-2"], "Customer service, voice assistant"),
        new("Capable Female", "ganliannvsheng", ["stepaudio-2.5-tts", "step-tts-2"], "Customer service, voice assistant"),
        new("Warm Female", "qinhenvsheng", ["stepaudio-2.5-tts", "step-tts-2"], "Customer service, voice assistant"),
        new("Energetic Female", "huolinvsheng", ["stepaudio-2.5-tts", "step-tts-2"], "Customer service, voice assistant")
    ];

    public record TtsVoice(
        string Name,
        string Id,
        string[] SupportedModels,
        string UseCases
    );
}