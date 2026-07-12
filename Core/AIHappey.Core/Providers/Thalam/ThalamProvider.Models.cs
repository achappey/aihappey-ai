using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using AIHappey.Common.Extensions;

namespace AIHappey.Core.Providers.Thalam;

public partial class ThalamProvider
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
                    throw new Exception($"Thalam API error: {err}");
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                var models = new List<Model>();
                var root = doc.RootElement;
               
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

                    if (el.TryGetProperty("owned_by", out var orgEl))
                        model.OwnedBy = orgEl.GetString() ?? "";

                    if (el.TryGetProperty("display_name", out var nameEl))
                        model.Name = nameEl.GetString() ?? model.Name;

                    model.Type = ResolveThalamModelType(el, model.Id);

                    if (!string.IsNullOrEmpty(model.Id))
                        models.Add(model);
                }

                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private static string ResolveThalamModelType(JsonElement element, string modelId)
    {
        var explicitType = element.TryGetString("type", "model_type", "modelType", "capability", "capabilities", "modality", "modalities");

        if (!string.IsNullOrWhiteSpace(explicitType))
        {
            if (explicitType.Contains("video", StringComparison.OrdinalIgnoreCase))
                return "video";

            if (explicitType.Contains("image", StringComparison.OrdinalIgnoreCase))
                return "image";

            if (explicitType.Contains("speech", StringComparison.OrdinalIgnoreCase)
                || explicitType.Contains("audio", StringComparison.OrdinalIgnoreCase)
                || explicitType.Contains("tts", StringComparison.OrdinalIgnoreCase))
            {
                return "speech";
            }

            if (explicitType.Contains("chat", StringComparison.OrdinalIgnoreCase)
                || explicitType.Contains("text", StringComparison.OrdinalIgnoreCase))
            {
                return "chat";
            }
        }

        if (element.TryGetProperty("capabilities", out var capabilities) && capabilities.ValueKind == JsonValueKind.Object)
        {
            if (capabilities.TryGetProperty("video", out var video) && video.ValueKind == JsonValueKind.True)
                return "video";

            if (capabilities.TryGetProperty("image", out var image) && image.ValueKind == JsonValueKind.True)
                return "image";

            if (capabilities.TryGetProperty("speech", out var speech) && speech.ValueKind == JsonValueKind.True)
                return "speech";

            if (capabilities.TryGetProperty("audio", out var audio) && audio.ValueKind == JsonValueKind.True)
                return "speech";
        }

        var normalized = modelId.StartsWith("thalam/", StringComparison.OrdinalIgnoreCase)
            ? modelId["thalam/".Length..]
            : modelId;

        if (IsThalamVideoModel(normalized))
            return "video";

        if (IsThalamImageModel(normalized))
            return "image";

        if (IsThalamSpeechModel(normalized))
            return "speech";

        return normalized.GuessModelType();
    }

    private static bool IsThalamImageModel(string modelId)
        => ContainsAny(modelId,
            "image", "seedream", "z-image", "glm-image", "flux", "kontext", "hunyuan-image");

    private static bool IsThalamVideoModel(string modelId)
        => ContainsAny(modelId,
            "video", "t2v", "i2v", "wan-", "wan2", "wan2.6", "seedance", "kling", "hailuo", "pixverse", "vidu", "hunyuan-video", "motion-control");

    private static bool IsThalamSpeechModel(string modelId)
        => ContainsAny(modelId,
            "speech", "tts", "minimax-speech", "eleven", "elevenlabs", "fish-audio", "fish-tts");

    private static bool ContainsAny(string value, params string[] fragments)
        => fragments.Any(fragment => value.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}
