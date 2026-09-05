using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.CheaperInference;

public partial class CheaperInferenceProvider
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
                    throw new Exception($"CheaperInference API error: {err}");
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

                    model.ContextWindow = el.TryGetProperty("context_length", out var v) &&
                        v.ValueKind == JsonValueKind.Number
                            ? v.GetInt32()
                            : null;

                    model.MaxTokens = el.TryGetProperty("max_output_tokens", out var m) &&
                        m.ValueKind == JsonValueKind.Number
                            ? m.GetInt32()
                            : null;

                    model.Type = ResolveCheaperInferenceModelType(el, model.Name);

                    model.Tags = ResolveCheaperInferenceModelTags(el);

                    if (el.TryGetProperty("owned_by", out var orgEl))
                        model.OwnedBy = orgEl.GetString() ?? "";                   

                    if (!string.IsNullOrEmpty(model.Id))
                        models.Add(model);
                }

                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
                cancellationToken: cancellationToken);
    }

    private static string ResolveCheaperInferenceModelType(JsonElement element, string modelId)
    {
        if (element.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
        {
            var value = type.GetString()?.Trim().ToLowerInvariant();
            if (value is "video" or "image" or "speech" or "transcription" or "embedding" or "reranking") return value;
            if (value is "text" or "language" or "chat") return "chat";
        }
        if (element.TryGetProperty("capabilities", out var capabilities) && capabilities.ValueKind == JsonValueKind.Object)
        {
            if (IsCheaperInferenceCapabilityEnabled(capabilities, "video")) return "video";
            if (IsCheaperInferenceCapabilityEnabled(capabilities, "image_generation")
                || IsCheaperInferenceCapabilityEnabled(capabilities, "image_edit")) return "image";
        }
        if (element.TryGetProperty("endpoint", out var endpoint) && endpoint.ValueKind == JsonValueKind.String)
        {
            var value = endpoint.GetString();
            if (value?.Contains("/videos/", StringComparison.OrdinalIgnoreCase) == true) return "video";
            if (value?.Contains("/images/", StringComparison.OrdinalIgnoreCase) == true) return "image";
        }
        return modelId.GuessModelType();
    }

    private static IEnumerable<string>? ResolveCheaperInferenceModelTags(JsonElement element)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (element.TryGetProperty("capabilities", out var capabilities) && capabilities.ValueKind == JsonValueKind.Object)
            foreach (var capability in capabilities.EnumerateObject())
                if (IsCheaperInferenceCapabilityEnabled(capabilities, capability.Name)) tags.Add(capability.Name);
        if (element.TryGetProperty("endpoint", out var endpoint) && endpoint.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(endpoint.GetString())) tags.Add(endpoint.GetString()!);
        return tags.Count == 0 ? null : tags;
    }

    private static bool IsCheaperInferenceCapabilityEnabled(JsonElement capabilities, string name)
        => capabilities.TryGetProperty(name, out var capability)
            && (capability.ValueKind == JsonValueKind.True
                || capability.ValueKind == JsonValueKind.String
                    && bool.TryParse(capability.GetString(), out var enabled) && enabled);
}
