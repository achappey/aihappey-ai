using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.OpenAI.Live;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.FishAudio;

public partial class FishAudioProvider
{
    private static bool IsAgentModel(string model) => model.StartsWith("fishaudio/agents/", StringComparison.OrdinalIgnoreCase)
        || model.StartsWith("agents/", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, JsonElement> FishOptions(Dictionary<string, JsonElement>? options)
    {
        if (options is null || !options.TryGetValue("fishaudio", out var value) || value.ValueKind == JsonValueKind.Null) return [];
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Fish Audio provider options must be an object.");
        return value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
    }

    private HttpRequestMessage FishRequest(HttpMethod method, string path, object? payload = null)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("No FishAudio API key.");
        var message = new HttpRequestMessage(method, path);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (payload is not null) message.Content = JsonContent.Create(payload, options: SpeechJson);
        return message;
    }

    private async Task<RealtimeResponse> GetFishRealtimeTokenAsync(RealtimeRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var session = await CreateFishSessionAsync(request.Model, FishOptions(request.ProviderOptions), ct);
        return new RealtimeResponse
        {
            Value = session.GetProperty("token").GetString()!,
            ExpiresAt = DateTimeOffset.Parse(session.GetProperty("expires_at").GetString()!, CultureInfo.InvariantCulture).ToUnixTimeSeconds(),
            // This is the SDK SessionToken object, not a provider credential or an inferred token.
            ProviderMetadata = new() { [GetIdentifier()] = session }
        };
    }

