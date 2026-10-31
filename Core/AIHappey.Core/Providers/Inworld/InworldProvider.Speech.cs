using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Inworld;

public partial class InworldProvider
{
    private static readonly HashSet<string> InworldSpeechOptions = new(StringComparer.Ordinal)
    {
        "audioConfig", "language", "deliveryMode", "temperature", "timestampType",
        "timestampTransportStrategy", "applyTextNormalization", "enhanceGeneration",
        "synthesisContext", "voiceDesign", "enableCustomPronunciation",
        "pronunciationDictionarySettings", "lipsyncConfig"
    };

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var warnings = new List<object>();
        var prepared = PrepareInworldSpeech(request, streaming: false, warnings);
        using var message = InworldJsonRequest("tts/v1/voice", prepared.Payload);
        ApplyAuthHeader();
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureInworldSuccessAsync(response, "TTS", cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        ThrowInworldPayloadError(root);
        var audio = RequiredInworldString(root, "audioContent");
        _ = Convert.FromBase64String(audio);
        var metadata = InworldSpeechMetadata(root);
        var count = InworldProcessedCharacters(root);
        return new SpeechResponse
        {
            Audio = new() { Base64 = audio, Format = MapEncodingToFormat(prepared.Encoding), MimeType = MapEncodingToMimeType(prepared.Encoding) },
            Warnings = warnings,
            Request = new() { Body = prepared.Payload },
            Response = new() { Timestamp = DateTime.UtcNow, ModelId = BuildInworldSpeechResponseModelId(prepared.Model, prepared.Voice), Body = root.Clone(), Headers = response.GetHeaders() },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(metadata, InworldSpeechCost(prepared.Model, count))
        };
    }

    public async IAsyncEnumerable<StreamingSpeechPart> SpeechStreamingAsync(
        StreamingSpeechRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        if (request.Text is not { Length: > 0 })
            throw new ArgumentException("At least one text part is required.", nameof(request));

        // Parts are consecutive script fragments, not independent utterances.
        var text = new StringBuilder();
        JsonElement? selectedOptions = null;
        string? selectedVoice = null;
        foreach (var part in request.Text)
        {
            if (part is null || part.Type != "text" || part.Text is null)
                throw new ArgumentException("Speech parts must have type 'text' and non-null text.", nameof(request));
            var options = MergeInworldSpeechOptions(request.ProviderOptions, part.ProviderOptions);
            var voice = part.Voice ?? request.Voice;
            if (selectedOptions is not null && (voice != selectedVoice || !JsonElement.DeepEquals(selectedOptions.Value, options)))
                throw new NotSupportedException("Inworld HTTP speech streaming requires one effective voice and provider-options configuration for the entire script.");
            selectedOptions = options;
            selectedVoice = voice;
            text.Append(part.Text);
        }
        if (request.Speed is { } speed && (!double.IsFinite(speed) || speed < 0.5 || speed > 1.5))
            throw new ArgumentOutOfRangeException(nameof(request.Speed), "Speed must be between 0.5 and 1.5.");
        if (request.OutputFormat is not null && string.IsNullOrWhiteSpace(request.OutputFormat.Type))
            throw new ArgumentException("outputFormat.type is required.", nameof(request));
        var synthesis = new SpeechRequest
        {
            Model = request.Model, Text = text.ToString(), Voice = selectedVoice,
            OutputFormat = request.OutputFormat?.Type, Instructions = request.Instructions,
            Speed = request.Speed, Language = request.Language,
            ProviderOptions = new() { [GetIdentifier()] = selectedOptions!.Value }
        };
        var warnings = new List<object>();
        var prepared = PrepareInworldSpeech(synthesis, streaming: true, warnings,
            ProviderNumericConversions.ToInt32Exact(request.OutputFormat?.Rate, "outputFormat.rate"));
        using var message = InworldJsonRequest("tts/v1/voice:stream", prepared.Payload);
        ApplyAuthHeader();
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureInworldSuccessAsync(response, "streaming TTS", cancellationToken);
        yield return new SpeechStreamStartPart { Warnings = warnings };
        cancellationToken.ThrowIfCancellationRequested();
        yield return new SpeechResponseMetadataPart
        {
            Timestamp = DateTime.UtcNow, ModelId = BuildInworldSpeechResponseModelId(prepared.Model, prepared.Voice), Headers = response.GetHeaders()
        };

        long characters = 0;
        var hasAudio = false;
        var timestamps = new List<JsonElement>();
        string? reasoning = null;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await foreach (var envelope in ReadInworldSpeechStreamEnvelopesAsync(stream, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.IncludeRawChunks == true) yield return new SpeechRawPart { RawValue = envelope };
            ThrowInworldPayloadError(envelope);
            if (!envelope.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Inworld streaming TTS returned no result object.");
            characters = checked(characters + InworldProcessedCharacters(result));
            if (result.TryGetProperty("timestampInfo", out var timestamp)) timestamps.Add(timestamp.Clone());
            if (result.TryGetProperty("reasoningInstruction", out var instruction) && instruction.ValueKind == JsonValueKind.String)
                reasoning = instruction.GetString();
            var audio = result.TryGetProperty("audioContent", out var content) && content.ValueKind == JsonValueKind.String ? content.GetString() : null;
            if (!string.IsNullOrEmpty(audio))
            {
                _ = Convert.FromBase64String(audio);
                hasAudio = true;
            }
            // Empty audio may carry trailing alignment; do not lose it or stop reading early.
            if (!string.IsNullOrEmpty(audio) || result.TryGetProperty("timestampInfo", out _) || result.TryGetProperty("reasoningInstruction", out _))
                yield return new SpeechAudioPart
                {
                    Audio = audio ?? "", AudioFormat = new() { Type = MapEncodingToMimeType(prepared.Encoding), Rate = prepared.Rate },
                    ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(InworldSpeechMetadata(result))
                };
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!hasAudio) throw new InvalidOperationException("Inworld streaming TTS ended without audio.");
        var usage = new { processedCharactersCount = characters, modelId = prepared.Model };
        yield return new SpeechFinishPart
        {
            // The normalized contract counts code points; Inworld's metered UTF-16 count stays in raw usage.
            Usage = new() { InputCharacters = synthesis.Text.EnumerateRunes().LongCount(), Raw = new() { ["processedCharactersCount"] = JsonSerializer.SerializeToElement(characters), ["modelId"] = JsonSerializer.SerializeToElement(prepared.Model) } },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new { usage, timestampInfo = timestamps, reasoningInstruction = reasoning }, InworldSpeechCost(prepared.Model, characters))
        };
    }

