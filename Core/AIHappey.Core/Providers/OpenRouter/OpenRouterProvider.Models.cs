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
                var models = new List<Model>();

                foreach (var el in await ReadOpenRouterModelCatalog("v1/models?output_modalities=all", required: true, ct))
                    models.AddRange(CreateModels(el));

                // Dedicated speech/transcription models are only guaranteed when their modalities are requested explicitly.
                foreach (var el in await ReadOpenRouterModelCatalog("v1/models?output_modalities=speech,transcription", required: false, ct))
                    models.AddRange(CreateModels(el));

                return models
                    .GroupBy(model => $"{model.Id}|{model.Type}", StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToArray();
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private static readonly string[] OpenRouterSpeechOutputFormats = ["mp3", "pcm"];

    private static readonly string[] OpenRouterTranscriptionInputFormats = ["wav", "mp3", "flac", "m4a", "ogg", "webm", "aac"];

    private async Task<JsonElement[]> ReadOpenRouterModelCatalog(string uri, bool required, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        using var resp = await _client.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            if (!required)
                return [];

            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new Exception($"OpenRouter API error from: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("data", out var dataEl)
            || dataEl.ValueKind != JsonValueKind.Array)
            return [];

        return [.. dataEl.EnumerateArray().Select(el => el.Clone())];
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

        foreach (var type in ResolveModelTypes(
            inputModalities,
            outputModalities))
        {
            yield return new Model
            {
                Id = rawId.ToModelId(GetIdentifier()),
                Name = name,
                ContextWindow = contextWindow,
                Description = GetString(el, "description"),
                OwnedBy = rawId.Split('/').FirstOrDefault()?.TrimStart('~') ?? GetIdentifier(),
                Created = created,
                MaxTokens = ReadMaxTokens(el),
                Pricing = ReadPricing(el, type),
                Tags = ResolveModelTags(el, type),
                Type = type
            };
        }
    }

    private static IEnumerable<string> ResolveModelTypes(
    IReadOnlySet<string> inputModalities,
    IReadOnlySet<string> outputModalities)
    {
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

        if (outputModalities.Contains("decisions"))
            yield return "decision";

        // "audio" output belongs to chat-completions audio models; /audio/speech models report "speech".
        if (outputModalities.Contains("speech"))
            yield return "speech";

        if (outputModalities.Contains("embeddings"))
            yield return "embedding";

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



    private static IEnumerable<string>? ResolveModelTags(JsonElement el, string type)
    {
        var tags = new List<string>();

        if (type == "speech")
        {
            if (el.TryGetProperty("supported_voices", out var voices) && voices.ValueKind == JsonValueKind.Array)
            {
                tags.AddRange(voices.EnumerateArray()
                    .Where(voice => voice.ValueKind == JsonValueKind.String)
                    .Select(voice => voice.GetString()?.Trim())
                    .Where(voice => !string.IsNullOrEmpty(voice))
                    .Distinct(StringComparer.Ordinal)
                    .Select(voice => $"voice:{voice}"));
            }

            tags.AddRange(OpenRouterSpeechOutputFormats.Select(format => $"output-format:{format}"));
        }
        else if (type == "transcription")
        {
            tags.AddRange(OpenRouterTranscriptionInputFormats.Select(format => $"input-format:{format}"));
        }

        var (unit, price) = ResolveAudioPriceUnit(el, type);
        if (unit is not null && price is not null)
            tags.Add($"price-per-{unit}:{price.Value.ToString(CultureInfo.InvariantCulture)}");

        return tags.Count == 0 ? null : tags;
    }

    // OpenRouter reuses prompt/completion for audio models: only pairs with a completion price are per token.
    private static (string? Unit, decimal? Price) ResolveAudioPriceUnit(JsonElement el, string type)
    {
        if ((type != "speech" && type != "transcription")
            || !el.TryGetProperty("pricing", out var pricing)
            || pricing.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        var prompt = ReadDecimal(pricing, "prompt") ?? 0m;
        var completion = ReadDecimal(pricing, "completion") ?? 0m;

        if (type == "speech")
        {
            if (prompt > 0 && completion > 0)
                return ("token", null);

            if (completion > 0)
                return ("output-second", completion);

            return prompt > 0 ? ("input-character", prompt) : (null, null);
        }

        if (completion > 0)
            return ("token", null);

        return prompt > 0 ? ("input-second", prompt) : (null, null);
    }

    private static ModelPricing? ReadPricing(JsonElement el, string type)
    {
        if (!el.TryGetProperty("pricing", out var pricing) ||
            pricing.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var (audioUnit, _) = ResolveAudioPriceUnit(el, type);
        if (audioUnit is not null && audioUnit != "token")
            return null;

        var input = ReadDecimal(pricing, "prompt");
        var output = ReadDecimal(pricing, "completion");

        var inputCacheRead = ReadNullableDecimal(pricing, "input_cache_read");
        var inputCacheWrite = ReadNullableDecimal(pricing, "input_cache_write");

        if (input is null && output is null && inputCacheRead is null && inputCacheWrite is null)
            return null;

        if (input < 0 || output < 0)
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
