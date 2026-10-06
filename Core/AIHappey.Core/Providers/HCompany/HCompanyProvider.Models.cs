using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Diagnostics;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.HCompany;

public partial class HCompanyProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key)) return [];
        return await _memoryCache.GetOrCreateAsync(this.GetCacheKey(key), async ct =>
        {
            var models = new List<Model>();
            Exception? modelError = null;
            Exception? agentError = null;
            try
            {
                var root = await SendJsonAsync(_models, HttpMethod.Get, "v1/models", null, ct, capture: false);
                if (Property(root, "data") is not { ValueKind: JsonValueKind.Array })
                    throw new InvalidOperationException("HCompany models response is missing data.");
                foreach (var item in Array(root, "data"))
                {
                    var id = Text(item, "id");
                    if (string.IsNullOrWhiteSpace(id) || Property(item, "is_active")?.ValueKind == JsonValueKind.False) continue;
                    var price = Property(item, "pricing") ?? default;
                    models.Add(new Model
                    {
                        Id = id.ToModelId(GetIdentifier()), Name = Text(item, "name") ?? id,
                        OwnedBy = "HCompany", Type = "language",
                        ContextWindow = Int(item, "context_length") is > 0 and var context ? context : null,
                        MaxTokens = Int(item, "max_output_length") is > 0 and var output ? output : null,
                        Tags = Array(item, "input_modalities").Concat(Array(item, "supported_features"))
                            .Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).Distinct().ToArray(),
                        Pricing = Price(price, "prompt") is { } input && Price(price, "completion") is { } completion
                            ? new ModelPricing { Input = input, Output = completion, InputCacheRead = Price(price, "input_cache_read") } : null
                    });
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException) { modelError = ex; }
            try
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (var page = 1; ; page++)
                {
                    var root = await SendJsonAsync(_agents, HttpMethod.Get, $"agents?page={page}&size=1000&sort=agent_name", null, ct, capture: false);
                    if (Property(root, "items") is not { ValueKind: JsonValueKind.Array })
                        throw new InvalidOperationException("HCompany agents response is missing items.");
                    var items = Array(root, "items").ToList();
                    var added = 0;
                    foreach (var item in items)
                    {
                        var name = Text(item, "name");
                        if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;
                        added++;
                        models.Add(new Model
                        {
                            Id = ("agent/" + name).ToModelId(GetIdentifier()), Name = name,
                            Description = Text(item, "description"), OwnedBy = "HCompany", Type = "language", Tags = ["agent"]
                        });
                    }
                    if (added == 0 || seen.Count >= Int(root, "total") && Property(root, "total").HasValue || items.Count < 1000) break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException) { agentError = ex; }
            if (modelError is not null && agentError is not null)
                throw new AggregateException("Neither HCompany catalog could be retrieved.", modelError, agentError);
            return models.DistinctBy(m => m.Id).ToList();
        }, baseTtl: TimeSpan.FromHours(1), jitterMinutes: 15, cancellationToken: cancellationToken);
    }

    private static decimal? Price(JsonElement root, string name)
        => Property(root, name) is { } value && decimal.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            && result >= 0 ? result : null;

    private async Task<JsonElement> SendJsonAsync(HttpClient client, HttpMethod method, string path, object? body,
        CancellationToken ct, Dictionary<string, string>? headers = null, bool capture = true)
    {
        using var request = new HttpRequestMessage(method, path);
        if (headers is not null)
            foreach (var (name, value) in headers)
                if (!name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) && !name.Equals("Host", StringComparison.OrdinalIgnoreCase))
                    request.Headers.TryAddWithoutValidation(name, value);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", RequireKey());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        var operation = Guid.NewGuid().ToString("N");
        if (capture && _debug.Enabled && request.Content is not null)
            await _debug.EmitAsync(GetIdentifier(), path, operation, "request-body",
                ProviderDebugPayload.FromText(await request.Content.ReadAsStringAsync(ct), "application/json"), ct);
        using var response = await client.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (capture && _debug.Enabled)
            await _debug.EmitAsync(GetIdentifier(), path, operation, "response-body",
                ProviderDebugPayload.FromText(text, response.Content.Headers.ContentType?.MediaType ?? "application/json"), ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HCompany API ({(int)response.StatusCode}) at {path}: {text}", null, response.StatusCode);
        if (response.StatusCode == HttpStatusCode.NoContent || string.IsNullOrWhiteSpace(text)) return default;
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}