    private (JsonObject Payload, string Model, string? Voice, string Encoding, int? Rate) PrepareInworldSpeech(
        SpeechRequest request, bool streaming, List<object> warnings, int? sampleRate = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
        var (model, shortcutVoice) = ParseInworldSpeechModelAndVoice(request.Model);
        var payload = JsonSerializer.SerializeToNode(MergeInworldSpeechOptions(request.ProviderOptions, null))!.AsObject();
        foreach (var key in payload.Select(p => p.Key))
            if (!InworldSpeechOptions.Contains(key)) throw new ArgumentException($"Unsupported Inworld speech option '{key}'.");
        var voice = (shortcutVoice ?? request.Voice)?.Trim();
        if (payload["voiceDesign"] is not null)
        {
            if (!string.IsNullOrWhiteSpace(voice)) throw new ArgumentException("voiceDesign and voiceId are mutually exclusive.");
            var prompt = payload["voiceDesign"]?["designPrompt"]?.GetValue<string>();
            if (prompt is null || prompt.Trim().Length < 7 || prompt.Length > 1024)
                throw new ArgumentException("voiceDesign.designPrompt must contain 7–1,024 characters (excluding surrounding whitespace for the minimum).");
        }
        else if (string.IsNullOrWhiteSpace(voice)) throw new ArgumentException("Inworld TTS requires a voice or voiceDesign.");
        else payload["voiceId"] = voice;
        if (shortcutVoice is not null && request.Voice is not null && request.Voice.Trim() != shortcutVoice)
            warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });

        var text = request.Text;
        if (!string.IsNullOrWhiteSpace(request.Instructions))
        {
            if (model == "inworld-tts-2") text = $"[{request.Instructions}] {text}";
            else warnings.Add(new { type = "unsupported", feature = "instructions", reason = "Steering requires inworld-tts-2." });
        }
        var limit = streaming ? 4000 : 2000;
        if (text.Length > limit) throw new ArgumentException($"Inworld TTS text must not exceed {limit} UTF-16 code units, including steering tags.");
        payload["text"] = text;
        payload["modelId"] = model;
        if (!string.IsNullOrWhiteSpace(request.Language)) payload["language"] = request.Language;
        var audio = payload["audioConfig"] as JsonObject ?? new JsonObject();
        var encoding = NormalizeAudioEncoding(request.OutputFormat ?? audio["audioEncoding"]?.GetValue<string>()) ?? "MP3";
        audio["audioEncoding"] = encoding;
        if (sampleRate is not null) audio["sampleRateHertz"] = sampleRate.Value;
        if (request.Speed is not null)
        {
            if (audio["speakingRate"] is not null) warnings.Add(new { type = "override", feature = "speakingRate", detail = "Request speed overrides audioConfig.speakingRate." });
            audio["speakingRate"] = request.Speed.Value;
        }
        if (audio["speakingRate"] is JsonNode speedNode && (speedNode.GetValue<double>() is var speed && (!double.IsFinite(speed) || speed < 0.5 || speed > 1.5)))
            throw new ArgumentOutOfRangeException(nameof(request.Speed), "speakingRate must be between 0.5 and 1.5.");
        var rate = audio["sampleRateHertz"]?.GetValue<int>();
        if (rate is < 8000 or > 48000) throw new ArgumentOutOfRangeException(nameof(sampleRate), "sampleRateHertz must be between 8000 and 48000.");
        if (audio["bitRate"] is JsonNode bitRate && bitRate.GetValue<int>() <= 0) throw new ArgumentOutOfRangeException("bitRate");
        payload["audioConfig"] = audio;
        if (!streaming && payload["timestampTransportStrategy"] is not null)
            throw new ArgumentException("timestampTransportStrategy is only supported by streaming synthesis.");
        ValidateInworldSpeechOptions(payload, model);
        return (payload, model, shortcutVoice, encoding, rate ?? (encoding is "ALAW" or "MULAW" ? 8000 : model.StartsWith("inworld-tts-2", StringComparison.Ordinal) ? 48000 : null));
    }

    private static void ValidateInworldSpeechOptions(JsonObject payload, string model)
    {
        foreach (var (key, allowed) in new (string, string[])[]
        {
            ("deliveryMode", ["DELIVERY_MODE_UNSPECIFIED", "STABLE", "BALANCED", "CREATIVE"]),
            ("timestampType", ["TIMESTAMP_TYPE_UNSPECIFIED", "WORD", "CHARACTER"]),
            ("timestampTransportStrategy", ["TIMESTAMP_TRANSPORT_STRATEGY_UNSPECIFIED", "SYNC", "ASYNC"]),
            ("applyTextNormalization", ["APPLY_TEXT_NORMALIZATION_UNSPECIFIED", "ON", "OFF"])
        })
            if (payload[key] is JsonNode node && !allowed.Contains(node.GetValue<string>(), StringComparer.Ordinal))
                throw new ArgumentException($"Invalid Inworld {key}.");
        if (payload["temperature"] is JsonNode temperature && (!double.IsFinite(temperature.GetValue<double>()) || temperature.GetValue<double>() < 0 || temperature.GetValue<double>() > 2))
            throw new ArgumentOutOfRangeException("temperature", "temperature must be between 0 and 2 (0 uses the default).");
        if (payload["enableCustomPronunciation"] is not null && payload["pronunciationDictionarySettings"] is not null)
            throw new ArgumentException("enableCustomPronunciation and pronunciationDictionarySettings cannot be combined, even when false.");
        if (payload["enableCustomPronunciation"]?.GetValue<bool>() == true || payload["pronunciationDictionarySettings"] is not null)
        {
            if (model != "inworld-tts-2" || string.IsNullOrWhiteSpace(payload["language"]?.GetValue<string>()))
                throw new ArgumentException("Saved pronunciations require inworld-tts-2 and an explicit language.");
        }
        if (payload["pronunciationDictionarySettings"] is JsonNode settings)
        {
            var dictionaries = settings["dictionaries"] as JsonArray;
            if (dictionaries is null || dictionaries.Count is < 1 or > 3)
                throw new ArgumentException("Select one to three pronunciation dictionaries.");
            var names = dictionaries.Select(d => d?["dictionary"]?.GetValue<string>()).ToArray();
            if (names.Any(n => string.IsNullOrWhiteSpace(n) || !n.StartsWith("workspaces/", StringComparison.Ordinal) || !n.Contains("/pronunciationDictionaries/", StringComparison.Ordinal)) || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
                throw new ArgumentException("Pronunciation dictionaries must be distinct workspace resource names.");
        }
        if (payload["voiceDesign"] is not null && model != "inworld-tts-2") throw new ArgumentException("voiceDesign requires inworld-tts-2.");
        if (payload["lipsyncConfig"]?["visemeSet"] is JsonNode visemes && visemes.GetValue<string>() != "OPENXR") throw new ArgumentException("Only OPENXR visemes are supported.");
        if (payload["synthesisContext"] is JsonNode context)
        {
            var previous = context["previousRequests"] as JsonArray;
            if (previous is not null && (previous.Count > 5 || previous.Sum(p => p?["text"]?.GetValue<string>()?.Length ?? 0) > 2000))
                throw new ArgumentException("synthesisContext permits at most five earlier requests and 2,000 total characters.");
            if (context["reasoningContext"]?.GetValue<string>()?.Length > 600) throw new ArgumentException("reasoningContext must not exceed 600 characters.");
            if (context["reasoning"] is JsonNode reasoning && reasoning.GetValue<string>() is not ("REASONING_UNSPECIFIED" or "REASONING_ON")) throw new ArgumentException("Invalid synthesis reasoning mode.");
            if (context["reasoning"]?.GetValue<string>() == "REASONING_ON" && model != "inworld-tts-2") throw new ArgumentException("Synthesis reasoning requires inworld-tts-2.");
        }
    }

    private static JsonElement MergeInworldSpeechOptions(Dictionary<string, JsonElement>? defaults, Dictionary<string, JsonElement>? overrides)
    {
        var merged = new JsonObject();
        foreach (var options in new[] { defaults, overrides })
            if (options?.TryGetValue("inworld", out var native) == true)
            {
                if (native.ValueKind != JsonValueKind.Object) throw new ArgumentException("providerOptions.inworld must be an object.");
                foreach (var property in native.EnumerateObject()) merged[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            }
        return JsonSerializer.SerializeToElement(merged);
    }

    private static (string ModelId, string? VoiceId) ParseInworldSpeechModelAndVoice(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var raw = StripInworldProviderPrefix(model.Trim());
        var slash = raw.IndexOf('/');
        if (slash < 0) return (raw, null);
        if (slash == 0 || slash == raw.Length - 1) throw new ArgumentException("Speech shortcuts require '<model>/<voiceId>'.", nameof(model));
        return (raw[..slash], raw[(slash + 1)..]);
    }

    private string BuildInworldSpeechResponseModelId(string modelId, string? voiceId)
        => (voiceId is null ? modelId : $"{modelId}/{voiceId}").ToModelId(GetIdentifier());

    private decimal? InworldSpeechCost(string model, long count)
        => GetIdentifier().GetPricing()?.TryGetValue(model.ToModelId(GetIdentifier()), out var pricing) == true ? count * pricing.Input : null;

    private static Dictionary<string, JsonElement> InworldSpeechMetadata(JsonElement result)
        => result.EnumerateObject().Where(p => p.Name != "audioContent").ToDictionary(p => p.Name, p => p.Value.Clone());

    private static long InworldProcessedCharacters(JsonElement result)
        => result.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("processedCharactersCount", out var count) && count.TryGetInt64(out var value) && value >= 0 ? value : 0;

    private static string? NormalizeAudioEncoding(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        null or "" => null,
        "MP3" or "MPEG" or "AUDIO/MPEG" or "AUDIO/MP3" => "MP3",
        "OGG_OPUS" or "OPUS" or "OGG" or "AUDIO/OGG" or "AUDIO/OPUS" => "OGG_OPUS",
        "PCM" or "AUDIO/PCM" => "PCM",
        "WAV" or "AUDIO/WAV" or "AUDIO/X-WAV" => "WAV",
        "LINEAR16" => "LINEAR16",
        "FLAC" or "AUDIO/FLAC" => "FLAC",
        "ALAW" or "AUDIO/ALAW" or "AUDIO/PCMA" => "ALAW",
        "MULAW" or "MU_LAW" or "MU-LAW" or "AUDIO/MULAW" or "AUDIO/PCMU" => "MULAW",
        _ => throw new NotSupportedException($"Unsupported Inworld audio encoding '{value}'.")
    };

    private static string MapEncodingToFormat(string encoding) => encoding switch
    {
        "OGG_OPUS" => "opus", "LINEAR16" or "WAV" => "wav", _ => encoding.ToLowerInvariant()
    };

    private static string MapEncodingToMimeType(string encoding) => encoding switch
    {
        "MP3" => "audio/mpeg", "OGG_OPUS" => "audio/ogg", "LINEAR16" or "WAV" => "audio/wav",
        "PCM" => "audio/pcm", "FLAC" => "audio/flac", "ALAW" => "audio/pcma", "MULAW" => "audio/pcmu",
        _ => throw new NotSupportedException($"Unsupported Inworld audio encoding '{encoding}'.")
    };

    private static HttpRequestMessage InworldJsonRequest(string path, JsonObject payload)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json") };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return message;
    }

    private static async Task EnsureInworldSuccessAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Inworld {operation} failed (HTTP {(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(cancellationToken)}");
    }

    private static string RequiredInworldString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! : throw new InvalidOperationException($"Inworld response missing '{name}'.");

    private static void ThrowInworldPayloadError(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Inworld returned a non-object response.");
        if (payload.TryGetProperty("error", out var error) && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            throw new InvalidOperationException($"Inworld request failed: {error.GetRawText()}");
        if (payload.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.GetInt32() != 0)
            throw new InvalidOperationException($"Inworld request failed: {payload.GetRawText()}");
    }

    private static async IAsyncEnumerable<JsonElement> ReadInworldSpeechStreamEnvelopesAsync(
        Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var document = JsonDocument.Parse(line);
            yield return document.RootElement.Clone();
        }
    }
}
