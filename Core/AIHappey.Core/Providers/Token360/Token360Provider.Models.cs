using AIHappey.Core.AI;
using AIHappey.Core.Models;
using System.Globalization;
using System.Text.Json;

namespace AIHappey.Core.Providers.Token360;

public partial class Token360Provider
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
                    "https://www.token360.ai/api/backend/public/models?size=2000");

                using var resp = await _client.SendAsync(req, ct);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(ct);
                    throw new Exception($"Token360 API error {(int)resp.StatusCode}: {err}");
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                var root = doc.RootElement;
                var models = new List<Model>();

                IEnumerable<JsonElement> arr = Enumerable.Empty<JsonElement>();

                if (root.TryGetProperty("data", out var dataEl) &&
                    dataEl.ValueKind == JsonValueKind.Object &&
                    dataEl.TryGetProperty("list", out var listEl) &&
                    listEl.ValueKind == JsonValueKind.Array)
                {
                    arr = listEl.EnumerateArray();
                }
                else if (root.ValueKind == JsonValueKind.Array)
                {
                    arr = root.EnumerateArray();
                }

                foreach (var el in arr)
                {
                    var name = GetString(el, "name");

                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    var model = new Model
                    {
                        Id = name.ToModelId(GetIdentifier()),
                        Name = GetString(el, "displayName") ?? name,
                        OwnedBy = GetPublisher(el),
                        Type = MapModelType(el),
                        Description = GetString(el, "descriptionEn")
                            ?? GetString(el, "description")
                            ?? GetString(el, "descriptionZh"),
                        ContextWindow = GetIntAny(
                            el,
                            "context_length",
                            "contextWindow",
                            "context_window",
                            "max_context_length",
                            "maxContextLength")
                    };

                    var pricing = TryReadTokenPricing(el);
                    if (pricing is not null)
                        model.Pricing = pricing;

                    models.Add(model);
                }

                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private static string MapModelType(JsonElement el)
    {
        var raw = GetString(el, "modelType")?.Trim().ToUpperInvariant();

        return raw switch
        {
            "IMAGE_GENERATION" => "image",
            "VIDEO_GENERATION" => "video",
            "TEXT_GENERATION" or "CHAT" or "LLM" or "LANGUAGE" => "language",
            "EMBEDDING" or "TEXT_EMBEDDING" => "embedding",
            "TRANSCRIPTION" or "SPEECH_TO_TEXT" => "transcription",
            "TEXT_TO_SPEECH" or "AUDIO_GENERATION" => "speech",
            _ => InferTypeFromOutputs(el)
        };
    }

    private static string InferTypeFromOutputs(JsonElement el)
    {
        foreach (var value in GetStringArray(el, "modelOutputTypes"))
        {
            var normalized = value.Trim().ToLowerInvariant();

            if (normalized is "image")
                return "image";

            if (normalized is "video")
                return "video";

            if (normalized is "audio")
                return "speech";

            if (normalized is "text")
                return "language";
        }

        return "language";
    }

    private static string GetPublisher(JsonElement el)
    {
        if (el.TryGetProperty("publisher", out var publisherEl) &&
            publisherEl.ValueKind == JsonValueKind.Object)
        {
            return GetString(publisherEl, "displayName")
                ?? GetString(publisherEl, "displayNameEn")
                ?? GetString(publisherEl, "name")
                ?? "";
        }

        return "";
    }

    private static ModelPricing? TryReadTokenPricing(JsonElement el)
    {
        if (!el.TryGetProperty("displayPricing", out var pricingEl) ||
            pricingEl.ValueKind != JsonValueKind.Object)
            return null;

        var input = GetDecimal(pricingEl, "inputPrice");
        var output = GetDecimal(pricingEl, "outputPrice");

        if (input is null || output is null)
            return null;

        if (input.Value == 0m || output.Value == 0m)
            return null;

        return new ModelPricing
        {
            Input = input.Value,
            Output = output.Value
        };
    }

    private static string? GetString(JsonElement el, string propertyName)
    {
        if (!el.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static int? GetIntAny(JsonElement el, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!el.TryGetProperty(propertyName, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i))
                return i;

            if (value.ValueKind == JsonValueKind.String &&
                int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out i))
                return i;
        }

        return null;
    }

    private static decimal? GetDecimal(JsonElement el, string propertyName)
    {
        if (!el.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var d) => d,
            JsonValueKind.String when decimal.TryParse(
                value.GetString(),
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out var d) => d,
            _ => null
        };
    }

    private static IEnumerable<string> GetStringArray(JsonElement el, string propertyName)
    {
        if (!el.TryGetProperty(propertyName, out var arr) ||
            arr.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var value = item.GetString();

                if (!string.IsNullOrWhiteSpace(value))
                    yield return value;
            }
        }
    }
}