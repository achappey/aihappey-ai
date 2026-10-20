using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Hedra;

public partial class HedraProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return [];
        return await _memoryCache.GetOrCreateAsync(
            this.GetCacheKey(key),
            async ct =>
            {
                var root = await SendJsonAsync(HttpMethod.Get, "v3/models", null, ct);
                var models = new List<Model>();
                foreach (var item in root.GetProperty("data").EnumerateArray())
                {
                    var model = item.Deserialize<Model>() ?? throw new JsonException("Invalid Hedra model.");
                    var id = item.GetProperty("id").GetString()!;
                    var modality = ReadString(item, "modality");
                    model.Id = id.ToModelId(GetIdentifier());
                    model.Name ??= id;
                    model.OwnedBy ??= "Hedra";
                    model.Type = modality switch
                    {
                        "audio" => "speech",
                        "text" or "chat" => "language",
                        _ => modality ?? model.Type
                    };
                    if (item.TryGetProperty("context_length", out var context) && context.ValueKind == JsonValueKind.Number && context.TryGetInt32(out var contextLength))
                        model.ContextWindow = contextLength;
                    if (item.TryGetProperty("max_output_tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Number && tokens.TryGetInt32(out var maxTokens))
                        model.MaxTokens = maxTokens;
                    models.Add(model);
                }
                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 60,
            cancellationToken: cancellationToken);
    }
}
