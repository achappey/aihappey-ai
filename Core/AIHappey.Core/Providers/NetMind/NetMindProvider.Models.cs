using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;
using System.Reflection;

namespace AIHappey.Core.Providers.NetMind;

public partial class NetMindProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var cacheKey = this.GetCacheKey();

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, "https://platform-api.netmind.ai/inference/queryModelList");
                var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
                var assemblyName = assembly.GetName();
                var userAgent = $"{assemblyName.Name}/{assemblyName.Version}";

                req.Headers.TryAddWithoutValidation("User-Agent", userAgent);

                using var resp = await _client.SendAsync(req, cancellationToken);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(cancellationToken);
                    throw new Exception($"NetMind API error: {err}");
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                var models = new List<Model>();
                var root = doc.RootElement;

                if (!root.TryGetProperty("data", out var dataEl) ||
                    dataEl.ValueKind != JsonValueKind.Object ||
                    !dataEl.TryGetProperty("models", out var modelsEl) ||
                    modelsEl.ValueKind != JsonValueKind.Array)
                {
                    return models;
                }

                foreach (var el in modelsEl.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Object)
                        continue;

                    var modelName = GetString(el, "model_name");

                    if (string.IsNullOrWhiteSpace(modelName))
                        continue;

                    var exhibitionConfig =
                        el.TryGetProperty("model_exhibition_config", out var configEl) &&
                        configEl.ValueKind == JsonValueKind.Object
                            ? configEl
                            : default;

                    var model = new Model
                    {
                        Id = modelName.ToModelId(GetIdentifier()),
                        Name = GetString(exhibitionConfig, "title") ?? modelName,
                        OwnedBy = GetString(exhibitionConfig, "model_owner") ?? "",
                        Description =
                            GetString(el, "inference_model_description") ??
                            GetEnglishDescription(exhibitionConfig),
                        ContextWindow = ParseContextWindow(
                            GetString(exhibitionConfig, "context")),
                        Type = MapModelType(GetString(el, "model_type"))
                            ?? modelName.GuessModelType()
                    };

                    if (el.TryGetProperty("billing_metadata", out var billingEl) &&
                        billingEl.ValueKind == JsonValueKind.Object)
                    {
                        model.Pricing = ParsePricing(billingEl);
                    }

                    models.Add(model);
                }

                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static decimal? GetDecimal(
        JsonElement element,
        string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number &&
            value.TryGetDecimal(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String &&
            decimal.TryParse(
                value.GetString(),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out number))
        {
            return number;
        }

        return null;
    }

    private static string? GetEnglishDescription(JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object ||
            !config.TryGetProperty("description_i18n", out var descriptions) ||
            descriptions.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return GetString(descriptions, "en");
    }

    private static int? ParseContextWindow(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        value = value.Trim().ToUpperInvariant();

        var multiplier = 1;

        if (value.EndsWith('K'))
        {
            multiplier = 1_000;
            value = value[..^1];
        }
        else if (value.EndsWith('M'))
        {
            multiplier = 1_000_000;
            value = value[..^1];
        }

        return decimal.TryParse(
            value,
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var parsed)
                ? checked((int)(parsed * multiplier))
                : null;
    }

    private static ModelPricing? ParsePricing(JsonElement billing)
    {
        var inputPerMillion =
            GetDecimal(billing, "usd_input_token_price_unit");

        var outputPerMillion =
            GetDecimal(billing, "usd_output_token_price_unit");

        var cacheReadPerMillion =
            GetDecimal(billing, "usd_cache_read_token_price_unit");

        var imagePrice =
            GetDecimal(billing, "usd_price_unit");

        var pricing = new ModelPricing
        {
            Input = ToPerToken(inputPerMillion) ?? 0,
            Output = ToPerToken(outputPerMillion) ?? 0,
            InputCacheRead = ToPerToken(cacheReadPerMillion)
        };

        return pricing.Input is not 0 &&
               pricing.Output is not 0
            ? pricing
            : null;
    }

    private static decimal? ToPerToken(decimal? perMillion)
    {
        return perMillion is > 0
            ? perMillion.Value / 1_000_000m
            : null;
    }

    private static bool IsImageBilling(JsonElement billing)
    {
        if (GetString(billing, "display_unit")?
                .Contains("image", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        if (billing.TryGetProperty("billing_display", out var display) &&
            display.ValueKind == JsonValueKind.Object)
        {
            return GetString(display, "billing_type")?
                .Contains("image", StringComparison.OrdinalIgnoreCase) == true;
        }

        return false;
    }
    private static string? MapModelType(string? modelType)
    {
        return modelType?.ToLowerInvariant() switch
        {
            "chat" => "language",
            "image" => "image",
            "audio" => "speech",
            "video" => "video",
            _ => null
        };
    }
}