using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Extensions;

/// <summary>Mapping for the new generic endpoints only; never used by legacy client-secret flows.</summary>
internal static partial class RealtimeSessionMapping
{
    internal static JsonObject Options(RealtimeSessionConfig? config, string provider)
    {
        if (config is not null) LiveSessionExtensions.ValidateGraph(config);
        if (config?.ProviderOptions?.TryGetValue(provider, out var value) != true) return [];
        if (value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException($"providerOptions.{provider} must be an object.");
        return JsonNode.Parse(value.GetRawText())!.AsObject();
    }

    internal static void Merge(JsonObject target, JsonObject overrides)
    {
        foreach (var (key, value) in overrides)
        {
            if (value is JsonObject nested && target[key] is JsonObject current) Merge(current, nested);
            else target[key] = value?.DeepClone();
        }
    }

    internal static JsonObject Object(JsonObject parent, string key)
    {
        if (!parent.ContainsKey(key)) parent[key] = new JsonObject();
        return parent[key] as JsonObject ?? throw new ArgumentException($"'{key}' must be an object.");
    }

    internal static void Protect(JsonObject options, string model, string? sdp = null, bool google = false)
    {
        CheckString(options, "model", model, google ? "models/" + model : null);
        CheckSdpAndTransport(options, sdp);
        if (options.ContainsKey("session"))
        {
            var session = Object(options, "session");
            CheckString(session, "model", model);
            CheckSdpAndTransport(session, sdp);
        }
        if (google && options.ContainsKey("bidiGenerateContentSetup"))
        {
            var setup = Object(options, "bidiGenerateContentSetup");
            CheckString(setup, "model", "models/" + model, model);
            CheckSdpAndTransport(setup, sdp);
        }
    }

    private static void CheckSdpAndTransport(JsonObject options, string? sdp)
    {
        if (options.ContainsKey("sdp"))
        {
            if (sdp is null) throw new ArgumentException("SDP cannot be supplied to token provisioning.");
            CheckString(options, "sdp", sdp);
        }
        if (!options.ContainsKey("transport")) return;
        if (sdp is null) throw new ArgumentException("Transport cannot be overridden for token provisioning.");
        var transport = Object(options, "transport");
        CheckString(transport, "type", "webrtc");
        CheckString(transport, "sdp", sdp);
        if (transport.Any(p => p.Key is not ("type" or "sdp")))
            throw new ArgumentException("Generic sessions support only the submitted WebRTC transport.");
    }

    internal static void CheckString(JsonObject options, string key, string expected, string? alternative = null)
    {
        if (!options.TryGetPropertyValue(key, out var node)) return;
        if (node is not JsonValue value || !value.TryGetValue<string>(out var actual)
            || (actual != expected && actual != alternative))
            throw new ArgumentException($"Provider options conflict with protected '{key}'.");
    }

    internal static JsonObject OpenAISession(string model, RealtimeSessionConfig? config)
    {
        var session = new JsonObject { ["type"] = "realtime", ["model"] = model };
        if (config is null) return session;
        if (config.OutputAudioTranscription is not null)
            throw new ArgumentException("OpenAI output audio transcription is automatic and cannot be configured.");
        Put(session, "instructions", config.Instructions);
        Put(session, "output_modalities", config.OutputModalities);
        Put(session, "tools", config.Tools);
        var audio = new JsonObject();
        var input = new JsonObject();
        var output = new JsonObject();
        Put(input, "format", AudioFormat(config.InputAudioFormat));
        Put(input, "transcription", Transcription(config.InputAudioTranscription));
        if (config.HasTurnDetection) input["turn_detection"] = OpenAITurnDetection(config.TurnDetection);
        Put(output, "format", AudioFormat(config.OutputAudioFormat));
        Put(output, "voice", config.Voice);
        if (input.Count > 0) audio["input"] = input;
        if (output.Count > 0) audio["output"] = output;
        if (audio.Count > 0) session["audio"] = audio;
        return session;
    }

    internal static JsonObject OpenAIPayload(string model, RealtimeSessionConfig? config, string? sdp = null)
    {
        var options = Options(config, "openai");
        Protect(options, model, sdp);
        // Model and transport belong to the endpoint envelope, not an arbitrary options bag.
        options.Remove("model");
        var payload = new JsonObject { ["session"] = OpenAISession(model, config) };
        Merge(payload, options);
        var session = Object(payload, "session");
        CheckString(session, "type", "realtime");
        session["model"] = model;
        session.Remove("sdp");
        session.Remove("transport");
        return payload;
    }

    internal static JsonObject LivePayload(string model, string sdp, RealtimeSessionConfig? config)
    {
        var options = Options(config, "openai");
        Protect(options, model, sdp);
        options.Remove("model");
        options.Remove("sdp");
        var session = new JsonObject { ["model"] = model };
        if (config is not null)
        {
            if (config.InputAudioFormat is not null || config.OutputAudioFormat is not null
                || config.InputAudioTranscription is not null || config.OutputAudioTranscription is not null
                || config.HasTurnDetection || config.OutputModalities is not null)
                throw new ArgumentException("Live negotiates audio on its transport; Realtime audio and VAD settings are not supported.");
            Put(session, "instructions", config.Instructions);
            if (config.Voice is not null)
                session["audio"] = new JsonObject { ["output"] = new JsonObject { ["voice"] = config.Voice } };
        }
        var payload = new JsonObject
        {
            ["session"] = session,
            ["transport"] = new JsonObject { ["type"] = "webrtc", ["sdp"] = sdp }
        };
        Merge(payload, options);
        session = Object(payload, "session");
        session["model"] = model;
        if (config?.Tools is { } tools)
        {
            if (session["delegation"] is not JsonObject delegation
                || delegation["type"]?.GetValue<string>() != "responses"
                || delegation["responses"] is not JsonObject responses
                || responses["model"] is not JsonValue backend || !backend.TryGetValue<string>(out var backendModel)
                || string.IsNullOrWhiteSpace(backendModel))
                throw new ArgumentException("Live tools require providerOptions.openai.session.delegation with a Responses backend model.");
            // Native tools take precedence over the neutral list.
            if (!responses.ContainsKey("tools")) Put(responses, "tools", tools);
        }
        return payload;
    }

    private static JsonObject? AudioFormat(RealtimeAudioFormat? format)
    {
        if (format is null) return null;
        if (format.Type is not ("audio/pcm" or "audio/pcmu" or "audio/pcma"))
            throw new ArgumentException("Unsupported Realtime audio format.");
        if (format.Type == "audio/pcm" ? format.Rate is not null and not 24000 : format.Rate is not null)
            throw new ArgumentException("OpenAI PCM uses 24000 Hz; G.711 formats do not accept a sample rate.");
        var result = new JsonObject { ["type"] = format.Type };
        if (format.Type == "audio/pcm") result["rate"] = 24000;
        return result;
    }

    private static JsonObject? Transcription(RealtimeAudioTranscription? transcription)
    {
        if (transcription is null) return null;
        var result = new JsonObject();
        Put(result, "model", transcription.Model);
        Put(result, "language", transcription.Language);
        Put(result, "prompt", transcription.Prompt);
        return result;
    }

    private static JsonObject? OpenAITurnDetection(RealtimeTurnDetection? detection)
    {
        if (detection is null || detection.Type == "disabled") return null;
        if (detection.Type == "semantic-vad" && (detection.Threshold is not null
            || detection.SilenceDurationMs is not null || detection.PrefixPaddingMs is not null))
            throw new ArgumentException("Semantic VAD does not support server VAD thresholds or timings.");
        var result = new JsonObject { ["type"] = detection.Type.Replace('-', '_') };
        Put(result, "threshold", detection.Threshold);
        Put(result, "silence_duration_ms", detection.SilenceDurationMs);
        Put(result, "prefix_padding_ms", detection.PrefixPaddingMs);
        return result;
    }

    internal static void Put<T>(JsonObject target, string key, T? value)
    {
        if (value is not null) target[key] = JsonSerializer.SerializeToNode(value, LiveSessionExtensions.SessionJsonOptions);
    }

    internal static string RequiredString(JsonObject response, string key)
        => response[key] is JsonValue value && value.TryGetValue<string>(out var result) && !string.IsNullOrWhiteSpace(result)
            ? result : throw new InvalidOperationException($"Realtime response is missing a valid '{key}'.");

    internal static long? Expiry(JsonObject response, string key)
    {
        if (!response.TryGetPropertyValue(key, out var node)) return null;
        return node is JsonValue value && value.TryGetValue<long>(out var result) && result > 0
            ? result : throw new InvalidOperationException("Realtime response contains an invalid expiry.");
    }

    internal static List<RealtimeToolDefinition>? Tools(JsonNode? tools)
    {
        if (tools is null) return null;
        try
        {
            var result = tools.Deserialize<List<RealtimeToolDefinition>>(LiveSessionExtensions.SessionJsonOptions)
                ?? throw new JsonException();
            LiveSessionExtensions.ValidateGraph(result);
            return result;
        }
        catch (JsonException)
        {
            throw new ArgumentException("Realtime function tools must have names and JSON-schema parameters.");
        }
    }
}
