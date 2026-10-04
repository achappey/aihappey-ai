using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Anthropic;

public partial class AnthropicProvider
{
    // Ownership lives on the provider session, not in a process-local cache or the
    // replayed chat transcript. Chunking respects the 512-character metadata limit.
    private const string ManagedAgentClientToolsMetadataKey = "aihappey.client_tools.v1";
    private const int ManagedAgentMetadataMaxPairs = 16;
    private const int ManagedAgentMetadataMaxValueLength = 512;

    private sealed record ManagedAgentToolReconciliation(
        List<JsonElement> Tools,
        Dictionary<string, object?> MetadataPatch,
        bool ToolsChanged);

    private static List<JsonElement> BuildManagedAgentClientTools(List<AIToolDefinition>? definitions)
    {
        var tools = new List<JsonElement>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in definitions ?? [])
        {
            var name = definition.Name;
            if (string.IsNullOrWhiteSpace(name) || name.Length > 128
                || name.Any(static c => !(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z'
                    or >= '0' and <= '9' or '_' or '-')))
                throw new ArgumentException($"Anthropic managed-agent client tool '{name}' must have a 1-128 character alphanumeric, underscore, or hyphen name.");
            if (!names.Add(name))
                throw new ArgumentException($"Anthropic managed-agent client tool '{name}' is defined more than once.");

            var schema = definition.InputSchema is null
                ? JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }, JsonSerializerOptions.Web)
                : JsonSerializer.SerializeToElement(definition.InputSchema, JsonSerializerOptions.Web);
            if (schema.ValueKind != JsonValueKind.Object || TryGetString(schema, "type") != "object")
                throw new ArgumentException($"Anthropic managed-agent client tool '{name}' requires an object input schema.");

            // Keep the complete schema (including nested constraints). Do not weaken
            // a client's schema by projecting it to only properties and required.
            tools.Add(JsonSerializer.SerializeToElement(new
            {
                type = "custom",
                name,
                description = string.IsNullOrWhiteSpace(definition.Description)
                    ? definition.Title ?? name
                    : definition.Description,
                input_schema = schema
            }, JsonSerializerOptions.Web));
        }
        return tools;
    }

    private async Task<object> BuildManagedAgentReferenceAsync(
        AIRequest request,
        AnthropicManagedAgentTarget target,
        List<JsonElement> clientTools,
        CancellationToken cancellationToken)
    {
        if (clientTools.Count == 0)
            return BuildManagedAgentReference(request, target);

        var requestedVersion = request.Metadata?.GetProviderOption<int?>(GetIdentifier(), "agent_version");
        var uri = $"{ManagedAgentsEndpoint}/{Uri.EscapeDataString(target.AgentId)}";
        if (requestedVersion is > 0)
            uri += $"?version={requestedVersion.Value}";
        var agent = await SendManagedAgentsJsonAsync(HttpMethod.Get, uri,
            operation: "Anthropic managed-agent retrieve agent for tool overrides", cancellationToken: cancellationToken);
        var version = TryGetInt32(agent, "version")
            ?? throw new InvalidOperationException("Anthropic managed-agent definition did not include its version.");
        if (requestedVersion is > 0 && requestedVersion.Value != version)
            throw new InvalidOperationException("Anthropic managed-agent returned a different version from the requested pin.");

        var merged = MergeManagedAgentClientTools(GetManagedAgentTools(agent), [], clientTools);
        // Pin the snapshot used to merge tools so a concurrently published version
        // cannot change the base configuration between retrieve and create.
        return new Dictionary<string, object?>
        {
            ["type"] = "agent_with_overrides",
            ["id"] = target.AgentId,
            ["version"] = version,
            ["tools"] = merged
        };
    }

    private async Task<JsonElement> ReconcileManagedAgentClientToolsAsync(
        string sessionId,
        List<JsonElement> clientTools,
        CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var session = await RetrieveManagedAgentSessionAsync(sessionId, cancellationToken);
            var reconciliation = BuildManagedAgentToolReconciliation(session, clientTools);
            if (!reconciliation.ToolsChanged && reconciliation.MetadataPatch.Count == 0)
                return session;

            var status = TryGetString(session, "status");
            if (status == "terminated")
                throw new InvalidOperationException("Cannot synchronize client tools on a terminated Anthropic managed-agent session.");
            if (status != "idle")
            {
                if (elapsed.Elapsed >= ManagedAgentPollTimeout)
                    throw new TimeoutException("Anthropic managed-agent session must be idle before client tools can be synchronized. The session was not interrupted or replaced.");
                await Task.Delay(ManagedAgentPollInterval, cancellationToken);
                continue; // Always merge against the fresh snapshot after becoming idle.
            }

            var body = new Dictionary<string, object?>();
            if (reconciliation.ToolsChanged)
                body["agent"] = new { tools = reconciliation.Tools };
            if (reconciliation.MetadataPatch.Count > 0)
                body["metadata"] = reconciliation.MetadataPatch;
            return await SendManagedAgentsJsonAsync(HttpMethod.Post,
                $"{ManagedAgentSessionsEndpoint}/{Uri.EscapeDataString(sessionId)}", body,
                "Anthropic managed-agent synchronize client tools", cancellationToken);
        }
    }

    private static ManagedAgentToolReconciliation BuildManagedAgentToolReconciliation(
        JsonElement session,
        List<JsonElement> clientTools)
    {
        var metadata = GetManagedAgentSessionMetadata(session);
        var ownedNames = ReadManagedAgentClientToolOwnership(metadata);
        // Legacy sessions without client tools need no tool snapshot or update.
        if (ownedNames.Count == 0 && clientTools.Count == 0)
            return new([], [], false);
        if (!TryGetProperty(session, "agent", out var agent))
            throw new InvalidOperationException("Anthropic managed-agent session did not include its agent configuration; refusing a destructive tool replacement.");
        var current = GetManagedAgentTools(agent);
        var merged = MergeManagedAgentClientTools(current, ownedNames, clientTools);
        var ownership = BuildManagedAgentClientToolOwnership(clientTools);
        ValidateManagedAgentMetadataCapacity(metadata, ownership);
        var patch = new Dictionary<string, object?>();
        foreach (var key in metadata.Keys.Where(IsManagedAgentClientToolsMetadataKey))
            if (!ownership.ContainsKey(key))
                patch[key] = null;
        foreach (var entry in ownership)
            if (!metadata.TryGetValue(entry.Key, out var existing) || existing != entry.Value)
                patch[entry.Key] = entry.Value;
        return new(merged, patch, !ManagedAgentJsonEquals(current, merged));
    }

    private static List<JsonElement> GetManagedAgentTools(JsonElement agent)
    {
        if (!TryGetProperty(agent, "tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Anthropic managed-agent configuration did not include a tool array; refusing a destructive tool replacement.");
        return tools.EnumerateArray().Select(static tool => tool.Clone()).ToList();
    }

    private static List<JsonElement> MergeManagedAgentClientTools(
        List<JsonElement> current,
        HashSet<string> ownedNames,
        List<JsonElement> clientTools)
    {
        var preserved = current.Where(tool => !(TryGetString(tool, "type") == "custom"
            && ownedNames.Contains(TryGetString(tool, "name") ?? string.Empty))).ToList();
        var nativeNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in preserved)
        {
            if (TryGetString(tool, "name") is { } name)
                nativeNames.Add(name);
            if (TryGetString(tool, "type") == "agent_toolset_20260401")
                nativeNames.UnionWith(["bash", "edit", "read", "web_fetch", "web_search", "grep", "glob"]);
        }
        foreach (var tool in clientTools)
        {
            var name = TryGetString(tool, "name")!;
            if (nativeNames.Contains(name))
                throw new InvalidOperationException($"Anthropic client tool '{name}' conflicts with an existing agent tool. Existing agent tools were not changed.");
        }
        preserved.AddRange(clientTools);
        return preserved;
    }

    private static Dictionary<string, string> GetManagedAgentSessionMetadata(JsonElement session)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (TryGetProperty(session, "metadata", out var value) && value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                    throw new InvalidOperationException("Anthropic session metadata must contain string values.");
                metadata[property.Name] = property.Value.GetString()!;
            }
        return metadata;
    }

    private static bool IsManagedAgentClientToolsMetadataKey(string key)
        => key == ManagedAgentClientToolsMetadataKey
            || key.StartsWith(ManagedAgentClientToolsMetadataKey + ".", StringComparison.Ordinal);

    private static Dictionary<string, string> BuildManagedAgentClientToolOwnership(List<JsonElement> tools)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (tools.Count == 0)
            return metadata;
        var names = tools.Select(tool => TryGetString(tool, "name")!).Order(StringComparer.Ordinal).ToArray();
        var json = JsonSerializer.Serialize(names);
        var chunks = (json.Length + ManagedAgentMetadataMaxValueLength - 1) / ManagedAgentMetadataMaxValueLength;
        metadata[ManagedAgentClientToolsMetadataKey] = chunks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        for (var index = 0; index < chunks; index++)
            metadata[$"{ManagedAgentClientToolsMetadataKey}.{index}"] = json.Substring(
                index * ManagedAgentMetadataMaxValueLength,
                Math.Min(ManagedAgentMetadataMaxValueLength, json.Length - index * ManagedAgentMetadataMaxValueLength));
        return metadata;
    }

    private static HashSet<string> ReadManagedAgentClientToolOwnership(Dictionary<string, string> metadata)
    {
        if (!metadata.TryGetValue(ManagedAgentClientToolsMetadataKey, out var count))
        {
            if (metadata.Keys.Any(IsManagedAgentClientToolsMetadataKey))
                throw new InvalidOperationException("Anthropic session client-tool ownership metadata is incomplete.");
            return new(StringComparer.Ordinal);
        }
        if (!int.TryParse(count, out var chunks) || chunks < 1 || chunks >= ManagedAgentMetadataMaxPairs)
            throw new InvalidOperationException("Anthropic session client-tool ownership metadata is invalid.");
        var json = new StringBuilder();
        for (var index = 0; index < chunks; index++)
        {
            if (!metadata.TryGetValue($"{ManagedAgentClientToolsMetadataKey}.{index}", out var chunk))
                throw new InvalidOperationException("Anthropic session client-tool ownership metadata is incomplete.");
            json.Append(chunk);
        }
        try
        {
            var names = JsonSerializer.Deserialize<string[]>(json.ToString());
            if (names is null || names.Any(string.IsNullOrWhiteSpace))
                throw new JsonException();
            return new(names, StringComparer.Ordinal);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Anthropic session client-tool ownership metadata is invalid.", ex);
        }
    }

    private static void ValidateManagedAgentMetadataCapacity(
        Dictionary<string, string> current,
        Dictionary<string, string> ownership)
    {
        if (current.Keys.Count(key => !IsManagedAgentClientToolsMetadataKey(key)) + ownership.Count > ManagedAgentMetadataMaxPairs)
            throw new InvalidOperationException("Anthropic session metadata has insufficient capacity to track client tools safely (maximum 16 pairs). No tools were changed.");
    }

    private static bool ManagedAgentJsonEquals(object left, object right)
        => CanonicalManagedAgentJson(JsonSerializer.SerializeToElement(left, JsonSerializerOptions.Web))
            == CanonicalManagedAgentJson(JsonSerializer.SerializeToElement(right, JsonSerializerOptions.Web));

    private static string CanonicalManagedAgentJson(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject().OrderBy(static p => p.Name, StringComparer.Ordinal)
                .Select(static p => JsonSerializer.Serialize(p.Name) + ":" + CanonicalManagedAgentJson(p.Value))) + "}",
            JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(CanonicalManagedAgentJson)) + "]",
            _ => element.GetRawText()
        };
}
