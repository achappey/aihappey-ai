using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;

namespace AIHappey.Core.Providers.Softref;

public partial class SoftrefProvider
{
  public async Task<IEnumerable<Model>> ListModels(
        CancellationToken cancellationToken = default)
    {
        var cacheKey = this.GetCacheKey();

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                var models = new List<Model>();

                using var req = new HttpRequestMessage(
                    HttpMethod.Get,
                    "https://api.openrouter.com/v1/models?output_modalities=text");

                using var resp = await _client.SendAsync(req, ct);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(ct);
                    throw new Exception($"Softref API error from: {err}");
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(
                    stream,
                    cancellationToken: ct);

                if (!doc.RootElement.TryGetProperty("data", out var dataEl) ||
                    dataEl.ValueKind != JsonValueKind.Array)
                {
                    return [];
                }

                foreach (var el in dataEl.EnumerateArray())
                {
                    var model = CreateModel(el);

                    if (model is not null)
                        models.Add(model);
                }

                return models
                    .GroupBy(
                        model => model.Id,
                        StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToArray();
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private Model? CreateModel(JsonElement el)
    {
        var rawId = GetString(el, "id");

        if (string.IsNullOrWhiteSpace(rawId))
            return null;

        var outputModalities = GetArchitectureArray(
            el,
            "output_modalities");

        // Extra safety: only expose models capable of text output.
        if (!outputModalities.Contains("text"))
            return null;

        var name = GetString(el, "name") ?? rawId;

        var contextWindow =
            el.TryGetProperty("context_length", out var contextEl) &&
            contextEl.ValueKind == JsonValueKind.Number
                ? contextEl.GetInt32()
                : (int?)null;

        var created =
            el.TryGetProperty("created", out var createdEl) &&
            createdEl.ValueKind == JsonValueKind.Number
                ? createdEl.GetInt64()
                : (long?)null;

        return new Model
        {
            Id = rawId.ToModelId(GetIdentifier()),
            Name = name,
            ContextWindow = contextWindow,
            Description = GetString(el, "description"),
            OwnedBy = rawId
                .Split('/')
                .FirstOrDefault()?
                .TrimStart('~') ?? GetIdentifier(),
            Created = created,
            MaxTokens = ReadMaxTokens(el),
            Type = "language"
        };
    }

    private static string? GetString(
        JsonElement el,
        string propertyName)
    {
        return el.TryGetProperty(propertyName, out var prop) &&
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
            return new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
        }

        return prop
            .EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

  

    private static int? ReadMaxTokens(JsonElement el)
    {
        if (!el.TryGetProperty("top_provider", out var topProvider) ||
            topProvider.ValueKind != JsonValueKind.Object ||
            !topProvider.TryGetProperty(
                "max_completion_tokens",
                out var maxTokens) ||
            maxTokens.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return maxTokens.GetInt32();
    }

    private static decimal? ReadNullableDecimal(
        JsonElement el,
        string propertyName)
    {
        return ReadDecimal(el, propertyName);
    }

    private static decimal? ReadDecimal(
        JsonElement el,
        string propertyName)
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
