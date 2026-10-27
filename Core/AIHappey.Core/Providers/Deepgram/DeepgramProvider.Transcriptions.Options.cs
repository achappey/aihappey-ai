using System.Globalization;
using System.Text.Json;

namespace AIHappey.Core.Providers.Deepgram;

public sealed partial class DeepgramProvider
{
    private sealed class ListenOptions
    {
        private static readonly HashSet<string> BatchBooleans = Set("sentiment topics intents detect_entities diarize dictation filler_words measurements multichannel numerals paragraphs profanity_filter punctuate smart_format utterances mip_opt_out");
        private static readonly HashSet<string> StreamBooleans = Set("diarize detect_entities dictation interim_results mip_opt_out multichannel numerals profanity_filter punctuate smart_format vad_events");
        private static readonly HashSet<string> Lists = Set("tag extra custom_topic custom_intent keyterm keywords language_hint redact replace search");
        private static readonly HashSet<string> StreamKeys = Set("channels encoding sample_rate language language_hint model diarize_model diarize detect_entities dictation endpointing extra interim_results keyterm keywords mip_opt_out multichannel numerals profanity_filter punctuate redact replace search smart_format tag utterance_end_ms vad_events version timestamp_granularities");
        private static readonly HashSet<string> BatchKeys = Set("model language diarize_model utt_split version encoding sample_rate channels detect_language summarize timestamp_granularities");
        private readonly Dictionary<string, JsonElement> _values = new(StringComparer.Ordinal);
        private readonly List<KeyValuePair<string, string>> _query = [];
        public List<object> Warnings { get; } = [];
        public bool WordOnly { get; private set; }
        public string Query => string.Join("&", _query.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
        private static HashSet<string> Set(string value) => new(value.Split(' '), StringComparer.Ordinal);

        public static ListenOptions Create(string model, Dictionary<string, JsonElement>? providerOptions, bool streaming)
        {
            if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("Model is required.");
            var result = new ListenOptions();
            if (providerOptions?.TryGetValue("deepgram", out var raw) == true)
            {
                if (raw.ValueKind != JsonValueKind.Object) throw new ArgumentException("providerOptions.deepgram must be an object.");
                foreach (var property in raw.EnumerateObject())
                    if (!result._values.TryAdd(property.Name, property.Value))
                        throw new ArgumentException($"Duplicate Deepgram option '{property.Name}'.");
            }
            result._query.Add(new("model", model));
            var booleans = streaming ? StreamBooleans : BatchBooleans;
            foreach (var (key, value) in result._values)
            {
                if (key.StartsWith("callback", StringComparison.Ordinal))
                    throw new NotSupportedException("Deepgram callbacks cannot return synchronous transcription results.");
                if (streaming ? !StreamKeys.Contains(key) : !BatchKeys.Contains(key) && !BatchBooleans.Contains(key) && !Lists.Contains(key))
                    throw new NotSupportedException($"Deepgram {(streaming ? "streaming" : "REST")} option '{key}' is not supported.");
                if (key == "timestamp_granularities")
                {
                    if (value.ValueKind != JsonValueKind.Array) throw Invalid(key);
                    var values = Strings(value, key).ToArray();
                    if (values.Any(v => v is not "word" and not "segment")) throw Invalid(key);
                    result.WordOnly = values.Contains("word") && !values.Contains("segment");
                    if (!streaming && values.Contains("segment"))
                    {
                        if (result._values.TryGetValue("utterances", out var utterances) && utterances.ValueKind == JsonValueKind.False)
                            throw new ArgumentException("Segment timestamps require utterances=true.");
                        if (!result._values.ContainsKey("utterances")) result._query.Add(new("utterances", "true"));
                    }
                    continue;
                }
                if (key == "model")
                {
                    if (value.ValueKind != JsonValueKind.String || value.GetString() != model) throw Invalid(key);
                    continue;
                }
                if (key == "diarize_model")
                {
                    if (value.ValueKind != JsonValueKind.String || value.GetString() is not ("latest" or "v1" or "v2")
                        || streaming && value.GetString() == "v2") throw Invalid(key);
                }
                if (key == "keyterm" && !model.StartsWith("nova-3", StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException("Deepgram keyterm requires Nova-3.");
                if (key == "keywords" && model.StartsWith("nova-3", StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException("Deepgram keywords is not supported by Nova-3; use plain keyterm terms.");
                if (key == "language_hint" && (!model.StartsWith("nova-3", StringComparison.OrdinalIgnoreCase)
                    || !result._values.TryGetValue("language", out var language) || language.ValueKind != JsonValueKind.String || language.GetString() != "multi"))
                    throw new NotSupportedException("language_hint requires Nova-3 with language=multi.");
                if (key == "diarize") result.Warnings.Add(new { type = "deprecated", feature = "diarize", message = "Prefer diarize_model." });
                if (booleans.Contains(key) || (key is "detect_language" or "summarize" or "endpointing")
                    && (value.ValueKind is JsonValueKind.True or JsonValueKind.False))
                {
                    if (value.ValueKind is not JsonValueKind.True and not JsonValueKind.False) throw Invalid(key);
                    result._query.Add(new(key, value.GetBoolean() ? "true" : "false"));
                }
                else if (Lists.Contains(key) || key == "detect_language")
                {
                    if (key == "detect_language" && value.ValueKind != JsonValueKind.Array) throw Invalid(key);
                    foreach (var text in Strings(value, key))
                    {
                        if (key == "keyterm" && text.Contains(':')) throw new ArgumentException("keyterm accepts plain terms, not boosts.");
                        result._query.Add(new(key, text));
                    }
                }
                else if (key is "sample_rate" or "channels" or "utterance_end_ms" or "endpointing" or "utt_split")
                {
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number)
                        || number < 0 || key != "utt_split" && (number != Math.Truncate(number) || key != "endpointing" && number == 0)) throw Invalid(key);
                    result._query.Add(new(key, number.ToString("R", CultureInfo.InvariantCulture)));
                }
                else
                {
                    if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())
                        || key == "summarize" && value.GetString() != "v2") throw Invalid(key);
                    result._query.Add(new(key, value.GetString()!));
                }
            }
            return result;
        }

        public void ValidateAudio(string type, int? rate)
        {
            var raw = type.ToLowerInvariant() is "pcm" or "pcm16" or "linear16" or "audio/pcm" or "audio/raw" or "audio/l16";
            if (rate.HasValue && rate <= 0) throw new ArgumentException("Audio rate must be positive.");
            if (rate.HasValue)
            {
                if (_values.TryGetValue("sample_rate", out var sampleRate) && sampleRate.GetInt32() != rate.Value)
                    throw new ArgumentException("sample_rate must match inputAudioFormat.rate.");
                if (raw && !_values.ContainsKey("sample_rate")) _query.Add(new("sample_rate", rate.Value.ToString(CultureInfo.InvariantCulture)));
            }
            if (!raw) return; // Container audio must not be forced to use a raw encoding.
            if (!_values.ContainsKey("sample_rate") && rate == null || !_values.ContainsKey("channels"))
                throw new ArgumentException("Headerless PCM requires sample_rate (or input rate) and channels.");
            if (!_values.ContainsKey("encoding")) _query.Add(new("encoding", "linear16"));
            else if (_values["encoding"].GetString() != "linear16") throw new ArgumentException("PCM16 requires encoding=linear16.");
        }

        private static IEnumerable<string> Strings(JsonElement value, string key)
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                if (string.IsNullOrWhiteSpace(value.GetString())) throw Invalid(key);
                yield return value.GetString()!;
            }
            else if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0)
            {
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString())) throw Invalid(key);
                    yield return item.GetString()!;
                }
            }
            else throw Invalid(key);
        }
        private static ArgumentException Invalid(string key) => new($"Invalid Deepgram option '{key}'.");
    }
}
