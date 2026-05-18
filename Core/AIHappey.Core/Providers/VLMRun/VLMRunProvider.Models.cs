using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;

namespace AIHappey.Core.Providers.VLMRun;

public partial class VLMRunProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            return [];

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
                    throw new Exception($"VLMRun API error: {err}");
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                var models = new List<Model>();

                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (!el.TryGetProperty("model", out var modelEl) ||
                        !el.TryGetProperty("domain", out var domainEl))
                        continue;

                    var modelName = modelEl.GetString();
                    var domain = domainEl.GetString();

                    if (string.IsNullOrWhiteSpace(modelName) || string.IsNullOrWhiteSpace(domain))
                        continue;

                    var id = $"{modelName}/{domain}";

                    models.Add(new Model
                    {
                        Id = id.ToModelId(GetIdentifier()),
                        Name = id,
                        OwnedBy = modelName
                    });
                }

                models.AddRange(GetIdentifier().GetModels());
                await AddAgentModelsAsync(models, cancellationToken);

                return models.DistinctBy(model => model.Id, StringComparer.OrdinalIgnoreCase).ToList();
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private async Task AddAgentModelsAsync(List<Model> models, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, VLMRunAgentListEndpoint);
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue(System.Net.Mime.MediaTypeNames.Application.Json));

        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
            return;

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return;

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var name = TryGetVLMRunAgentString(el, "name");
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var created = TryGetVLMRunAgentDateTime(el, "created_at")?.ToUnixTimeSeconds();
            var status = TryGetVLMRunAgentString(el, "status");
            var id = TryGetVLMRunAgentString(el, "id");

            models.Add(new Model
            {
                Id = $"{VLMRunAgentModelPrefix}{name}".ToModelId(GetIdentifier()),
                Name = $"{VLMRunAgentModelPrefix}{name}",
                OwnedBy = nameof(VLMRun),
                Type = "language",
                Description = TryGetVLMRunAgentString(el, "description") ?? $"VLMRun agent shortcut for {name}.",
                Created = created,
                Tags = BuildVLMRunAgentModelTags(status, id)
            });
        }
    }

    private static string[] BuildVLMRunAgentModelTags(string? status, string? id)
    {
        var tags = new List<string> { "agent", "non-streaming", "synthetic-stream" };

        if (!string.IsNullOrWhiteSpace(status))
            tags.Add($"status:{status}");

        if (!string.IsNullOrWhiteSpace(id))
            tags.Add($"agent-id:{id}");

        return [.. tags];
    }

    private static DateTimeOffset? TryGetVLMRunAgentDateTime(JsonElement element, string propertyName)
    {
        var value = TryGetVLMRunAgentString(element, propertyName);
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }
}
