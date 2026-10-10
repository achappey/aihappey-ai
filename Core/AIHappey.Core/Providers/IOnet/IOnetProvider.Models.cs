using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Net;
using System.Net.Http.Headers;

namespace AIHappey.Core.Providers.IOnet;

public partial class IOnetProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {

        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key)) return [];
        var cacheKey = this.GetCacheKey(key);

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using var resp = await _client.SendAsync(req, ct);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(ct);
                    throw new Exception($"IOnet API error: {err}");
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                var models = new List<Model>();
                var root = doc.RootElement;

                var arr = root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array
                        ? dataEl.EnumerateArray()
                        : Enumerable.Empty<JsonElement>();

                foreach (var el in arr)
                {
                    Model model = new();

                    if (el.TryGetProperty("id", out var idEl))
                    {
                        model.Id = idEl.GetString()?.ToModelId(GetIdentifier()) ?? "";
                        model.Name = idEl.GetString() ?? "";
                    }

                    if (el.TryGetProperty("context_window", out var contextLengthEl))
                        model.ContextWindow = contextLengthEl.GetInt32();

                    if (el.TryGetProperty("owned_by", out var orgEl))
                        model.OwnedBy = orgEl.GetString() ?? "";

                    if (el.TryGetProperty("created", out var createdEl) && createdEl.ValueKind == JsonValueKind.Number)
                        model.Created = createdEl.GetInt64();

                    if (el.TryGetProperty("name", out var nameEl))
                        model.Name = nameEl.GetString() ?? model.Id;

                    if (el.TryGetProperty("input_token_price", out var inEl) &&
                        el.TryGetProperty("output_token_price", out var outEl))
                    {
                        var inputPrice = (decimal)inEl.GetDouble();
                        var outputPrice = (decimal)outEl.GetDouble();

                        if (inputPrice > 0 && outputPrice > 0)
                        {
                            model.Pricing = new ModelPricing
                            {
                                Input = inputPrice,
                                Output = outputPrice,
                                InputCacheWrite = el.TryGetProperty("cache_write_token_price", out var cw)
                                    ? (decimal)cw.GetDouble()
                                    : null,
                                InputCacheRead = el.TryGetProperty("cache_read_token_price", out var cr)
                                    ? (decimal)cr.GetDouble()
                                    : null
                            };
                        }
                    }

                    if (!string.IsNullOrEmpty(model.Id))
                        models.Add(model);
                }

                // Agents are a separate beta API, not chat-completion models.
                using var agentRequest = new HttpRequestMessage(HttpMethod.Get, "v1/agents");
                agentRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using var agentResponse = await _client.SendAsync(agentRequest, ct);
                if (agentResponse.IsSuccessStatusCode)
                {
                    await using var agentStream = await agentResponse.Content.ReadAsStreamAsync(ct);
                    using var agentsDoc = await JsonDocument.ParseAsync(agentStream, cancellationToken: ct);
                    if (agentsDoc.RootElement.TryGetProperty("agents", out var agents) && agents.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var agent in agents.EnumerateObject())
                        {
                            if (string.IsNullOrWhiteSpace(agent.Name) || agent.Name.Contains('/') || agent.Value.ValueKind != JsonValueKind.Object)
                                continue;
                            var specification = agent.Value;
                            var metadata = specification.TryGetProperty("metadata", out var info) && info.ValueKind == JsonValueKind.Object
                                ? info : default;
                            var tags = new List<string> { "agent" };
                            if (metadata.ValueKind == JsonValueKind.Object && metadata.TryGetProperty("tags", out var agentTags)
                                && agentTags.ValueKind == JsonValueKind.Array)
                                tags.AddRange(agentTags.EnumerateArray().Where(tag => tag.ValueKind == JsonValueKind.String)
                                    .Select(tag => tag.GetString()!).Where(tag => !string.IsNullOrWhiteSpace(tag)));
                            var id = $"agents/{agent.Name}".ToModelId(GetIdentifier());
                            if (models.Any(model => model.Id == id)) continue;
                            models.Add(new Model
                            {
                                Id = id,
                                Name = specification.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                                    ? name.GetString() ?? agent.Name : agent.Name,
                                Description = specification.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String
                                    ? description.GetString() : null,
                                OwnedBy = "io.net",
                                Type = "language",
                                Tags = ["agent"]
                            });
                        }
                    }
                }
                else if (agentResponse.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.Forbidden))
                {
                    var error = await agentResponse.Content.ReadAsStringAsync(ct);
                    throw new HttpRequestException($"IOnet agents API error ({(int)agentResponse.StatusCode}): {error}", null, agentResponse.StatusCode);
                }

                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);

    }
}
