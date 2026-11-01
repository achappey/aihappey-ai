using System.Globalization;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.BaguetteBot;

public sealed partial class BaguetteBotProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return [];

        var catalog = await SendJsonAsync(HttpMethod.Get, "v1/models", null, cancellationToken);
        var nativeModels = ReadList(catalog, "models").ToDictionary(item => RequiredString(item, "id"), StringComparer.Ordinal);
        var agents = await SendJsonAsync(HttpMethod.Get, "v1/agents?limit=500", null, cancellationToken);
        var result = nativeModels.Values.Select(item => MapModel(item, null)).ToList();
        foreach (var agent in ReadList(agents, "agents"))
        {
            var id = RequiredString(agent, "id");
            ValidateAgentId(id);
            nativeModels.TryGetValue(Text(agent, "model") ?? "", out var model);
            result.Add(MapModel(model, agent));
        }
        return result;
    }

    private Model MapModel(JsonElement native, JsonElement? agent)
    {
        var nativeId = Text(native, "id") ?? (agent.HasValue ? Text(agent.Value, "model") : null);
        var description = agent.HasValue
            ? $"Persistent BaguetteBot agent {RequiredString(agent.Value, "id")}; underlying model: {nativeId}."
            : $"BaguetteBot model {nativeId}; execution requires an existing agent_id. Selecting this model does not update the agent.";
        // ModelPricing has no currency field; EUR must not enter the gateway's USD cost calculation.
        var input = Property(native, "eur_per_million_input_tokens");
        var output = Property(native, "eur_per_million_output_tokens");
        if (input.HasValue || output.HasValue)
            description += $" Native pricing (EUR/million tokens): input={input?.ToString() ?? "unknown"}, output={output?.ToString() ?? "unknown"}.";
        return new Model
        {
            Id = agent.HasValue ? $"baguettebot/agent/{RequiredString(agent.Value, "id")}" : $"baguettebot/model/{nativeId}",
            Name = agent.HasValue ? Text(agent.Value, "name") ?? RequiredString(agent.Value, "id") : Text(native, "name") ?? nativeId!,
            OwnedBy = "BaguetteBot",
            Type = "language",
            Description = description,
            ContextWindow = Property(native, "context_length") is { } context && context.TryGetInt32(out var length) ? length : null,
            Created = agent.HasValue && DateTimeOffset.TryParse(Text(agent.Value, "created_at"), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var created) ? created.ToUnixTimeSeconds() : null,
            Tags = Property(native, "vision")?.ValueKind == JsonValueKind.True
                ? ["agent", "stateful", "computer", "vision"] : ["agent", "stateful", "computer"]
        };
    }

    private static IReadOnlyList<JsonElement> ReadList(JsonElement root, string alternate)
    {
        var data = root.ValueKind == JsonValueKind.Array ? root : Property(root, "data") ?? Property(root, alternate);
        if (data is not { ValueKind: JsonValueKind.Array })
            throw new InvalidOperationException($"BaguetteBot {alternate} response must contain an array.");
        return data.Value.EnumerateArray().Select(item => item.Clone()).ToList();
    }
}
