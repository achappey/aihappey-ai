using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Agent37;

public partial class Agent37Provider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key)) return [];

        return await _memoryCache.GetOrCreateAsync(
            this.GetCacheKey(key),
            async ct =>
            {
                ApplyModelAuthHeader();
                using var req = new HttpRequestMessage(HttpMethod.Get, "v1/instances");
                using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode)
                    throw new HttpRequestException($"Agent37 list instances failed: {await resp.Content.ReadAsStringAsync(ct)}",
                        null, resp.StatusCode);

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                var instances = TryGetProperty(document.RootElement, "data", out var data) && data.ValueKind == JsonValueKind.Array
                    ? data.EnumerateArray().Select(element => element.Clone()).ToList()
                    : [];
                var result = new List<Model>();
                foreach (var instance in instances)
                {
                    var instanceId = GetString(instance, "id");
                    if (string.IsNullOrWhiteSpace(instanceId)) continue;
                    var name = GetString(instance, "name") ?? instanceId;
                    var created = GetLong(instance, "created");
                    result.Add(new Model
                    {
                        Id = instanceId.ToModelId(GetIdentifier()), Name = name, Type = "chat", OwnedBy = nameof(Agent37),
                        Created = created, Description = $"Agent37 instance '{name}' using its configured default harness and model.",
                        Tags = ["agent", "instance", "default"]
                    });

                    foreach (var harness in Agent37Harnesses)
                    {
                        var catalog = await TryListAgent37InstanceModelsAsync(instanceId, harness, key, ct);
                        foreach (var model in catalog)
                        {
                            var upstreamId = GetString(model, "id");
                            if (string.IsNullOrWhiteSpace(upstreamId)) continue;
                            var slug = $"{instanceId}/{harness}/{upstreamId}";
                            result.Add(new Model
                            {
                                Id = slug.ToModelId(GetIdentifier()),
                                Name = $"{name} · {harness} · {GetString(model, "label") ?? upstreamId}",
                                Type = "chat", OwnedBy = GetString(model, "owned_by") ?? harness,
                                Created = GetLong(model, "created") ?? created,
                                Description = $"Agent37 instance '{name}', {harness} harness, model '{upstreamId}'.",
                                Tags = new[] { "agent", "instance", harness, GetString(model, "source"),
                                        GetBool(model, "is_default") == true ? "default" : null }
                                    .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToArray()
                            });
                        }
                    }
                }

                return result.GroupBy(model => model.Id, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
            },
            baseTtl: TimeSpan.FromHours(1), jitterMinutes: 30, cancellationToken: cancellationToken);
    }

    private async Task<List<JsonElement>> TryListAgent37InstanceModelsAsync(string instanceId, string harness,
        string key, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://{instanceId}.agent37.app/v1/models?agent={Uri.EscapeDataString(harness)}");
        request.Headers.Add("X-Agent37-Key", key);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) return [];
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return TryGetProperty(document.RootElement, "data", out var data) && data.ValueKind == JsonValueKind.Array
                ? data.EnumerateArray().Select(element => element.Clone()).ToList()
                : [];
        }
        catch (JsonException) { return []; }
    }

    private static long? GetLong(JsonElement element, string name)
        => TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var result)
            ? result : null;

    private static bool? GetBool(JsonElement element, string name)
        => TryGetProperty(element, name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : null;
}
