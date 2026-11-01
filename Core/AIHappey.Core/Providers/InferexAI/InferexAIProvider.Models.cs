using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.InferexAI;

public sealed partial class InferexAIProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = keyResolver.Resolve(ProviderId);
        if (string.IsNullOrWhiteSpace(key))
            return [];
        var models = await asyncCacheHelper.GetOrCreateAsync<IEnumerable<Model>>(
            this.GetCacheKey(key), async ct =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "v1/models");
                request.Headers.Authorization = new("Bearer", key);
                using var response = await _client.SendAsync(request, ct);
                var root = await ReadJson(response, ct);
                return root.GetProperty("data").EnumerateArray().Select(ToModel).ToArray();
            }, baseTtl: TimeSpan.FromMinutes(5), jitterMinutes: 1, cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return models;
    }

    private static Model ToModel(JsonElement item)
    {
        var id = item.GetProperty("id").GetString();
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var nativeType = item.GetProperty("type").GetString();
        var tags = new List<string>();
        // Model has no extension-data field. Keep all native capability fields in tags.
        foreach (var property in item.EnumerateObject())
        {
            if (property.Name is "id" or "object" or "created" or "owned_by" or "name" or "description" or "type")
                continue;
            tags.Add($"{ProviderId}:{property.Name}={property.Value.GetRawText()}");
            if (property.Name is "voices" or "languages" && property.Value.ValueKind == JsonValueKind.Array)
                tags.AddRange(property.Value.EnumerateArray().Select(v => $"{property.Name}:{v.GetString()}"));
            if (property.Name == "reasoning" && property.Value.ValueKind == JsonValueKind.True)
                tags.Add("reasoning");
            if (property.Name == "streaming" && property.Value.ValueKind == JsonValueKind.True)
                tags.Add("streaming");
        }
        return new Model
        {
            Id = $"{ProviderId}/{id}", Name = String(item, "name") ?? id,
            OwnedBy = String(item, "owned_by") ?? ProviderId,
            Description = String(item, "description"),
            Created = item.TryGetProperty("created", out var created) && created.ValueKind != JsonValueKind.Null ? created.GetInt64() : null,
            ContextWindow = item.TryGetProperty("context_window", out var context) && context.ValueKind != JsonValueKind.Null ? context.GetInt32() : null,
            MaxTokens = item.TryGetProperty("max_tokens", out var maxTokens) && maxTokens.ValueKind != JsonValueKind.Null ? maxTokens.GetInt32() : null,
            Type = nativeType switch
            {
                "llm" => "language", "stt" => "transcription", "tts" => "speech",
                "embedding" => "embedding", "intent" => "intent", "voice" => "voice",
                _ => nativeType ?? throw new InvalidOperationException("InferexAI model has no type.")
            },
            Tags = tags
        };
    }

    private async Task<Model> ResolveModel(string? model, CancellationToken cancellationToken)
        => (await ListModels(cancellationToken)).SingleOrDefault(m => m.Id == $"{ProviderId}/{NativeModel(model)}")
            ?? throw new ArgumentException($"InferexAI model '{model}' is not available.");

    private static string? String(JsonElement root, string property)
        => root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? Number(JsonElement root, string property)
        => root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static Dictionary<string, JsonElement> Metadata(JsonElement root)
        => new() { [ProviderId] = root.Clone() };

    private static Dictionary<string, object?> Options(Dictionary<string, JsonElement>? providerOptions)
    {
        var result = new Dictionary<string, object?>();
        if (providerOptions?.TryGetValue(ProviderId, out var native) == true)
        {
            if (native.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("providerOptions.inferexai must be an object.");
            foreach (var property in native.EnumerateObject())
                result[property.Name] = property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.Clone();
        }
        return result;
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"InferexAI failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(ct)}", null, response.StatusCode);
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response, CancellationToken ct)
    {
        await EnsureSuccess(response, ct);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = document.RootElement;
        if (root.TryGetProperty("error", out _))
            throw new InvalidOperationException($"InferexAI returned an error: {root}");
        return root.Clone();
    }
}
