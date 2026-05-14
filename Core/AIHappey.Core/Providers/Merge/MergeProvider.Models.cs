using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Merge;

public partial class MergeProvider
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

                var models = new List<Model>();
                models.AddRange(await ListPublicModelsAsync(ct));
                models.AddRange(await ListRoutingPolicyModelsAsync(ct));

                return models
                    .Where(model => !string.IsNullOrWhiteSpace(model.Id))
                    .GroupBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToArray();
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private async Task<IEnumerable<Model>> ListPublicModelsAsync(CancellationToken cancellationToken)
    {
        var models = new List<Model>();
        string? cursor = null;

        do
        {
            var url = "models?limit=100";
            if (!string.IsNullOrWhiteSpace(cursor))
                url += "&cursor=" + Uri.EscapeDataString(cursor);

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await _client.SendAsync(req, cancellationToken);

            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(cancellationToken);
                throw new Exception($"Merge API error while listing models: {err}");
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = doc.RootElement;

            if (root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in dataEl.EnumerateArray())
                {
                    var model = ToPublicModel(el);
                    if (!string.IsNullOrWhiteSpace(model?.Id))
                        models.Add(model);
                }
            }
            else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("model", out _))
            {
                var model = ToPublicModel(root);
                if (!string.IsNullOrWhiteSpace(model?.Id))
                    models.Add(model);
            }

            cursor = root.TryGetProperty("next_cursor", out var cursorEl) &&
                     cursorEl.ValueKind == JsonValueKind.String
                ? cursorEl.GetString()
                : null;

            var hasMore = root.TryGetProperty("has_more", out var hasMoreEl) &&
                          hasMoreEl.ValueKind is JsonValueKind.True;

            if (!hasMore)
                cursor = null;
        }
        while (!string.IsNullOrWhiteSpace(cursor));

        return models;
    }

    private async Task<IEnumerable<Model>> ListRoutingPolicyModelsAsync(CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "routing/policies");
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"Merge API error while listing routing policies: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!doc.RootElement.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array)
            return [];

        var models = new List<Model>();
        foreach (var policyEl in dataEl.EnumerateArray())
        {
            var model = ToRoutingPolicyModel(policyEl);
            if (!string.IsNullOrWhiteSpace(model?.Id))
                models.Add(model);
        }

        return models;
    }

    private Model? ToPublicModel(JsonElement el)
    {
        var mergeModelId = ReadString(el, "model");
        if (string.IsNullOrWhiteSpace(mergeModelId))
            return null;

        var provider = ReadString(el, "provider");
        var displayName = ReadString(el, "display_name") ?? mergeModelId;
        var status = ReadString(el, "availability_status");
        var vendorInfos = ReadVendorInfos(el);

        var tags = new SortedSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "merge-model",
            $"merge-model:{mergeModelId}"
        };

        if (!string.IsNullOrWhiteSpace(provider))
            tags.Add($"provider:{provider}");

        if (!string.IsNullOrWhiteSpace(status))
            tags.Add($"status:{status}");

        foreach (var vendor in vendorInfos)
        {
            tags.Add($"vendor:{vendor.Name}");

            if (!string.IsNullOrWhiteSpace(vendor.AvailabilityStatus))
                tags.Add($"vendor-status:{vendor.AvailabilityStatus}");

            foreach (var capability in vendor.CapabilityTags)
                tags.Add(capability);
        }

        var pricing = vendorInfos
            .Where(vendor => vendor.Pricing is not null)
            .OrderBy(vendor => vendor.Pricing!.Input + vendor.Pricing.Output)
            .Select(vendor => vendor.Pricing)
            .FirstOrDefault();

        return new Model
        {
            Id = mergeModelId.ToModelId(GetIdentifier()),
            Name = displayName,
            OwnedBy = provider ?? nameof(Merge),
            ContextWindow = vendorInfos.Select(vendor => vendor.ContextWindow).Where(value => value.HasValue).Max(),
            MaxTokens = vendorInfos.Select(vendor => vendor.MaxOutputTokens).Where(value => value.HasValue).Max(),
            Type = GuessMergeModelType(vendorInfos.SelectMany(vendor => vendor.InputCapabilities)),
            Tags = tags,
            Pricing = pricing
        };
    }

    private Model? ToRoutingPolicyModel(JsonElement policyEl)
    {
        var id = ReadString(policyEl, "id");
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var name = ReadString(policyEl, "name") ?? id;
        var strategy = ReadString(policyEl, "strategy");
        var description = ReadString(policyEl, "description");

        var tags = new SortedSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "routing-policy",
            $"routing-policy:{id}"
        };

        if (!string.IsNullOrWhiteSpace(strategy))
            tags.Add($"strategy:{strategy}");

        if (ReadBool(policyEl, "is_intelligent") == true)
            tags.Add("intelligent");

        if (ReadBool(policyEl, "is_active") == true)
            tags.Add("active");

        if (policyEl.TryGetProperty("providers", out var providersEl) && providersEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var providerEl in providersEl.EnumerateArray())
            {
                var provider = ReadString(providerEl, "provider");
                var model = ReadString(providerEl, "model");

                if (!string.IsNullOrWhiteSpace(provider))
                    tags.Add($"provider:{provider}");

                if (!string.IsNullOrWhiteSpace(model))
                    tags.Add($"member:{provider}/{model}".Replace("member:/", "member:"));
            }
        }

        return new Model
        {
            Id = (RoutingPolicyModelPrefix + id).ToModelId(GetIdentifier()),
            Name = name,
            OwnedBy = nameof(Merge),
            Type = "language",
            Description = description,
            Tags = tags
        };
    }

    private static List<MergeVendorInfo> ReadVendorInfos(JsonElement modelEl)
    {
        var vendors = new List<MergeVendorInfo>();

        if (!modelEl.TryGetProperty("vendors", out var vendorsEl) || vendorsEl.ValueKind != JsonValueKind.Object)
            return vendors;

        foreach (var vendorProp in vendorsEl.EnumerateObject())
        {
            var infoEl = vendorProp.Value;
            var inputCapabilities = ReadStringArray(infoEl, "capabilities", "input");
            var outputCapabilities = ReadStringArray(infoEl, "capabilities", "output");

            var capabilityTags = new List<string>();
            capabilityTags.AddRange(inputCapabilities.Select(input => $"input:{input}"));
            capabilityTags.AddRange(outputCapabilities.Select(output => $"output:{output}"));

            AddCapabilityFlag(capabilityTags, infoEl, "supports_tool_calling", "tools");
            AddCapabilityFlag(capabilityTags, infoEl, "supports_tool_choice", "tool-choice");
            AddCapabilityFlag(capabilityTags, infoEl, "supports_structured_outputs", "structured-outputs");
            AddCapabilityFlag(capabilityTags, infoEl, "streaming", "streaming");

            vendors.Add(new MergeVendorInfo(
                vendorProp.Name,
                ReadInt32(infoEl, "context_window"),
                ReadInt32(infoEl, "max_output_tokens"),
                ReadString(infoEl, "availability_status"),
                inputCapabilities,
                capabilityTags,
                ReadPricing(infoEl)));
        }

        return vendors;
    }

    private static ModelPricing? ReadPricing(JsonElement vendorInfoEl)
    {
        if (!vendorInfoEl.TryGetProperty("pricing", out var pricingEl) || pricingEl.ValueKind != JsonValueKind.Object)
            return null;

        var input = ReadDecimal(pricingEl, "input_per_million");
        var output = ReadDecimal(pricingEl, "output_per_million");

        if (input is not > 0 || output is not > 0)
            return null;

        return new ModelPricing
        {
            Input = input.Value / 1_000_000m,
            Output = output.Value / 1_000_000m
        };
    }

    private static void AddCapabilityFlag(List<string> tags, JsonElement infoEl, string propertyName, string tag)
    {
        if (infoEl.TryGetProperty("capabilities", out var capabilitiesEl) &&
            capabilitiesEl.ValueKind == JsonValueKind.Object &&
            capabilitiesEl.TryGetProperty(propertyName, out var valueEl) &&
            valueEl.ValueKind is JsonValueKind.True)
        {
            tags.Add(tag);
        }
    }

    private static string GuessMergeModelType(IEnumerable<string> inputCapabilities)
    {
        var capabilities = inputCapabilities.ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (capabilities.Contains("embedding"))
            return "embedding";

        if (capabilities.Contains("image"))
            return "language";

        if (capabilities.Contains("document"))
            return "language";

        return "language";
    }

    private static string[] ReadStringArray(JsonElement root, string parentName, string childName)
    {
        if (!root.TryGetProperty(parentName, out var parentEl) || parentEl.ValueKind != JsonValueKind.Object)
            return [];

        if (!parentEl.TryGetProperty(childName, out var childEl) || childEl.ValueKind != JsonValueKind.Array)
            return [];

        return [.. childEl.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .OfType<string>()
            .Where(item => !string.IsNullOrWhiteSpace(item))];
    }

    private static string? ReadString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt32(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static bool? ReadBool(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static decimal? ReadDecimal(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)
            ? number
            : null;

    private sealed record MergeVendorInfo(
        string Name,
        int? ContextWindow,
        int? MaxOutputTokens,
        string? AvailabilityStatus,
        IReadOnlyCollection<string> InputCapabilities,
        IReadOnlyCollection<string> CapabilityTags,
        ModelPricing? Pricing);
}
