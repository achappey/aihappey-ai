using System.Globalization;
using System.Text.Json.Nodes;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Extensions;

internal static partial class RealtimeSessionMapping
{
    internal static JsonObject GooglePayload(string model, RealtimeSessionConfig? config, DateTimeOffset now)
    {
        var options = Options(config, "google");
        Protect(options, model, google: true);
        options.Remove("model");
        if (options.ContainsKey("session"))
            throw new ArgumentException("Google token options use bidiGenerateContentSetup, not session.");
        var setup = new JsonObject { ["model"] = "models/" + model };
        if (config is not null)
        {
            ValidateGoogleFormat(config.InputAudioFormat, 16000);
            ValidateGoogleFormat(config.OutputAudioFormat, 24000);
            if (config.Instructions is not null)
                setup["systemInstruction"] = new JsonObject
                {
                    ["parts"] = new JsonArray(new JsonObject { ["text"] = config.Instructions })
                };
            var generation = new JsonObject();
            if (config.OutputModalities is not null)
            {
                if (config.OutputModalities.Any(m => m is not ("audio" or "text")))
                    throw new ArgumentException("Google Live output modalities must be audio or text.");
                Put(generation, "responseModalities", config.OutputModalities.Select(m => m.ToUpperInvariant()).ToArray());
            }
            if (config.Voice is not null)
                generation["speechConfig"] = new JsonObject
                {
                    ["voiceConfig"] = new JsonObject
                    {
                        ["prebuiltVoiceConfig"] = new JsonObject { ["voiceName"] = config.Voice }
                    }
                };
            if (generation.Count > 0) setup["generationConfig"] = generation;
            Put(setup, "inputAudioTranscription", GoogleTranscription(config.InputAudioTranscription));
            Put(setup, "outputAudioTranscription", GoogleTranscription(config.OutputAudioTranscription));
            if (config.HasTurnDetection)
            {
                var vad = config.TurnDetection;
                if (vad?.Type == "semantic-vad" || vad?.Threshold is not null)
                    throw new ArgumentException("Google Live does not support semantic VAD or numeric VAD thresholds.");
                var activity = new JsonObject { ["disabled"] = vad is null || vad.Type == "disabled" };
                Put(activity, "silenceDurationMs", vad?.SilenceDurationMs);
                Put(activity, "prefixPaddingMs", vad?.PrefixPaddingMs);
                setup["realtimeInputConfig"] = new JsonObject { ["automaticActivityDetection"] = activity };
            }
            if (config.Tools is { } tools)
            {
                var declarations = new JsonArray();
                foreach (var tool in tools)
                {
                    var declaration = new JsonObject { ["name"] = tool.Name };
                    Put(declaration, "description", tool.Description);
                    Put(declaration, "parametersJsonSchema", tool.Parameters);
                    declarations.Add(declaration);
                }
                setup["tools"] = new JsonArray(new JsonObject { ["functionDeclarations"] = declarations });
            }
        }
        var payload = new JsonObject
        {
            ["uses"] = 1,
            ["expireTime"] = now.AddMinutes(30).ToString("O", CultureInfo.InvariantCulture),
            ["newSessionExpireTime"] = now.AddMinutes(1).ToString("O", CultureInfo.InvariantCulture),
            ["bidiGenerateContentSetup"] = setup
        };
        Merge(payload, options);
        Object(payload, "bidiGenerateContentSetup")["model"] = "models/" + model;
        var expiry = GoogleExpiry(payload, "expireTime");
        var newSessionExpiry = GoogleExpiry(payload, "newSessionExpireTime");
        if (expiry <= now || expiry > now.AddHours(20) || newSessionExpiry <= now || newSessionExpiry > expiry)
            throw new ArgumentException("Google token expiry windows are invalid.");
        if (payload["uses"] is not JsonValue uses || !uses.TryGetValue<int>(out var count) || count < 0)
            throw new ArgumentException("Google token uses must be a nonnegative integer.");
        return payload;
    }

    internal static DateTimeOffset GoogleExpiry(JsonObject source, string key)
    {
        if (source[key] is not JsonValue value || !value.TryGetValue<string>(out var text)
            || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var expiry))
            throw new ArgumentException($"Google '{key}' must be an RFC 3339 timestamp.");
        return expiry;
    }

    internal static List<RealtimeToolDefinition>? GoogleTools(JsonNode? tools)
    {
        if (tools is null) return null;
        if (tools is not JsonArray array) throw new ArgumentException("Google tools must be an array.");
        var functions = new JsonArray();
        foreach (var tool in array)
        {
            if (tool is not JsonObject item) throw new ArgumentException("Google tools must contain objects.");
            if (!item.TryGetPropertyValue("functionDeclarations", out var value)) continue;
            if (value is not JsonArray declarations) throw new ArgumentException("Google functionDeclarations must be an array.");
            foreach (var declaration in declarations)
            {
                if (declaration is not JsonObject function) throw new ArgumentException("Invalid Google function declaration.");
                functions.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["name"] = function["name"]?.DeepClone(),
                    ["description"] = function["description"]?.DeepClone(),
                    ["parameters"] = (function["parametersJsonSchema"] ?? function["parameters"])?.DeepClone()
                        ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
                });
            }
        }
        return Tools(functions);
    }

    private static JsonObject? GoogleTranscription(RealtimeAudioTranscription? config)
    {
        if (config is null) return null;
        if (config.Model is not null || config.Prompt is not null)
            throw new ArgumentException("Google Live transcription does not accept a model or prompt.");
        var result = new JsonObject();
        if (config.Language is not null) Put(result, "languageCodes", new[] { config.Language });
        return result;
    }

    private static void ValidateGoogleFormat(RealtimeAudioFormat? format, int rate)
    {
        if (format is not null && (format.Type != "audio/pcm" || (format.Rate is not null && format.Rate != rate)))
            throw new ArgumentException($"Google Live uses audio/pcm at {rate} Hz for this audio direction.");
    }
}
