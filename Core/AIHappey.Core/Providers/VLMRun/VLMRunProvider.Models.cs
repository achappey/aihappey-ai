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

                var models = GetIdentifier().GetModels().ToList();

                using var req = new HttpRequestMessage(HttpMethod.Get, VLMRunGatewayModelsEndpoint);
                using var resp = await _client.SendAsync(req, ct);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(ct);
                    throw new Exception($"VLM Run Gateway models error: {err}");
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                if (doc.RootElement.TryGetProperty("data", out var data)
                    && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in data.EnumerateArray())
                    {
                        var id = el.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                        var task = el.TryGetProperty("task", out var taskElement) ? taskElement.GetString() : "chat";

                        if (string.IsNullOrWhiteSpace(id)
                            || task is not ("chat" or "transcribe"))
                            continue;

                        var methods = el.TryGetProperty("methods", out var methodsElement)
                            && methodsElement.ValueKind == JsonValueKind.Array
                            ? methodsElement.EnumerateArray()
                                .Where(item => item.ValueKind == JsonValueKind.String)
                                .Select(item => $"method:{item.GetString()}")
                            : [];

                        var inputTypes = el.TryGetProperty("capabilities", out var capabilities)
                            && capabilities.ValueKind == JsonValueKind.Object
                            && capabilities.TryGetProperty("supported_input_types", out var inputTypesElement)
                            && inputTypesElement.ValueKind == JsonValueKind.Array
                            ? inputTypesElement.EnumerateArray()
                                .Where(item => item.ValueKind == JsonValueKind.String)
                                .Select(item => $"input:{item.GetString()}")
                            : [];

                        models.Add(new Model
                        {
                            Id = id.ToModelId(GetIdentifier()),
                            Name = id,
                            OwnedBy = el.TryGetProperty("owned_by", out var ownerElement)
                                ? ownerElement.GetString() ?? nameof(VLMRun)
                                : nameof(VLMRun),
                            Created = el.TryGetProperty("created", out var createdElement)
                                && createdElement.TryGetInt64(out var created) ? created : null,
                            Type = task == "transcribe" ? "transcription" : "language",
                            Tags = methods.Concat(inputTypes).Append("gateway").ToArray()
                        });
                    }
                }

                await AddAgentModelsAsync(models, ct);

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
        var tags = new List<string> { "agent" };

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