    private async Task<LiveSessionResponse> CreateFishLiveSessionAsync(LiveSessionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Session);
        if (request.Transport?.Type != "livekit")
            throw new NotSupportedException("Fish Audio sessions require the livekit transport, not SDP or SIP.");
        var config = request.Session;
        if (config.Delegation is not null || config.Client is not null || config.Input is not null || config.Triggers is not null || config.Store is not null)
            throw new NotSupportedException("Fish Audio session history, tools and delegation are configured on the published agent.");
        // Native Fish session fields are extension data, not additions to the OpenAI-compatible schema.
        var options = config.AdditionalProperties?.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal) ?? [];
        var overrides = options.TryGetValue("overrides", out var item) && item.ValueKind == JsonValueKind.Object
            ? item.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone()) : new Dictionary<string, object?>();
        if (config.Instructions is not null) overrides["system_prompt"] = config.Instructions;
        if (config.Audio?.Output?.Voice is { } voice) overrides["voice_id"] = voice.Name ?? voice.Custom?.Id;
        if (overrides.Count > 0) options["overrides"] = JsonSerializer.SerializeToElement(overrides);
        var session = await CreateFishSessionAsync(config.Model, options, ct);
        return new LiveSessionResponse
        {
            Session = new() { Id = session.GetProperty("session_id").GetString()! },
            Transport = new LiveAdditionalResponseTransport("livekit")
            {
                AdditionalProperties = session.EnumerateObject().Where(p => p.Name is not ("transport" or "session_id"))
                    .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal)
            }
        };
    }

    private async Task<JsonElement> CreateFishSessionAsync(string model, Dictionary<string, JsonElement> options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var id = NormalizeModel(model);
        if (id.StartsWith("agents/", StringComparison.OrdinalIgnoreCase)) id = id[7..];
        if (string.IsNullOrWhiteSpace(id) || id.Contains('/') || id is "asr" or "s1" or "s2-pro" or "s2.1-pro" or "s2.1-pro-free" or "drama-3")
            throw new ArgumentException("A Fish Audio agent ID is required, not a speech model.");
        string[] allowed = ["agent_id", "name", "timezone", "client_timezone", "context", "overrides", "dynamic_variables", "end_user_id", "metadata", "llm_extra_body", "tool_events", "record_audio"];
        if (options.Keys.Any(k => !allowed.Contains(k))) throw new ArgumentException("Unsupported Fish Audio session option.");
        if (options.TryGetValue("agent_id", out var selected) && (selected.ValueKind != JsonValueKind.String || selected.GetString() != id))
            throw new ArgumentException("Fish Audio agent_id must match the selected model.");
        options["agent_id"] = JsonSerializer.SerializeToElement(id);
        using var message = FishRequest(HttpMethod.Post, "v1/agent/sessions", options);
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        // Session errors must never echo a token or credential from an upstream response body.
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Fish Audio session creation failed (HTTP {(int)response.StatusCode}).");
        JsonElement session;
        try { session = await response.Content.ReadFromJsonAsync<JsonElement>(ct); }
        catch (JsonException) { throw new InvalidOperationException("Fish Audio returned an invalid session response."); }
        if (session.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Fish Audio returned an invalid session response.");
        string Required(string key) => session.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()! : throw new InvalidOperationException($"Fish Audio session response is missing {key}.");
        if (Required("transport") != "livekit") throw new NotSupportedException("Unsupported Fish Audio session transport.");
        _ = Required("session_id"); _ = Required("token");
        if (!Uri.TryCreate(Required("livekit_url"), UriKind.Absolute, out var url) || url.Scheme != "wss" || !string.IsNullOrEmpty(url.UserInfo))
            throw new InvalidOperationException("Fish Audio returned an invalid LiveKit URL.");
        if (!DateTimeOffset.TryParse(Required("expires_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expires) || expires <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Fish Audio returned an invalid or expired join deadline.");
        if (!session.TryGetProperty("max_duration_seconds", out var duration) || !duration.TryGetInt32(out var seconds) || seconds <= 0)
            throw new InvalidOperationException("Fish Audio returned an invalid session duration.");
        return session.Clone();
    }

    private async Task<IEnumerable<Model>> ListFishModelsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var models = GetIdentifier().GetModels().ToList();
        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier()))) return models;
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var path = "v1/agent/agents?include_archived=false&publication_state=live&page_size=100";
            if (cursor is not null) path += "&cursor=" + Uri.EscapeDataString(cursor);
            using var message = FishRequest(HttpMethod.Get, path);
            using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
            // Discovery is optional for existing TTS-only accounts; keep their static catalog.
            if (!response.IsSuccessStatusCode) return models;
            var page = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (!page.TryGetProperty("agents", out var agents) || agents.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Fish Audio agent discovery returned an invalid page.");
            foreach (var agent in agents.EnumerateArray())
            {
                string? Read(string key) => agent.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                if (Read("status") != "active" || Read("publication_state") != "live" || string.IsNullOrWhiteSpace(Read("agent_id"))) continue;
                var tags = new List<string> { "agent", "realtime", "realtime-token", "dynamic", "livekit", "active", "live" };
                foreach (var key in new[] { "workspace_id", "updated_at" }) if (Read(key) is { } value) tags.Add(key + ":" + value);
                if (agent.TryGetProperty("public_enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True) tags.Add("public");
                foreach (var key in new[] { "allowed_origins", "overrides_allowed" })
                    if (agent.TryGetProperty(key, out var values) && values.ValueKind == JsonValueKind.Array)
                        tags.AddRange(values.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => key + ":" + v.GetString()));
                models.Add(new Model { Id = $"fishaudio/agents/{Read("agent_id")}", OwnedBy = "FishAudio", Name = Read("name") ?? Read("agent_id")!,
                    Description = Read("description"), Type = "audio", Tags = tags,
                    Created = DateTimeOffset.TryParse(Read("created_at"), out var created) ? created.ToUnixTimeSeconds() : null });
            }
            if (!page.TryGetProperty("has_more", out var more) || more.ValueKind != JsonValueKind.True) break;
            cursor = page.TryGetProperty("next_cursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
            if (string.IsNullOrWhiteSpace(cursor) || !cursors.Add(cursor) || cursors.Count > 1000)
                throw new InvalidOperationException("Fish Audio agent pagination did not advance.");
        } while (true);
        return models.DistinctBy(m => m.Id, StringComparer.Ordinal).ToArray();
    }
}
