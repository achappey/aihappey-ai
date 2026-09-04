using System.Globalization;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Haimaker;

public partial class HaimakerProvider
{
    private const string RawModelsCacheSuffix = ":raw";
    private const string ResponsesMode = "responses";

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
        => (await GetModelsListingAsync(cancellationToken)).Models;

    private async Task<HaimakerModelsListing> GetModelsListingAsync(CancellationToken cancellationToken)
        => CreateModelsListing(await GetRawModelsAsync(cancellationToken));

    private async Task<IReadOnlyList<JsonElement>> GetRawModelsAsync(CancellationToken cancellationToken)
    {
        return await _memoryCache.GetOrCreateAsync(
            this.GetCacheKey() + RawModelsCacheSuffix,
            FetchRawModelsAsync,
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private async Task<IReadOnlyList<JsonElement>> FetchRawModelsAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "public/model_hub");
        using var response = await _client.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Haimaker model catalog failed ({(int)response.StatusCode}): {payload}");

        using var document = JsonDocument.Parse(payload);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return [];

        // Clone every entry so the cache retains the exact upstream object after
        // the JsonDocument containing the response is disposed.
        return [.. document.RootElement.EnumerateArray().Select(model => model.Clone())];
    }

    private HaimakerModelsListing CreateModelsListing(IReadOnlyList<JsonElement> rawModels)
    {
        var models = new List<Model>(rawModels.Count);
        var originals = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        foreach (var original in rawModels)
        {
            if (original.ValueKind != JsonValueKind.Object ||
                !original.TryGetProperty("model_group", out var groupElement) ||
                groupElement.ValueKind != JsonValueKind.String)
                continue;

            var modelGroup = groupElement.GetString();
            if (string.IsNullOrWhiteSpace(modelGroup))
                continue;

            var unifiedId = modelGroup.ToModelId(GetIdentifier());
            originals[modelGroup] = original;
            originals[unifiedId] = original;

            var providers = ReadStrings(original, "providers");
            var tags = BuildTags(original);
            var model = new Model
            {
                Id = unifiedId,
                Name = modelGroup,
                OwnedBy = providers.FirstOrDefault() ?? nameof(Haimaker),
                Type = "language",
                ContextWindow = ReadWholeNumber(original, "max_input_tokens"),
                MaxTokens = ReadWholeNumber(original, "max_output_tokens"),
                Tags = tags.Count == 0 ? null : tags,
                Pricing = ReadPricing(original)
            };

            models.Add(model);
        }

        return new HaimakerModelsListing(models, originals);
    }

    private async Task<bool> UsesResponsesEndpointAsync(
        string? requestModel,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requestModel))
            return false;

        try
        {
            var listing = await GetModelsListingAsync(cancellationToken);
            foreach (var candidate in GetModelLookupCandidates(requestModel))
            {
                if (!listing.OriginalByModelId.TryGetValue(candidate, out var original))
                    continue;

                return UsesResponsesMode(original);
            }
        }
        catch
        {
            // Model lookup must never prevent the documented Chat fallback.
        }

        return false;
    }

    public static bool UsesResponsesMode(JsonElement? originalModel)
    {
        return originalModel is { ValueKind: JsonValueKind.Object } &&
            originalModel.Value.TryGetProperty("mode", out var modeElement) &&
            modeElement.ValueKind == JsonValueKind.String &&
            string.Equals(modeElement.GetString(), ResponsesMode, StringComparison.OrdinalIgnoreCase);
    }

    private IEnumerable<string> GetModelLookupCandidates(string requestModel)
    {
        yield return requestModel;

        var providerPrefix = GetIdentifier() + "/";
        if (requestModel.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            yield return requestModel[providerPrefix.Length..];
    }

    private static List<string> BuildTags(JsonElement model)
    {
        var tags = new List<string>();
        AddCapabilityTag(model, "supports_vision", "vision", tags);
        AddCapabilityTag(model, "supports_web_search", "web-search", tags);
        AddCapabilityTag(model, "supports_url_context", "url-context", tags);
        AddCapabilityTag(model, "supports_reasoning", "reasoning", tags);
        AddCapabilityTag(model, "supports_function_calling", "function-calling", tags);
        AddCapabilityTag(model, "supports_parallel_function_calling", "parallel-function-calling", tags);
        return tags;
    }

    private static void AddCapabilityTag(
        JsonElement model,
        string propertyName,
        string tag,
        ICollection<string> tags)
    {
        if (model.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.True)
            tags.Add(tag);
    }

    private static IReadOnlyList<string> ReadStrings(JsonElement model, string propertyName)
    {
        if (!model.TryGetProperty(propertyName, out var values) || values.ValueKind != JsonValueKind.Array)
            return [];

        return [.. values.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)];
    }

    private static int? ReadWholeNumber(JsonElement model, string propertyName)
    {
        if (!model.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Number)
            return null;

        if (value.TryGetInt32(out var integer))
            return integer;

        return value.TryGetDouble(out var number) && number >= 0 && number <= int.MaxValue
            ? Convert.ToInt32(number)
            : null;
    }

    private static ModelPricing? ReadPricing(JsonElement model)
    {
        var input = ReadDecimal(model, "input_cost_per_token");
        var output = ReadDecimal(model, "output_cost_per_token");
        if (input is null && output is null)
            return null;

        return new ModelPricing
        {
            Input = input ?? 0,
            Output = output ?? 0
        };
    }

    private static decimal? ReadDecimal(JsonElement model, string propertyName)
    {
        if (!model.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;

        return decimal.TryParse(
            value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : null;
    }

    private sealed record HaimakerModelsListing(
        IReadOnlyList<Model> Models,
        IReadOnlyDictionary<string, JsonElement> OriginalByModelId);
}
