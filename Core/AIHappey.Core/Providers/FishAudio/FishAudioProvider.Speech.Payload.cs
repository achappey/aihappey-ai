using System.Text.Json;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.FishAudio;

public partial class FishAudioProvider
{
    private Dictionary<string, object?> BuildFishSpeechPayload(SpeechRequest request, int? rate = null, bool streaming = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
        if (IsAgentModel(request.Model)) throw new ArgumentException("A Fish Audio TTS model is required.");
        var options = FishOptions(request.ProviderOptions);
        if (options.TryGetValue("references", out var references) && references.ValueKind != JsonValueKind.Null)
            throw new NotSupportedException("Inline Fish Audio references require binary MessagePack; use reference_id voice models with JSON.");
        string[] allowed = ["reference_id", "references", "prosody", "language", "format", "sample_rate", "mp3_bitrate", "opus_bitrate", "temperature", "top_p", "chunk_length", "normalize", "latency", "max_new_tokens", "repetition_penalty", "min_chunk_length", "condition_on_previous_chunks", "early_stop_threshold", "features", "pronunciation_dictionary"];
        if (options.Keys.Any(k => !allowed.Contains(k))) throw new ArgumentException("Unsupported Fish Audio speech option.");
        var payload = options.ToDictionary(p => p.Key, p => (object?)p.Value.Clone(), StringComparer.Ordinal);
        payload["text"] = request.Text;
        var format = NormalizeSpeechFormat(request.OutputFormat ?? (options.TryGetValue("format", out var fmt) ? fmt.GetString() : null));
        payload["format"] = format;
        if (request.Voice is not null) payload["reference_id"] = request.Voice;
        if (request.Language is not null) payload["language"] = request.Language;
        if (streaming && !payload.ContainsKey("latency")) payload["latency"] = "balanced";
        if (request.Speed is not null)
        {
            if (!double.IsFinite(request.Speed.Value) || request.Speed < .5 || request.Speed > 2)
                throw new ArgumentException("Fish Audio speed must be between 0.5 and 2.0.");
            var prosody = options.TryGetValue("prosody", out var pro) && pro.ValueKind == JsonValueKind.Object
                ? pro.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone()) : new Dictionary<string, object?>();
            prosody["speed"] = request.Speed;
            payload["prosody"] = prosody;
        }
        var sampleRate = rate ?? (options.TryGetValue("sample_rate", out var sr) && sr.ValueKind != JsonValueKind.Null ? sr.GetInt32() : (int?)null);
        if (sampleRate is not null)
        {
            int[] rates = format switch { "opus" => [48000], "mp3" => [32000, 44100], _ => [8000, 16000, 24000, 32000, 44100] };
            if (!rates.Contains(sampleRate.Value)) throw new ArgumentException("Fish Audio sample rate is not supported for the selected format.");
            payload["sample_rate"] = sampleRate.Value;
        }
        return payload;
    }
}
