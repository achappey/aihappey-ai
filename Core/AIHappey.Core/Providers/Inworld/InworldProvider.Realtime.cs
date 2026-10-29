using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Common.Model;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Inworld;

public partial class InworldProvider
{
    private const string InworldRealtimeSocketUrl = "wss://api.inworld.ai/api/v1/realtime/session";

    public async Task<RealtimeTokenResponse> CreateRealtimeTokenAsync(
        string model, RealtimeTokenRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = NormalizeInworldRealtimeModel(model);
        // Minted credentials are workspace-scoped, not bound to a conversation or its model.
        if (request.SessionConfig is not null && JsonSerializer.SerializeToNode(request.SessionConfig)!.AsObject().Count > 0)
            throw new NotSupportedException("Inworld session tokens cannot bind sessionConfig. Apply configuration in client session.update, or use WebRTC session creation.");
        var (token, expiresAt) = await MintInworldSessionTokenAsync(cancellationToken);
        return new() { Token = token, ExpiresAt = expiresAt, Url = InworldRealtimeSocketUrl };
    }

    public async Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(realtimeRequest);
        _ = NormalizeInworldRealtimeModel(realtimeRequest.Model);
        if (realtimeRequest.ProviderOptions is { Count: > 0 })
            throw new NotSupportedException("Inworld token minting does not accept conversation or workspace overrides. Configure the client connection after minting.");
        var (token, expiresAt) = await MintInworldSessionTokenAsync(cancellationToken);
        return new()
        {
            Value = token, ExpiresAt = expiresAt,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new
            {
                url = InworldRealtimeSocketUrl, webrtcUrl = "https://api.inworld.ai/v1/realtime/calls",
                iceServersUrl = "https://api.inworld.ai/v1/realtime/ice-servers"
            })
        };
    }

    private async Task<(string Token, long ExpiresAt)> MintInworldSessionTokenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var credential = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(credential)) throw new InvalidOperationException("No Inworld API key.");
        string[] parts;
        try { parts = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(credential)).Split(':'); }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
        { throw new InvalidOperationException("Inworld session-token minting requires the copied Base64 key ID and secret."); }
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1])
            || parts[0].Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw new InvalidOperationException("Inworld session-token minting requires a valid Base64 key ID and secret.");

        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        const string host = "api.inworld.ai";
        const string method = "ai.inworld.engine.v1.SessionTokens/GenerateSessionToken";
        var signature = Encoding.UTF8.GetBytes("IW1" + parts[1]);
        foreach (var value in new[] { timestamp, host, method, nonce, "iw1_request" })
        {
            var next = HMACSHA256.HashData(signature, Encoding.UTF8.GetBytes(value));
            CryptographicOperations.ZeroMemory(signature);
            signature = next;
        }
        var authorization = $"ApiKey={parts[0]},DateTime={timestamp},Nonce={nonce},Signature={Convert.ToHexStringLower(signature)}";
        CryptographicOperations.ZeroMemory(signature);
        using var message = InworldJsonRequest("v1/sessionTokens/token:generate", new() { ["api_key"] = parts[0] });
        message.Headers.Authorization = new AuthenticationHeaderValue("IW1-HMAC-SHA256", authorization);
        // Do not copy a client-selected resources/workspace scope into a bearer credential.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Inworld session-token minting failed (HTTP {(int)response.StatusCode}).");
        JsonDocument document;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
        }
        catch (JsonException) { throw new InvalidOperationException("Inworld session-token minting returned invalid JSON."); }
        using (document)
        {
            var root = document.RootElement;
            var token = RequiredInworldString(root, "token");
            var expiry = RequiredInworldString(root, "expirationTime");
            if (!DateTimeOffset.TryParse(expiry, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expiration) || expiration <= DateTimeOffset.UtcNow)
                throw new InvalidOperationException("Inworld session-token minting returned an invalid expirationTime.");
            if (root.TryGetProperty("type", out var type) && type.GetString() != "Bearer")
                throw new InvalidOperationException("Inworld session-token minting returned an unsupported token type.");
            cancellationToken.ThrowIfCancellationRequested();
            return (token, expiration.ToUnixTimeSeconds());
        }
    }

    public async Task<RealtimeSessionResponse> CreateRealtimeSessionAsync(
        string model, RealtimeSessionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Sdp);
        cancellationToken.ThrowIfCancellationRequested();
        var session = BuildInworldRealtimeSession(NormalizeInworldRealtimeModel(model), request.SessionConfig);
        using var message = InworldJsonRequest("v1/realtime/calls", new() { ["sdp"] = request.Sdp, ["session"] = session });
        ApplyAuthHeader();
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        // Avoid echoing SDP, instructions, tool arguments or credentials through provisioning errors.
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Inworld WebRTC setup failed (HTTP {(int)response.StatusCode}).");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var id = RequiredInworldString(document.RootElement, "id");
        var sdp = RequiredInworldString(document.RootElement, "sdp");
        if (!sdp.StartsWith("v=0", StringComparison.Ordinal)) throw new InvalidOperationException("Inworld WebRTC returned an invalid SDP answer.");
        return new() { SessionId = id, Sdp = sdp };
    }

    private static string NormalizeInworldRealtimeModel(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var raw = StripInworldProviderPrefix(model.Trim());
        if (raw.StartsWith("inworld-tts-", StringComparison.OrdinalIgnoreCase) || raw.Contains("inworld-stt-", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Realtime sessions require an LLM model, not a standalone TTS/STT model.");
        const string servicePrefix = "SERVICE_PROVIDER_";
        if (raw.StartsWith(servicePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var slash = raw.IndexOf('/');
            if (slash < servicePrefix.Length || slash == raw.Length - 1) throw new ArgumentException("Invalid Inworld provider/model shortcut.");
            raw = raw[servicePrefix.Length..slash].ToLowerInvariant() + "/" + raw[(slash + 1)..];
        }
        return raw;
    }

    private static JsonObject BuildInworldRealtimeSession(string model, RealtimeSessionConfig? config)
    {
        var options = RealtimeSessionMapping.Options(config, "inworld");
        if (options.Any(p => p.Key != "session")) throw new ArgumentException("Inworld realtime provider options belong inside providerOptions.inworld.session.");
        var session = new JsonObject { ["type"] = "realtime", ["model"] = model };
        if (config is not null)
        {
            if (config.OutputAudioTranscription is not null) throw new NotSupportedException("Inworld output transcription is automatic and cannot be configured independently.");
            RealtimeSessionMapping.Put(session, "instructions", config.Instructions);
            RealtimeSessionMapping.Put(session, "output_modalities", config.OutputModalities);
            RealtimeSessionMapping.Put(session, "tools", config.Tools);
            var input = new JsonObject();
            var output = new JsonObject();
            if (config.InputAudioFormat is not null) input["format"] = InworldRealtimeAudioFormat(config.InputAudioFormat);
            if (config.OutputAudioFormat is not null) output["format"] = InworldRealtimeAudioFormat(config.OutputAudioFormat);
            if (config.InputAudioTranscription is { } transcription)
            {
                if (transcription.Prompt is not null) throw new NotSupportedException("Inworld STT does not accept singular prompt. Supply native audio.input.transcription.prompts through provider options.");
                var native = new JsonObject();
                RealtimeSessionMapping.Put(native, "model", transcription.Model);
                RealtimeSessionMapping.Put(native, "language", transcription.Language);
                input["transcription"] = native;
            }
            if (config.HasTurnDetection)
            {
                var detection = config.TurnDetection;
                JsonObject? vad = null;
                if (detection is not null && detection.Type != "disabled")
                {
                    if (detection.Type == "semantic-vad" && (detection.Threshold is not null || detection.SilenceDurationMs is not null || detection.PrefixPaddingMs is not null))
                        throw new ArgumentException("Semantic VAD does not support server VAD thresholds or timings.");
                    vad = new() { ["type"] = detection.Type.Replace('-', '_') };
                    RealtimeSessionMapping.Put(vad, "threshold", detection.Threshold);
                    RealtimeSessionMapping.Put(vad, "silence_duration_ms", detection.SilenceDurationMs);
                    RealtimeSessionMapping.Put(vad, "prefix_padding_ms", detection.PrefixPaddingMs);
                }
                input["turn_detection"] = vad;
            }
            RealtimeSessionMapping.Put(output, "voice", config.Voice);
            var audio = new JsonObject();
            if (input.Count > 0) audio["input"] = input;
            if (output.Count > 0) audio["output"] = output;
            if (audio.Count > 0) session["audio"] = audio;
        }
        if (options.ContainsKey("session"))
        {
            var overrides = RealtimeSessionMapping.Object(options, "session");
            RealtimeSessionMapping.CheckString(overrides, "model", model);
            RealtimeSessionMapping.CheckString(overrides, "type", "realtime");
            if (overrides.Any(p => p.Key is "sdp" or "transport" or "id" or "object" or "expires_at"))
                throw new ArgumentException("Session options cannot override transport or read-only fields.");
            RealtimeSessionMapping.Merge(session, overrides);
        }
        _ = RealtimeSessionMapping.Tools(session["tools"]);
        if (session["audio"]?["input"]?["transcription"] is JsonObject stt && stt.ContainsKey("prompt"))
            throw new NotSupportedException("Inworld STT accepts prompts, not singular prompt.");
        return session;
    }

    private static JsonObject InworldRealtimeAudioFormat(RealtimeAudioFormat format)
    {
        if (format.Type is not ("audio/pcm" or "audio/pcmu" or "audio/pcma") || format.Rate is not null and not 24000)
            throw new ArgumentException("Inworld Realtime audio uses audio/pcm, audio/pcmu or audio/pcma at 24000 Hz.");
        return new() { ["type"] = format.Type, ["rate"] = 24000 };
    }
}
