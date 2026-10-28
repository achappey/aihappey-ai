using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Zoom;

public sealed partial class ZoomProvider
{
    private const int MaximumAudioBytes = 100_000_000;
    private static readonly HashSet<string> AudioMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "audio/wav", "audio/x-wav", "audio/wave", "audio/mpeg", "audio/mp3",
        "audio/mp4", "audio/m4a", "audio/x-m4a", "audio/flac", "audio/x-flac",
        "audio/ogg", "audio/webm", "video/webm", "video/mp4", "video/quicktime"
    };

    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var model = NativeModel(request.Model);
        if (model != "scribe" && !model.StartsWith("zoom-scribe-", StringComparison.Ordinal))
            throw new NotSupportedException($"Zoom Scribe does not support model '{request.Model}'.");
        var audio = NormalizeAudio(request);
        var options = Options(request.ProviderOptions?.TryGetValue(GetIdentifier(), out var scoped) == true ? scoped : null);
        RequireOnly(options, "language", "timestamps", "word_time_offsets", "channel_separation", "diarization",
            "profanity_filter", "language_detection", "output_format", "model", "vocabulary", "vocabulary_id", "outputs");
        var config = new JsonObject { ["timestamps"] = true, ["output_format"] = "json" };
        foreach (var option in options.Where(option => option.Key != "outputs"))
            config[option.Key] = option.Value?.DeepClone();
        if (model != "scribe")
        {
            if (config["model"] is { } configured && configured.GetValue<string>() != model)
                throw new ArgumentException("Zoom Scribe config.model conflicts with the selected model.");
            config["model"] = model;
        }
        ValidateScribeConfig(config);
        var payload = new JsonObject { ["file"] = audio, ["config"] = config };
        if (options.ContainsKey("outputs"))
        {
            if (options["outputs"] is not JsonObject outputs
                || outputs["text_polishing"] is not JsonObject polishing
                || polishing["enabled"] is not JsonValue enabled || !enabled.TryGetValue<bool>(out _))
                throw new ArgumentException("outputs.text_polishing.enabled must be a boolean.");
            RequireOnly(outputs, "text_polishing");
            RequireOnly(polishing, "enabled");
            payload["outputs"] = outputs.DeepClone();
        }
        var timestamp = DateTime.UtcNow;
        var (body, _) = await SendAsync("aiservices/scribe/transcribe", payload, false, null, cancellationToken);
        try
        {
            RequiredString(body, "request_id");
            var responseModel = body.TryGetProperty("model", out _) ? RequiredString(body, "model") : model;
            var duration = Seconds(body, "duration_sec");
            if (duration > 7200)
                throw new JsonException("Zoom Scribe returned a duration exceeding its two-hour limit.");
            var result = body.GetProperty("result");
            if (!result.TryGetProperty("text_display", out var text) || text.ValueKind != JsonValueKind.String)
                throw new JsonException("Zoom Scribe response requires result.text_display.");
            var segments = new List<TranscriptionSegment>();
            if (result.TryGetProperty("segments", out var nativeSegments))
            {
                foreach (var segment in nativeSegments.EnumerateArray())
                {
                    var start = Seconds(segment, "start");
                    var end = Seconds(segment, "end");
                    if (end < start)
                        throw new JsonException("Zoom segment end precedes its start.");
                    segments.Add(new()
                    {
                        Text = segment.GetProperty("text").GetString() ?? throw new JsonException("Missing segment text."),
                        StartSecond = (float)start,
                        EndSecond = (float)end
                    });
                    foreach (var word in segment.TryGetProperty("words", out var words) ? words.EnumerateArray() : [])
                    {
                        if (word.GetProperty("word").ValueKind != JsonValueKind.String || Seconds(word, "end") < Seconds(word, "start"))
                            throw new JsonException("Invalid Zoom word timestamps.");
                    }
                }
            }
            return new()
            {
                Text = text.GetString()!,
                Language = result.TryGetProperty("language", out var language) && language.ValueKind == JsonValueKind.String
                    ? language.GetString() : config["language"]?.GetValue<string>(),
                DurationInSeconds = (float)duration,
                Segments = segments,
                ProviderMetadata = new() { [GetIdentifier()] = body },
                Response = new()
                {
                    Timestamp = timestamp,
                    ModelId = $"zoom/{responseModel}",
                    Body = body
                }
            };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new InvalidOperationException("Zoom returned an invalid Scribe response.", exception);
        }
    }

    private static void ValidateScribeConfig(JsonObject config)
    {
        foreach (var field in new[] { "timestamps", "word_time_offsets", "channel_separation", "diarization", "profanity_filter" })
            if (config.ContainsKey(field) && (config[field] is not JsonValue value || !value.TryGetValue<bool>(out _)))
                throw new ArgumentException($"Zoom Scribe {field} must be a boolean.");
        foreach (var field in new[] { "language", "output_format", "vocabulary_id" })
            if (config.ContainsKey(field) && (config[field] is not JsonValue value
                || !value.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text)))
                throw new ArgumentException($"Zoom Scribe {field} must be a nonempty string.");
        if (config["model"] is { } model && (model is not JsonValue modelValue
            || !modelValue.TryGetValue<string>(out var modelName) || string.IsNullOrWhiteSpace(modelName)))
            throw new ArgumentException("Zoom Scribe model must be a nonempty string or null for automatic selection.");
        if (config["output_format"]!.GetValue<string>() != "json")
            throw new NotSupportedException("Zoom Scribe output_format supports only json; srt/vtt are reserved by Zoom.");
        if (config.ContainsKey("language_detection"))
        {
            if (config["language_detection"] is not JsonObject detection
                || detection["mode"] is not JsonValue mode || !mode.TryGetValue<string>(out var modeName)
                || modeName is not ("once" or "continuous"))
                throw new ArgumentException("Zoom Scribe language_detection.mode must be once or continuous.");
            RequireOnly(detection, "mode", "candidate_languages");
            if (detection.ContainsKey("candidate_languages")
                && (detection["candidate_languages"] is not JsonArray languages || languages.Count == 0
                    || languages.Any(language => language is not JsonValue value
                        || !value.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text))))
                throw new ArgumentException("Zoom Scribe candidate_languages must be a nonempty string array.");
        }
        if (config.ContainsKey("vocabulary"))
        {
            if (config["vocabulary"] is not JsonObject vocabulary || vocabulary["phrases"] is not JsonArray phrases
                || phrases.Count > 100 || phrases.Any(phrase => phrase is not JsonValue value || !value.TryGetValue<string>(out _)))
                throw new ArgumentException("Zoom Scribe vocabulary.phrases supports up to 100 strings.");
            RequireOnly(vocabulary, "phrases");
        }
    }

    private static string NormalizeAudio(TranscriptionRequest request)
    {
        if (!AudioMediaTypes.Contains(request.MediaType))
            throw new NotSupportedException($"Zoom Scribe does not support media type '{request.MediaType}'.");
        var audio = request.Audio switch
        {
            string value => value,
            JsonElement value when value.ValueKind == JsonValueKind.String => value.GetString(),
            _ => throw new ArgumentException("Zoom Scribe audio must be base64 or a base64 data URL.")
        };
        ArgumentException.ThrowIfNullOrWhiteSpace(audio);
        if (audio.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = audio.IndexOf(',');
            if (comma < 0 || !audio[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Zoom Scribe requires a base64 data URL.");
            var mediaType = audio[5..(comma - ";base64".Length)];
            if (!string.Equals(mediaType, request.MediaType, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Zoom Scribe audio data URL and mediaType must match.");
            audio = audio[(comma + 1)..];
        }
        if (audio.Length > ((MaximumAudioBytes + 2L) / 3 * 4))
            throw new ArgumentException("Zoom Scribe files must not exceed 100 MB.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(audio); }
        catch (FormatException exception) { throw new ArgumentException("Zoom Scribe audio is not valid base64.", nameof(request), exception); }
        if (bytes.Length is 0 or > MaximumAudioBytes)
            throw new ArgumentException("Zoom Scribe requires a nonempty audio file of at most 100 MB (maximum duration two hours).");
        return Convert.ToBase64String(bytes);
    }

    private static double Seconds(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var seconds) || !double.IsFinite(seconds) || seconds < 0 || seconds > float.MaxValue)
            throw new JsonException($"Zoom Scribe requires nonnegative finite '{name}'.");
        return seconds;
    }

    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options.ValidateOpenAITranscriptionRequest();
        if (options.File.Length > MaximumAudioBytes)
            throw new ArgumentException("Zoom Scribe files must not exceed 100 MB.");
        if (options.Prompt is not null || options.Temperature is not null || options.AdditionalProperties?.Count > 0)
            throw new NotSupportedException("Zoom Scribe does not support prompt, temperature or additional OpenAI transcription fields.");
        var format = options.ResolveOpenAITranscriptionResponseFormat();
        if (options.TimestampGranularities?.Any(item => item is not ("word" or "segment")) == true)
            throw new NotSupportedException("Zoom Scribe timestamp granularities support only word and segment.");
        if (options.TimestampGranularities?.Length > 0 && format != "verbose_json")
            throw new ArgumentException("timestamp_granularities requires verbose_json.");
        var request = await options.ToTranscriptionRequest(options.Model, GetIdentifier(), cancellationToken);
        request.ProviderOptions ??= [];
        var config = new JsonObject();
        if (options.Language is not null)
            config["language"] = options.Language;
        config["timestamps"] = format == "verbose_json";
        config["word_time_offsets"] = options.TimestampGranularities?.Contains("word") == true;
        request.ProviderOptions[GetIdentifier()] = JsonSerializer.SerializeToElement(config, Json);
        var result = await TranscriptionRequest(request, cancellationToken);
        var usage = new OpenAITranscriptionDurationUsage { Seconds = result.ProviderMetadata![GetIdentifier()].GetProperty("duration_sec").GetDouble() };
        if (result.ToOpenAITranscriptionResponse(format) is OpenAITranscriptionVerboseResponse verbose)
        {
            verbose.Usage = usage;
            var body = result.ProviderMetadata[GetIdentifier()].GetProperty("result");
            verbose.Words = body.TryGetProperty("segments", out var segments)
                ? segments.EnumerateArray().SelectMany(segment => segment.TryGetProperty("words", out var words)
                    ? words.EnumerateArray().Select(word => new OpenAITranscriptionWord
                    {
                        Word = word.GetProperty("word").GetString()!,
                        Start = word.GetProperty("start").GetDouble(),
                        End = word.GetProperty("end").GetDouble()
                    }) : []).ToArray() : [];
            return verbose;
        }
        return new OpenAITranscriptionResponse { Text = result.Text, Usage = usage };
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var result = await OpenAITranscriptionRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrEmpty(result.Text))
            yield return new OpenAITranscriptionTextDelta { Delta = result.Text };
        cancellationToken.ThrowIfCancellationRequested();
        yield return new OpenAITranscriptionTextDone
        {
            Text = result.Text,
            Usage = result switch
            {
                OpenAITranscriptionResponse response => response.Usage,
                OpenAITranscriptionVerboseResponse response => response.Usage,
                _ => null
            }
        };
    }
}
