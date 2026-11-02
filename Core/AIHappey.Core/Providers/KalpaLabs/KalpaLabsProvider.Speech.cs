using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.KalpaLabs;

public partial class KalpaLabsProvider
{
    private sealed record SpeechPlan(string Path, JsonObject Payload, int Rate, List<object> Warnings);

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var format = NormalizeFormat(request.OutputFormat, streaming: false);
        var plan = PrepareSpeech(request, format == "pcm");
        if (format == "pcm")
        {
            using var audio = new MemoryStream();
            JsonElement? end = null;
            await foreach (var chunk in StreamSpeech(plan, cancellationToken))
            {
                if (chunk.Audio is not null)
                    await audio.WriteAsync(Convert.FromBase64String(chunk.Audio), cancellationToken);
                if (chunk.Kind == "end")
                    end = chunk.Data;
            }
            if (audio.Length == 0)
                throw new InvalidOperationException("KalpaLabs returned no audio.");
            return SpeechResult(plan, Convert.ToBase64String(audio.ToArray()), "pcm",
                end ?? throw new InvalidOperationException("KalpaLabs stream ended without terminal metadata."));
        }

        var body = await SendJsonAsync(HttpMethod.Post, plan.Path, plan.Payload, cancellationToken);
        var nativeAudio = body.TryGetProperty("reply", out var reply) ? reply.GetProperty("audio") : body.GetProperty("audio");
        if (nativeAudio.ValueKind == JsonValueKind.Null)
            throw new InvalidOperationException("KalpaLabs returned no audio for the speech request.");
        var base64 = nativeAudio.GetProperty("data_b64").GetString()!;
        ValidateAudio(base64, pcm: false);
        if (nativeAudio.GetProperty("format").GetString() != "wav")
            throw new InvalidOperationException("KalpaLabs returned an unexpected audio encoding.");
        return SpeechResult(plan, base64, "wav", body);
    }

    private static SpeechResponse SpeechResult(SpeechPlan plan, string audio, string format, JsonElement body)
        => new()
        {
            Audio = new() { Base64 = audio, MimeType = format == "wav" ? "audio/wav" : "audio/pcm", Format = format },
            Warnings = plan.Warnings,
            ProviderMetadata = Metadata(body),
            Request = new() { Body = plan.Payload },
            Response = new() { Timestamp = DateTime.UtcNow, ModelId = $"{ProviderId}/{plan.Payload["model"]}", Body = body }
        };

    private static SpeechPlan PrepareSpeech(SpeechRequest request, bool streaming, int? outputRate = null)
    {
        var (model, modelVoice) = ParseModel(request.Model);
        var options = ReadOptions(request.ProviderOptions);
        var voice = modelVoice ?? request.Voice ?? GetStringOption(options, "voice");
        var warnings = new List<object>();
        void Ignore(string feature, string reason) => warnings.Add(new { type = "ignored", feature, reason });
        if (modelVoice is not null && request.Voice is not null && request.Voice != modelVoice)
            Ignore("voice", "Voice is derived from the model id.");
        if (modelVoice is not null && GetStringOption(options, "voice") is { } optionVoice && optionVoice != modelVoice)
            Ignore("providerOptions.kalpalabs.voice", "Voice is derived from the model id.");
        if (request.Instructions is not null)
            Ignore("instructions", "KalpaLabs has no instruction parameter.");
        if (request.Speed is not null)
            Ignore("speed", "KalpaLabs has no speed parameter.");
        if (request.Language is not null)
            Ignore("language", "KalpaLabs infers language from the text; model languages are listed in discovery.");
        var optionRate = GetInt32Option(options, "sample_rate");
        if (outputRate is not null && optionRate is not null && outputRate != optionRate)
            Ignore("providerOptions.kalpalabs.sample_rate", "outputFormat.rate takes precedence.");

        var payload = new JsonObject();
        AddUnknownOptions(payload, options);
        payload["model"] = model;
        AddParams(payload, options);
        var rate = outputRate ?? optionRate ?? 24000;
        if (rate is not (8000 or 16000 or 22050 or 24000 or 32000 or 44100 or 48000))
            throw new ArgumentOutOfRangeException(nameof(request), "Unsupported KalpaLabs sample_rate.");
        var format = NormalizeFormat(request.OutputFormat, streaming);
        if (streaming && format != "pcm")
            throw new NotSupportedException("KalpaLabs HTTP streaming returns pcm_s16le only, not WAV or compressed audio.");

        if (voice is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(voice);
            if (ReadConversation(options) is not null || GetStringOption(options, "speaker") is not null)
                throw new ArgumentException("Named-voice TTS cannot be combined with conversation or speaker options.");
            ValidateText(request.Text, required: true);
            payload["text"] = request.Text;
            payload["sample_rate"] = rate;
            payload["output_format"] = streaming ? "pcm_s16le" : "wav";
            return new($"v1/tts/{Uri.EscapeDataString(voice)}{(streaming ? "/stream" : "")}", payload, rate, warnings);
        }

        if (rate != 24000)
            throw new NotSupportedException("KalpaLabs converse does not accept sample_rate; use a named voice for resampling.");
        var conversation = ReadConversation(options);
        if (conversation is null)
            ValidateText(request.Text, required: true);
        if (conversation is null)
        {
            conversation = new JsonArray();
            conversation.Add(CreateTurn(GetStringOption(options, "speaker") ?? "0", request.Text));
        }
        ValidateConversation(conversation);
        payload["conversation"] = conversation;
        return new($"v1/converse{(streaming ? "/stream" : "")}", payload, rate, warnings);
    }

    private static JsonObject ReadOptions(Dictionary<string, JsonElement>? providerOptions)
    {
        if (providerOptions?.TryGetValue(ProviderId, out var value) != true)
            return new JsonObject();
        if (value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("providerOptions.kalpalabs must be an object.");
        return JsonNode.Parse(value.GetRawText()) as JsonObject
            ?? throw new ArgumentException("providerOptions.kalpalabs must be an object.");
    }

    private static void AddUnknownOptions(JsonObject payload, JsonObject options)
    {
        foreach (var (name, value) in options)
            if (!IsMappedOption(name))
                payload[name] = value?.DeepClone();
    }

    private static bool IsMappedOption(string name)
        => name.Equals("voice", StringComparison.OrdinalIgnoreCase)
            || name.Equals("sample_rate", StringComparison.OrdinalIgnoreCase)
            || name.Equals("params", StringComparison.OrdinalIgnoreCase)
            || name.Equals("conversation", StringComparison.OrdinalIgnoreCase)
            || name.Equals("speaker", StringComparison.OrdinalIgnoreCase);

    private static JsonNode? GetOption(JsonObject options, string name)
        => options.LastOrDefault(property => property.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    private static string? GetStringOption(JsonObject options, string name)
    {
        var value = GetOption(options, name);
        return value is null ? null : value.Deserialize<string>(Json);
    }

    private static int? GetInt32Option(JsonObject options, string name)
    {
        var value = GetOption(options, name);
        return value is null ? null : value.Deserialize<int>(Json);
    }

    private static JsonObject? ReadParams(JsonObject options)
    {
        var value = GetOption(options, "params");
        if (value is null)
            return null;
        if (value is not JsonObject parameters)
            throw new JsonException("KalpaLabs params must be an object.");
        var normalized = (JsonObject)parameters.DeepClone();

        foreach (var name in new[] { "temperature", "acoustic_temperature" })
        {
            var parameter = GetOption(parameters, name);
            RemoveOption(normalized, name);
            if (parameter is not null)
            {
                var number = parameter.Deserialize<double>(Json);
                if (!double.IsFinite(number) || number < 0 || number > 1.5)
                    throw new ArgumentOutOfRangeException(nameof(options), "KalpaLabs temperatures must be between 0 and 1.5.");
                normalized[name] = number;
            }
        }

        var audioQuality = GetOption(parameters, "audio_quality");
        RemoveOption(normalized, "audio_quality");
        if (audioQuality is not null)
        {
            var quality = audioQuality.Deserialize<string>(Json);
            if (quality is not ("low" or "medium" or "high"))
                throw new ArgumentException("KalpaLabs audio_quality must be low, medium, or high.", nameof(options));
            normalized["audio_quality"] = quality;
        }
        return normalized;
    }

    private static void AddParams(JsonObject payload, JsonObject options)
    {
        var parameters = ReadParams(options);
        if (parameters is not null)
            payload["params"] = parameters;
    }

    private static JsonArray? ReadConversation(JsonObject options)
    {
        var value = GetOption(options, "conversation");
        if (value is null)
            return null;
        if (value is not JsonArray conversation)
            throw new JsonException("KalpaLabs conversation must be an array.");

        var normalized = new JsonArray();
        foreach (var valueTurn in conversation)
        {
            if (valueTurn is not JsonObject turn)
                throw new ArgumentException("Every KalpaLabs turn requires a speaker.");
            var speaker = GetOption(turn, "speaker");
            var text = GetOption(turn, "text");
            var audio = GetOption(turn, "audio_wav_b64");
            var normalizedTurn = (JsonObject)turn.DeepClone();
            RemoveOption(normalizedTurn, "speaker");
            RemoveOption(normalizedTurn, "text");
            RemoveOption(normalizedTurn, "audio_wav_b64");
            if (speaker is null && !HasOption(turn, "speaker"))
                normalizedTurn["speaker"] = "0";
            else
                normalizedTurn["speaker"] = speaker?.DeepClone();
            if (text is not null)
                normalizedTurn["text"] = text.DeepClone();
            if (audio is not null)
                normalizedTurn["audio_wav_b64"] = audio.DeepClone();
            normalized.Add(normalizedTurn);
        }
        return normalized;
    }

    private static bool HasOption(JsonObject options, string name)
        => options.Any(property => property.Key.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static void RemoveOption(JsonObject options, string name)
    {
        foreach (var key in options.Select(property => property.Key)
            .Where(key => key.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray())
            options.Remove(key);
    }

    private static JsonObject CreateTurn(string speaker, string? text = null, string? audio = null)
    {
        var turn = new JsonObject { ["speaker"] = speaker };
        if (text is not null)
            turn["text"] = text;
        if (audio is not null)
            turn["audio_wav_b64"] = audio;
        return turn;
    }

    private static void ValidateConversation(JsonArray conversation)
    {
        if (conversation.Count is < 1 or > 64)
            throw new ArgumentException("KalpaLabs conversations require 1 to 64 turns.");
        foreach (var turn in conversation)
        {
            if (turn is not JsonObject turnObject || string.IsNullOrWhiteSpace(GetStringOption(turnObject, "speaker")))
                throw new ArgumentException("Every KalpaLabs turn requires a speaker.");
            ValidateText(GetStringOption(turnObject, "text"), required: false);
            if (GetStringOption(turnObject, "audio_wav_b64") is { } audio)
                ValidateAudio(audio, pcm: false);
        }
    }

    private static void ValidateText(string? text, bool required)
    {
        if ((required && string.IsNullOrWhiteSpace(text)) || (text is not null && text.EnumerateRunes().Count() > 8000))
            throw new ArgumentException("KalpaLabs TTS requires non-whitespace text of at most 8,000 characters; conversation turn text may be null.");
    }

    private static byte[] ValidateAudio(string base64, bool pcm)
    {
        var bytes = Convert.FromBase64String(base64);
        if (bytes.Length == 0 || (pcm && bytes.Length % 2 != 0))
            throw new InvalidOperationException("KalpaLabs returned empty or incomplete audio.");
        return bytes;
    }

    private static string NormalizeFormat(string? format, bool streaming) => format?.ToLowerInvariant() switch
    {
        null => streaming ? "pcm" : "wav",
        "wav" or "audio/wav" or "audio/x-wav" => "wav",
        "pcm" or "pcm_s16le" or "audio/pcm" => "pcm",
        _ => throw new NotSupportedException($"KalpaLabs does not support audio format '{format}'.")
    };
}
