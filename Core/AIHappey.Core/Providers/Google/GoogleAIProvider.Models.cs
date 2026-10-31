using AIHappey.Core.Models;
using AIHappey.Core.AI;
using System.Globalization;
using System.Text.Json;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.Google;

public partial class GoogleAIProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            return await Task.FromResult<IEnumerable<Model>>([]);

        var cacheKey = this.GetCacheKey(key);

        var result = await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                var models = await ListGoogleModels(ct);

                var rawModels = models
                    .Where(a => !IsImagenModel(a.Name))
                    .Where(a => !IsDeprecatedGoogleOmniModel(a.Name))
                    .Select(a =>
                    {
                        var id = a.Name?.Split("/").LastOrDefault() ?? string.Empty;

                        GoogleAIModels.ModelCreatedAt.TryGetValue(id, out var createdAt);

                        return new Model()
                        {
                            Name = a.DisplayName!,
                            OwnedBy = Google,
                            Description = id,
                            Id = id.ToModelId(GetIdentifier()),
                            Type = id.GuessModelType(),
                            Tags = RealtimeCapabilities.Tags(GetIdentifier(), id,
                                id.Contains("-live") ? ["real-time"] : null),
                            Created = createdAt != default ? createdAt.ToUnixTimeSeconds() : null
                        };
                    })
                    .ToList();

                rawModels.AddRange(BuildGoogleSpeechVoiceShortcutModels([.. rawModels], GetIdentifier()));

                var omniModels = rawModels
                    .Where(a => a.Id.Contains("omni", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var omniModel in omniModels ?? [])
                {
                    rawModels.Add(new Model()
                    {
                        Name = omniModel.Name + " Video",
                        Id = omniModel.Id,
                        OwnedBy = omniModel.OwnedBy,
                        Description = omniModel.Description,
                        Created = omniModel.Created,
                        Type = "video"
                    });
                }

                var lyriaModels = rawModels
                 .Where(a => a.Id.Contains("lyria", StringComparison.OrdinalIgnoreCase))
                 .ToList();

                foreach (var lyriaModel in lyriaModels ?? [])
                {
                    rawModels.Add(new Model()
                    {
                        Name = lyriaModel.Name,
                        Id = lyriaModel.Id,
                        OwnedBy = lyriaModel.OwnedBy,
                        Description = lyriaModel.Description,
                        Created = lyriaModel.Created,
                        Type = "speech"
                    });
                }

                rawModels.AddRange(await ListGoogleCustomAgents(ct));

                return rawModels
                    .WithPricing(GetIdentifier());

            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);

        // Also filter cached snapshots so the deprecated entry disappears
        // immediately instead of waiting for the model-list cache to expire.
        return result.Where(model => !IsDeprecatedGoogleOmniModel(model.Id)).ToList();
    }

    private async Task<List<GoogleModelInfo>> ListGoogleModels(CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        var models = new List<GoogleModelInfo>();
        var seenPageTokens = new HashSet<string>(StringComparer.Ordinal);
        string? pageToken = null;

        do
        {
            var url = "v1beta/models?pageSize=1000";
            if (!string.IsNullOrEmpty(pageToken))
                url += "&pageToken=" + Uri.EscapeDataString(pageToken);

            using var response = await _client.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;

            if (root.TryGetProperty("models", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        continue;

                    models.Add(new GoogleModelInfo(
                        GetGoogleModelString(item, "name"),
                        GetGoogleModelString(item, "displayName")));
                }
            }

            pageToken = root.TryGetProperty("nextPageToken", out var nextToken)
                && nextToken.ValueKind == JsonValueKind.String ? nextToken.GetString() : null;
        } while (!string.IsNullOrEmpty(pageToken) && seenPageTokens.Add(pageToken));

        return models;
    }

    private static string? GetGoogleModelString(JsonElement model, string propertyName)
        => model.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed record GoogleModelInfo(string? Name, string? DisplayName);

    private async Task<List<Model>> ListGoogleCustomAgents(CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        var agents = new List<Model>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        string? pageToken = null;

        do
        {
            var url = "v1beta/agents?page_size=1000";
            if (!string.IsNullOrEmpty(pageToken))
                url += "&page_token=" + Uri.EscapeDataString(pageToken);

            using var response = await _client.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;

            // The beta reference describes `agents`, while its list example uses `data`.
            if ((root.TryGetProperty("agents", out var items) || root.TryGetProperty("data", out items))
                && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object
                        || !item.TryGetProperty("id", out var idElement)
                        || idElement.ValueKind != JsonValueKind.String)
                        continue;

                    var id = idElement.GetString()?.Trim();
                    if (string.IsNullOrWhiteSpace(id) || id.Contains('/') || !seenIds.Add(id))
                        continue;

                    var name = item.TryGetProperty("display_name", out var displayName)
                        && displayName.ValueKind == JsonValueKind.String
                        ? displayName.GetString() : null;
                    var description = item.TryGetProperty("description", out var descriptionElement)
                        && descriptionElement.ValueKind == JsonValueKind.String
                        ? descriptionElement.GetString() : null;
                    var created = item.TryGetProperty("created", out var createdElement)
                        && createdElement.ValueKind == JsonValueKind.String
                        && DateTimeOffset.TryParse(createdElement.GetString(), CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal, out var createdAt)
                        ? createdAt.ToUnixTimeSeconds() : (long?)null;

                    agents.Add(new Model
                    {
                        Id = $"{GetIdentifier()}/agents/{id}",
                        Name = string.IsNullOrWhiteSpace(name) ? id : name,
                        Description = description,
                        OwnedBy = Google,
                        Type = "language",
                        Tags = ["agent"],
                        Created = created
                    });
                }
            }

            pageToken = root.TryGetProperty("next_page_token", out var nextToken)
                && nextToken.ValueKind == JsonValueKind.String ? nextToken.GetString() : null;
        } while (!string.IsNullOrEmpty(pageToken) && seenTokens.Add(pageToken));

        return agents;
    }

    private static bool IsDeprecatedGoogleOmniModel(string? name)
        => string.Equals(name?.Split('/').LastOrDefault(), "gemini-omni-flash-preview", StringComparison.OrdinalIgnoreCase);

    private static bool IsImagenModel(string? name)
    {
        var modelId = name?.Split('/').LastOrDefault();
        return modelId?.StartsWith("imagen-", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static IEnumerable<Model> BuildGoogleSpeechVoiceShortcutModels(IEnumerable<Model> models, string providerId)
    {
        var existingIds = new HashSet<string>(
            models.Select(model => model.Id),
            StringComparer.OrdinalIgnoreCase);

        foreach (var baseModel in models)
        {
            var baseModelId = GetLocalGoogleModelId(baseModel, providerId);
            if (string.IsNullOrWhiteSpace(baseModelId)
                || !baseModelId.Contains("tts", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var voice in GoogleSpeechVoices)
            {
                var shortcutId = $"{baseModelId}/{voice.Name}";
                var providerModelId = shortcutId.ToModelId(providerId);
                if (!existingIds.Add(providerModelId))
                    continue;

                yield return new Model
                {
                    Id = providerModelId,
                    Name = shortcutId,
                    OwnedBy = Google,
                    Type = "speech",
                    Created = baseModel.Created,
                    Description = $"Google Gemini text-to-speech model '{baseModelId}' with preset voice '{voice.Name}' ({voice.Style}).",
                    Tags = ["voice"]
                };
            }
        }
    }

    private static string GetLocalGoogleModelId(Model model, string providerId)
    {
        var id = model.Id ?? string.Empty;
        var providerPrefix = providerId + "/";
        if (id.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            return id[providerPrefix.Length..];

        return id;
    }
}
