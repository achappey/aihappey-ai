using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Requesty;

public partial class RequestyProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var cacheKey = this.GetCacheKey();

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models/all");
                using var resp = await _client.SendAsync(req, cancellationToken);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(cancellationToken);
                    throw new Exception($"Requesty API error: {err}");
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(
                    stream,
                    cancellationToken: cancellationToken);

                var models = new List<Model>();
                var root = doc.RootElement;

                var arr = root.TryGetProperty("data", out var dataEl) &&
                          dataEl.ValueKind == JsonValueKind.Array
                    ? dataEl.EnumerateArray()
                    : Enumerable.Empty<JsonElement>();

                foreach (var el in arr)
                {
                    if (!el.TryGetProperty("id", out var idEl))
                        continue;

                    var id = idEl.GetString();

                    if (string.IsNullOrWhiteSpace(id))
                        continue;

                    var api = el.TryGetProperty("api", out var apiEl)
                        ? apiEl.GetString()
                        : null;

                    var model = new Model
                    {
                        Id = id.ToModelId(GetIdentifier()),
                        Name = id,
                        Type = api switch
                        {
                            "chat" => "language",
                            "embedding" => "embedding",
                            "image" => "image",
                            "transcription" => "transcription",
                            "speech" => "speech",
                            _ => "language"
                        }
                    };

                    if (el.TryGetProperty("owned_by", out var ownerEl))
                        model.OwnedBy = ownerEl.GetString() ?? "";

                    if (el.TryGetProperty("description", out var descriptionEl))
                        model.Description = descriptionEl.GetString() ?? "";

                    if (el.TryGetProperty("context_window", out var contextEl) &&
                        contextEl.ValueKind == JsonValueKind.Number &&
                        contextEl.TryGetInt32(out var contextWindow))
                    {
                        model.ContextWindow = contextWindow;
                    }

                    if (el.TryGetProperty("max_output_tokens", out var maxOutputEl) &&
                        maxOutputEl.ValueKind == JsonValueKind.Number &&
                        maxOutputEl.TryGetInt32(out var maxOutputTokens))
                    {
                        model.MaxTokens = maxOutputTokens;
                    }

                    // Chat models expose token pricing at the root level.
                    if (api == "chat")
                    {
                        decimal? inputPrice = null;
                        decimal? outputPrice = null;

                        if (el.TryGetProperty("input_price", out var inputEl) &&
                            inputEl.ValueKind == JsonValueKind.Number &&
                            inputEl.TryGetDecimal(out var input))
                        {
                            inputPrice = input;
                        }

                        if (el.TryGetProperty("output_price", out var outputEl) &&
                            outputEl.ValueKind == JsonValueKind.Number &&
                            outputEl.TryGetDecimal(out var output))
                        {
                            outputPrice = output;
                        }

                        if (inputPrice.HasValue &&
                            outputPrice.HasValue &&
                            inputPrice.Value != 0 &&
                            outputPrice.Value != 0)
                        {
                            model.Pricing = new ModelPricing
                            {
                                Input = inputPrice.Value,
                                Output = outputPrice.Value
                            };
                        }
                    }

                    models.Add(model);
                }

                return models
                    .Where(m => !string.IsNullOrWhiteSpace(m.Id))
                    .ToList();
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }
}
