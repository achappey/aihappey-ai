using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.EdgeSpeak;

public sealed partial class EdgeSpeakProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        var options = NativeOptions(request.ProviderOptions);
        var format = options.TryGetValue("response_format", out var value) ? value.GetString()! : "verbose_json";
        using var form = TranscriptionForm(request, options, streaming: false, format);
        using var message = CreateMessage(HttpMethod.Post, "v1/audio/transcriptions");
        message.Content = form;
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccess(response, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(format == "text" ? JsonSerializer.Serialize(new { text = body }) : body);
        var root = document.RootElement;
        return new()
        {
            Text = RequiredString(root, "text"), Language = String(root, "language"),
            DurationInSeconds = (float?)Number(root, "duration"),
            Segments = TranscriptionSegments(root),
            ProviderMetadata = Metadata(root),
            Response = new()
            {
                Timestamp = DateTime.UtcNow, ModelId = $"{ProviderId}/{NativeModel(request.Model)}",
                Headers = response.GetHeaders(), Body = root.Clone()
            }
        };
    }

    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        var request = await ToTranscription(options, cancellationToken);
        var response = await TranscriptionRequest(request, cancellationToken);
        var native = response.ProviderMetadata![ProviderId];
        var format = NativeOptions(request.ProviderOptions)["response_format"].GetString();
        var usage = native.TryGetProperty("usage", out var usageValue) && Number(usageValue, "seconds") is { } seconds
            ? new OpenAITranscriptionDurationUsage { Seconds = seconds } : null;
        if (format == "diarized_json")
            return new OpenAITranscriptionDiarizedResponse
            {
                Text = response.Text, Duration = RequiredNumber(native, "duration"), Usage = usage,
                Segments = Elements(native, "segments").Select((segment, i) => new OpenAITranscriptionTextSegment
                {
                    Id = segment.TryGetProperty("id", out var id) ? id.ToString() : i.ToString(CultureInfo.InvariantCulture),
                    Text = RequiredString(segment, "text"),
                    Start = RequiredNumber(segment, "start"), End = RequiredNumber(segment, "end"),
                    Speaker = String(segment, "speaker") ?? "unknown"
                }).ToArray()
            };
        if (format == "verbose_json")
            return new OpenAITranscriptionVerboseResponse
            {
                Text = response.Text, Language = RequiredString(native, "language"), Duration = RequiredNumber(native, "duration"),
                Usage = usage,
                Segments = Elements(native, "segments").Select((segment, i) => new OpenAITranscriptionSegment
                {
                    Id = i, Seek = 0, Text = RequiredString(segment, "text"),
                    Start = RequiredNumber(segment, "start"), End = RequiredNumber(segment, "end"),
                    Tokens = [], Temperature = 0, AverageLogprob = (float)(Number(segment, "avg_logprob") ?? 0),
                    CompressionRatio = 0, NoSpeechProbability = 0
                }).ToArray(),
                Words = (native.TryGetProperty("words", out var words) && words.ValueKind == JsonValueKind.Array
                    ? words.EnumerateArray().ToArray()
                    : Elements(native, "segments").SelectMany(s => Elements(s, "words")))
                    .Select(word => new OpenAITranscriptionWord
                    {
                        Word = RequiredString(word, "word"), Start = RequiredNumber(word, "start"),
                        End = RequiredNumber(word, "end")
                    }).ToArray()
            };
        return new OpenAITranscriptionResponse { Text = response.Text, Usage = usage };
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = await ToTranscription(options, cancellationToken);
        await foreach (var item in StreamTranscription(request, cancellationToken))
        {
            if (item.Type == "transcript.text.delta")
                yield return new OpenAITranscriptionTextDelta { Delta = RequiredString(item.Data, "delta") };
            else if (item.Type == "transcript.text.done")
                yield return new OpenAITranscriptionTextDone
                {
                    Text = RequiredString(item.Data, "text"),
                    Usage = item.Data.TryGetProperty("usage", out var usage) ? usage.Clone() : null
                };
        }
    }

    public async IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(
        StreamingTranscriptionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.InputAudioFormat);
        if (request.InputAudioFormat.Rate is <= 0)
            throw new ArgumentException("EdgeSpeak input audio rate must be positive.");
        var transcription = new TranscriptionRequest
        {
            Model = request.Model, Audio = request.Audio, MediaType = request.InputAudioFormat.Type,
            ProviderOptions = request.ProviderOptions
        };
        yield return new TranscriptionStreamStartPart();
        await foreach (var item in StreamTranscription(transcription, cancellationToken))
        {
            if (item.Type == "response-metadata")
            {
                yield return new TranscriptionResponseMetadataPart
                {
                    Timestamp = DateTime.UtcNow, ModelId = $"{ProviderId}/{NativeModel(request.Model)}",
                    Headers = item.Headers
                };
                continue;
            }
            if (request.IncludeRawChunks == true)
                yield return new TranscriptionRawPart { RawValue = new { @event = item.Type, data = item.Data } };
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Type == "transcript.text.delta")
                yield return new TranscriptionDeltaPart { Delta = RequiredString(item.Data, "delta") };
            else if (item.Type == "transcript.text.done")
                yield return new TranscriptionFinishPart
                {
                    Text = RequiredString(item.Data, "text"), ProviderMetadata = Metadata(item.Data),
                    Language = String(item.Data, "language"), DurationInSeconds = Number(item.Data, "duration"),
                    Segments = TranscriptionSegments(item.Data)
                };
        }
    }

    private async IAsyncEnumerable<NativeEvent> StreamTranscription(
        TranscriptionRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var options = NativeOptions(request.ProviderOptions);
        var format = options.TryGetValue("response_format", out var value) ? value.GetString()! : "json";
        using var form = TranscriptionForm(request, options, streaming: true, format);
        using var message = CreateMessage(HttpMethod.Post, "v1/audio/transcriptions", streaming: true);
        message.Content = form;
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccess(response, ct);
        yield return new("response-metadata", default, response.GetHeaders());
        await foreach (var item in ReadEvents(response, "transcript.text.done", ct))
            yield return item;
    }

    private static async Task<TranscriptionRequest> ToTranscription(OpenAITranscriptionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ChunkingStrategy is not null || request.Include?.Length > 0 ||
            request.KnownSpeakerNames?.Length > 0 || request.KnownSpeakerReferences?.Length > 0)
            throw new NotSupportedException("EdgeSpeak does not document OpenAI chunking, include, or known-speaker reference fields.");
        var result = await request.ToTranscriptionRequest(NativeModel(request.Model), ProviderId, ct);
        var options = ExtensionOptions(request.AdditionalProperties);
        foreach (var option in NativeOptions(result.ProviderOptions))
            options[option.Key] = option.Value;
        if (request.ResponseFormat is not null || !options.ContainsKey("response_format"))
            options["response_format"] = JsonSerializer.SerializeToElement(request.ResponseFormat ?? "json");
        result.ProviderOptions = new() { [ProviderId] = JsonSerializer.SerializeToElement(options) };
        return result;
    }

    private static MultipartFormDataContent TranscriptionForm(TranscriptionRequest request,
        Dictionary<string, JsonElement> options, bool streaming, string format)
    {
        var model = NativeModel(request.Model);
        if (format is not ("json" or "verbose_json" or "diarized_json" or "text"))
            throw new NotSupportedException($"EdgeSpeak transcription response_format '{format}' is unsupported.");
        ValidateTranscriptionOptions(options, streaming, format);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MediaType);
        var mediaType = request.MediaType.Contains('/') ? request.MediaType : "audio/" + request.MediaType;
        mediaType = mediaType switch { "audio/mp3" => "audio/mpeg", "audio/wave" => "audio/wav", _ => mediaType };
        var encoded = request.Audio?.ToString() ?? throw new ArgumentException("EdgeSpeak requires audio.");
        if (encoded.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = encoded.IndexOf(',');
            if (comma < 0 || !encoded[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("EdgeSpeak audio data URLs must contain base64 audio.");
            encoded = encoded[(comma + 1)..];
        }
        var audio = Convert.FromBase64String(encoded);
        if (audio.Length == 0)
            throw new ArgumentException("EdgeSpeak audio must not be empty.");
        var contentType = MediaTypeHeaderValue.Parse(mediaType);
        var extension = contentType.MediaType switch
        {
            "audio/wav" or "audio/x-wav" => ".wav", "audio/mpeg" => ".mp3",
            "audio/mp4" or "video/mp4" => ".mp4", "audio/flac" => ".flac", "audio/ogg" => ".ogg",
            "audio/opus" => ".opus", "audio/webm" or "video/webm" => ".webm", "audio/aac" => ".aac", _ => ".bin"
        };
        var form = new MultipartFormDataContent();
        try
        {
            var file = new ByteArrayContent(audio);
            file.Headers.ContentType = contentType;
            form.Add(file, "file", "audio" + extension);
            form.Add(new StringContent(model), "model");
            form.Add(new StringContent(format), "response_format");
            form.Add(new StringContent(streaming ? "true" : "false"), "stream");
            foreach (var (name, value) in options)
            {
                if (name is "model" or "file" or "stream" or "response_format" || value.ValueKind == JsonValueKind.Null)
                    continue;
                if (name is "timestamp_granularities" or "timestamp_granularities[]")
                {
                    if (value.ValueKind != JsonValueKind.Array)
                        throw new ArgumentException("EdgeSpeak timestamp_granularities must be an array.");
                    foreach (var entry in value.EnumerateArray())
                        form.Add(new StringContent(entry.GetString()!), "timestamp_granularities[]");
                }
                else
                    form.Add(new StringContent(value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText(), Encoding.UTF8), name);
            }
            return form;
        }
        catch
        {
            form.Dispose();
            throw;
        }
    }

    private static void ValidateTranscriptionOptions(Dictionary<string, JsonElement> options, bool streaming, string format)
    {
        var granularities = new List<string>();
        foreach (var name in new[] { "timestamp_granularities", "timestamp_granularities[]" })
        {
            if (!options.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null)
                continue;
            if (value.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("EdgeSpeak timestamp_granularities must be an array.");
            foreach (var entry in value.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String || entry.GetString() is not ("word" or "segment"))
                    throw new ArgumentException("EdgeSpeak timestamp granularities must be word or segment.");
                granularities.Add(entry.GetString()!);
            }
        }
        if (granularities.Count > 8)
            throw new ArgumentException("EdgeSpeak accepts at most eight timestamp granularities.");
        var diarization = options.TryGetValue("diarization_model", out var diarizationModel) && diarizationModel.ValueKind != JsonValueKind.Null;
        if (streaming && (format != "json" || diarization || granularities.Count > 0))
            throw new NotSupportedException("EdgeSpeak streaming transcription requires json without diarization or timestamp granularities.");
        if ((diarization && format != "diarized_json") ||
            (granularities.Count > 0 && format is not ("verbose_json" or "diarized_json")))
            throw new ArgumentException("EdgeSpeak diarization and timestamp options require a detailed transcription response format.");
        if (options.TryGetValue("word_timestamp_enabled", out var wordTiming) && wordTiming.ValueKind != JsonValueKind.Null)
        {
            if (wordTiming.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ArgumentException("EdgeSpeak word_timestamp_enabled must be boolean.");
            if (wordTiming.GetBoolean() && format is not ("verbose_json" or "diarized_json"))
                throw new NotSupportedException("EdgeSpeak word timestamps require verbose_json or diarized_json.");
            if (!wordTiming.GetBoolean() && granularities.Contains("word"))
                throw new ArgumentException("EdgeSpeak word_timestamp_enabled=false conflicts with word granularities.");
        }
        if (options.TryGetValue("candidates", out var candidates) && candidates.ValueKind != JsonValueKind.Null)
        {
            if (candidates.ValueKind != JsonValueKind.Number || !candidates.TryGetInt32(out var count) || count is < 0 or > 8)
                throw new ArgumentException("EdgeSpeak candidates must be an integer from zero to eight.");
            if (count >= 2 && (format != "verbose_json" || granularities.Contains("word") || wordTiming.ValueKind == JsonValueKind.True))
                throw new ArgumentException("EdgeSpeak alternative candidates require verbose_json without word timestamps.");
        }
        if (options.TryGetValue("semantic_sentence_enabled", out var sentences) && sentences.ValueKind == JsonValueKind.False &&
            new[] { "min_chars", "max_chars", "start_margin", "end_margin" }
                .Any(name => options.TryGetValue(name, out var value) && value.ValueKind != JsonValueKind.Null))
            throw new ArgumentException("EdgeSpeak sentence controls conflict with semantic_sentence_enabled=false.");
    }

    private static string RequiredString(JsonElement value, string name)
        => String(value, name) ?? throw new InvalidOperationException($"EdgeSpeak response is missing '{name}'.");

    private static double? Number(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) &&
            field.ValueKind == JsonValueKind.Number ? field.GetDouble() : null;

    private static double RequiredNumber(JsonElement value, string name)
        => Number(value, name) ?? throw new InvalidOperationException($"EdgeSpeak response is missing '{name}'.");

    private static IEnumerable<JsonElement> Elements(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Array
            ? field.EnumerateArray() : [];

    private static TranscriptionSegment[] TranscriptionSegments(JsonElement root)
        => Elements(root, "segments").Select(s => new TranscriptionSegment
        {
            Text = RequiredString(s, "text"), StartSecond = (float)RequiredNumber(s, "start"),
            EndSecond = (float)RequiredNumber(s, "end")
        }).ToArray();
}
