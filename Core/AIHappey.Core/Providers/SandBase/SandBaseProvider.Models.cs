using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.SandBase;

public partial class SandBaseProvider
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

                using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
                using var resp = await _client.SendAsync(req, cancellationToken);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(cancellationToken);
                    throw new Exception($"SandBase API error: {err}");
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

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


                    if (el.TryGetProperty("owned_by", out var orgEl))
                        model.OwnedBy = orgEl.GetString() ?? "";

                    if (el.TryGetProperty("capability_tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
                    {
                        model.Tags = tags.EnumerateArray().Where(tag => tag.ValueKind == JsonValueKind.String)
                            .Select(tag => tag.GetString()!).ToArray();
                    }

                    var capabilities = model.Tags ?? [];
                    model.Type = capabilities.Any(tag => tag.Contains("video", StringComparison.OrdinalIgnoreCase)) ? "video"
                        : capabilities.Any(tag => tag.Contains("image", StringComparison.OrdinalIgnoreCase) || tag.Contains("upscale", StringComparison.OrdinalIgnoreCase)) ? "image"
                        : capabilities.Any(tag => tag.Contains("speech-to-text", StringComparison.OrdinalIgnoreCase) || tag.Contains("transcri", StringComparison.OrdinalIgnoreCase)) ? "transcription"
                        : capabilities.Any(tag => tag.Contains("audio", StringComparison.OrdinalIgnoreCase) || tag.Contains("speech", StringComparison.OrdinalIgnoreCase) || tag.Contains("music", StringComparison.OrdinalIgnoreCase)) ? "speech"
                        : "language";

                    if (!string.IsNullOrEmpty(model.Id))
                        models.Add(model);
                }

                // Agent listing is independent of model listing: an unavailable Agents API
                // must not hide the ordinary SandBase catalogue.
                try
                {
                    models.AddRange(await ListSandBaseAgentModelsAsync(cancellationToken));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // The model endpoint remains usable even when agents are not enabled.
                }

                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }
}
