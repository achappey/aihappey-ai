using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;

namespace AIHappey.Core.Providers.OpenRouter;

public partial class OpenRouterProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var cacheKey = this.GetCacheKey();

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                using var req = new HttpRequestMessage(
                    HttpMethod.Get,
                    "v1/models?output_modalities=all");

                using var resp = await _client.SendAsync(req, ct);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(ct);
                    throw new Exception($"OpenRouter API error: {err}");
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                var models = new List<Model>();
                var root = doc.RootElement;

                var arr = root.TryGetProperty("data", out var dataEl) &&
                          dataEl.ValueKind == JsonValueKind.Array
                    ? dataEl.EnumerateArray()
                    : Enumerable.Empty<JsonElement>();

                foreach (var el in arr)
                {
                    models.AddRange(CreateModels(el));
                }

                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private IEnumerable<Model> CreateModels(JsonElement el)
    {
        var rawId = GetString(el, "id");

        if (string.IsNullOrWhiteSpace(rawId))
            yield break;

        var name = GetString(el, "name") ?? rawId;

        var contextWindow = el.TryGetProperty("context_length", out var contextEl) &&
                            contextEl.ValueKind == JsonValueKind.Number
            ? contextEl.GetInt32()
            : (int?)null;

        var created = el.TryGetProperty("created", out var createdEl) &&
                      createdEl.ValueKind == JsonValueKind.Number
            ? createdEl.GetInt64()
            : (long?)null;

        var inputModalities = GetArchitectureArray(el, "input_modalities");
        var outputModalities = GetArchitectureArray(el, "output_modalities");
        var architectureModality = GetArchitectureString(el, "modality");

        foreach (var type in ResolveModelTypes(
            inputModalities,
            outputModalities,
            architectureModality))
        {
            if (type != "embeddings")
                yield return new Model
                {
                    Id = rawId.ToModelId(GetIdentifier()),
                    Name = name,
                    ContextWindow = contextWindow,
                    Description = GetString(el, "description"),
                    OwnedBy = rawId.Split('/').FirstOrDefault()?.TrimStart('~') ?? GetIdentifier(),
                    Created = created,
                    MaxTokens = ReadMaxTokens(el),
                    Pricing = ReadPricing(el),
                    // Rename this to your actual stack property / enum.
                    Type = type
                };
        }
    }

    private static IEnumerable<string> ResolveModelTypes(
    IReadOnlySet<string> inputModalities,
    IReadOnlySet<string> outputModalities,
    string? architectureModality)
    {
        var modality = architectureModality ?? "";

        var hasTextInput = inputModalities.Contains("text");
        var hasAudioInput =
            inputModalities.Contains("audio") ||
            inputModalities.Contains("speech");

        var hasTextOutput = outputModalities.Contains("text");

        if (outputModalities.Contains("transcription"))
            yield return "transcription";

        if (hasTextOutput && (hasTextInput || !hasAudioInput))
            yield return "language";

        if (outputModalities.Contains("image"))
            yield return "image";

        // OpenRouter docs mention audio, raw payload currently shows speech.
        if (outputModalities.Contains("speech") || outputModalities.Contains("audio"))
            yield return "speech";

        if (outputModalities.Contains("embeddings"))
            yield return "embeddings";

        if (outputModalities.Contains("video"))
            yield return "video";

        if (outputModalities.Contains("rerank"))
            yield return "reranking";
    }

    private static string? GetString(JsonElement el, string propertyName)
    {
        return el.TryGetProperty(propertyName, out var prop) &&
               prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    private static string? GetArchitectureString(JsonElement el, string propertyName)
    {
        if (!el.TryGetProperty("architecture", out var architecture) ||
            architecture.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return architecture.TryGetProperty(propertyName, out var prop) &&
               prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    private static IReadOnlySet<string> GetArchitectureArray(
        JsonElement el,
        string propertyName)
    {
        if (!el.TryGetProperty("architecture", out var architecture) ||
            architecture.ValueKind != JsonValueKind.Object ||
            !architecture.TryGetProperty(propertyName, out var prop) ||
            prop.ValueKind != JsonValueKind.Array)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return prop
            .EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }



    private static ModelPricing? ReadPricing(JsonElement el)
    {
        if (!el.TryGetProperty("pricing", out var pricing) ||
            pricing.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var input = ReadDecimal(pricing, "prompt");
        var output = ReadDecimal(pricing, "completion");

        var inputCacheRead = ReadNullableDecimal(pricing, "input_cache_read");
        var inputCacheWrite = ReadNullableDecimal(pricing, "input_cache_write");

        if (input is null && output is null && inputCacheRead is null && inputCacheWrite is null)
            return null;

        return new ModelPricing
        {
            Input = input ?? 0m,
            Output = output ?? 0m,
            InputCacheRead = inputCacheRead,
            InputCacheWrite = inputCacheWrite
        };
    }

    private static int? ReadMaxTokens(JsonElement el)
    {
        if (!el.TryGetProperty("top_provider", out var topProvider) ||
            topProvider.ValueKind != JsonValueKind.Object ||
            !topProvider.TryGetProperty("max_completion_tokens", out var maxTokens) ||
            maxTokens.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return maxTokens.GetInt32();
    }

    private static decimal? ReadNullableDecimal(JsonElement el, string propertyName)
    {
        return ReadDecimal(el, propertyName);
    }

    private static decimal? ReadDecimal(JsonElement el, string propertyName)
    {
        if (!el.TryGetProperty(propertyName, out var prop))
            return null;

        if (prop.ValueKind == JsonValueKind.Number)
            return prop.GetDecimal();

        if (prop.ValueKind == JsonValueKind.String)
        {
            var value = prop.GetString();

            if (decimal.TryParse(
                value,
                NumberStyles.Number | NumberStyles.AllowExponent,
                CultureInfo.InvariantCulture,
                out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }
}