using System.Text.Json;
using AIHappey.Common.Model.Providers.KalpaLabs;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.KalpaLabs;

public partial class KalpaLabsProvider
{
    private sealed record SpeechPlan(string Path, Dictionary<string, object?> Payload, int Rate, List<object> Warnings);

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
        var voice = modelVoice ?? request.Voice ?? options.Voice;
        var warnings = new List<object>();
        void Ignore(string feature, string reason) => warnings.Add(new { type = "ignored", feature, reason });
        if (modelVoice is not null && request.Voice is not null && request.Voice != modelVoice)
            Ignore("voice", "Voice is derived from the model id.");
        if (modelVoice is not null && options.Voice is not null && options.Voice != modelVoice)
            Ignore("providerOptions.kalpalabs.voice", "Voice is derived from the model id.");
        if (request.Instructions is not null)
            Ignore("instructions", "KalpaLabs has no instruction parameter.");
        if (request.Speed is not null)
            Ignore("speed", "KalpaLabs has no speed parameter.");
        if (request.Language is not null)
            Ignore("language", "KalpaLabs infers language from the text; model languages are listed in discovery.");
        if (outputRate is not null && options.SampleRate is not null && outputRate != options.SampleRate)
            Ignore("providerOptions.kalpalabs.sample_rate", "outputFormat.rate takes precedence.");

        var payload = new Dictionary<string, object?> { ["model"] = model };
        AddParams(payload, options.Params);
        var rate = outputRate ?? options.SampleRate ?? 24000;
        if (rate is not (8000 or 16000 or 22050 or 24000 or 32000 or 44100 or 48000))
            throw new ArgumentOutOfRangeException(nameof(request), "Unsupported KalpaLabs sample_rate.");
        var format = NormalizeFormat(request.OutputFormat, streaming);
        if (streaming && format != "pcm")
            throw new NotSupportedException("KalpaLabs HTTP streaming returns pcm_s16le only, not WAV or compressed audio.");

        if (voice is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(voice);
            if (options.Conversation is not null || options.Speaker is not null)
                throw new ArgumentException("Named-voice TTS cannot be combined with conversation or speaker options.");
            ValidateText(request.Text, required: true);
            payload["text"] = request.Text;
            payload["sample_rate"] = rate;
            payload["output_format"] = streaming ? "pcm_s16le" : "wav";
            return new($"v1/tts/{Uri.EscapeDataString(voice)}{(streaming ? "/stream" : "")}", payload, rate, warnings);
        }

        if (rate != 24000)
            throw new NotSupportedException("KalpaLabs converse does not accept sample_rate; use a named voice for resampling.");
        if (options.Conversation is null)
            ValidateText(request.Text, required: true);
        var conversation = options.Conversation ?? [new KalpaLabsTurn { Speaker = options.Speaker ?? "0", Text = request.Text }];
        ValidateConversation(conversation);
        payload["conversation"] = conversation;
        return new($"v1/converse{(streaming ? "/stream" : "")}", payload, rate, warnings);
    }

    private static KalpaLabsProviderMetadata ReadOptions(Dictionary<string, JsonElement>? providerOptions)
    {
        if (providerOptions?.TryGetValue(ProviderId, out var value) != true)
            return new();
        if (value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("providerOptions.kalpalabs must be an object.");
        return value.Deserialize<KalpaLabsProviderMetadata>(Json)
            ?? throw new ArgumentException("providerOptions.kalpalabs must be an object.");
    }

    private static void AddParams(Dictionary<string, object?> payload, KalpaLabsGenerationParams? parameters)
    {
        if (parameters is null)
            return;
        foreach (var value in new[] { parameters.Temperature, parameters.AcousticTemperature })
            if (value is { } number && (!double.IsFinite(number) || number < 0 || number > 1.5))
                throw new ArgumentOutOfRangeException(nameof(parameters), "KalpaLabs temperatures must be between 0 and 1.5.");
        if (parameters.AudioQuality is not null and not ("low" or "medium" or "high"))
            throw new ArgumentException("KalpaLabs audio_quality must be low, medium, or high.", nameof(parameters));
        payload["params"] = parameters;
    }

    private static void ValidateConversation(List<KalpaLabsTurn> conversation)
    {
        if (conversation.Count is < 1 or > 64)
            throw new ArgumentException("KalpaLabs conversations require 1 to 64 turns.");
        foreach (var turn in conversation)
        {
            if (turn is null || string.IsNullOrWhiteSpace(turn.Speaker))
                throw new ArgumentException("Every KalpaLabs turn requires a speaker.");
            ValidateText(turn.Text, required: false);
            if (turn.AudioWavBase64 is not null)
                ValidateAudio(turn.AudioWavBase64, pcm: false);
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
