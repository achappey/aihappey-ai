using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Mistral;

public partial class MistralProvider
{
    public async IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(
        StreamingTranscriptionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Audio);
        ArgumentNullException.ThrowIfNull(request.InputAudioFormat);
        if (request.InputAudioFormat.Rate is <= 0)
            throw new ArgumentException("Transcription input sample rate must be positive.", nameof(request));
        var model = NormalizeAudioModel(request.Model);
        var bytes = Convert.FromBase64String(request.Audio);
        if (bytes.Length == 0)
            throw new ArgumentException("Transcription audio must not be empty.", nameof(request));
        var warnings = new List<object>();
        using var form = CreateStreamingTranscriptionForm(bytes, request.InputAudioFormat.Type, model,
            AudioProviderOptions(request.ProviderOptions), warnings);
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/audio/transcriptions") { Content = form };
        using var response = await SendAudioStreamingAsync(message, cancellationToken);

        yield return new TranscriptionStreamStartPart { Warnings = warnings };
        cancellationToken.ThrowIfCancellationRequested();
        yield return new TranscriptionResponseMetadataPart
        {
            Timestamp = DateTime.UtcNow,
            ModelId = model.ToModelId(GetIdentifier()),
            Headers = response.GetHeaders()
        };

        string? language = null;
        var segments = new List<JsonElement>();
        await foreach (var item in ReadAudioEventsAsync(response, "transcription.done", cancellationToken))
        {
            var payload = item.Payload;
            if (request.IncludeRawChunks == true)
                yield return new TranscriptionRawPart { RawValue = payload };
            cancellationToken.ThrowIfCancellationRequested();
            switch (item.Type)
            {
                case "transcription.text.delta":
                    yield return new TranscriptionDeltaPart { Delta = RequiredAudioString(payload, "text") };
                    break;
                case "transcription.language":
                    language = AudioString(payload, "language");
                    yield return new TranscriptionResponseMetadataPart { Body = payload };
                    break;
                case "transcription.segment.delta":
                    segments.Add(payload);
                    yield return new TranscriptionFinalPart
                    {
                        Text = RequiredAudioString(payload, "text"),
                        StartSecond = AudioNumber(payload, "start"),
                        EndSecond = AudioNumber(payload, "end"),
                        ProviderMetadata = AudioMetadata(payload)
                    };
                    break;
                case "transcription.done":
                    yield return new TranscriptionResponseMetadataPart
                    {
                        ModelId = (AudioString(payload, "model") ?? model).ToModelId(GetIdentifier()),
                        Body = payload
                    };
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return CreateTranscriptionStreamFinish(payload, segments, language);
                    break;
            }
        }
    }

    private static MultipartFormDataContent CreateStreamingTranscriptionForm(
        byte[] audio,
        string inputFormat,
        string model,
        Dictionary<string, JsonElement> options,
        ICollection<object> warnings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputFormat);
        var mediaType = inputFormat.Trim().ToLowerInvariant() switch
        {
            "mp3" or "audio/mp3" => "audio/mpeg",
            "wave" or "audio/x-wav" => "audio/wav",
            var value when !value.Contains('/') => "audio/" + value,
            var value => value
        };
        var contentType = MediaTypeHeaderValue.Parse(mediaType);
        if (contentType.MediaType is "audio/pcm" or "audio/l16" or "audio/pcm_s16le")
            throw new NotSupportedException("Mistral file transcription requires a self-describing audio file, not raw PCM samples.");
        var extension = contentType.MediaType switch
        {
            "audio/mpeg" => ".mp3",
            "audio/wav" => ".wav",
            "audio/flac" => ".flac",
            "audio/ogg" => ".ogg",
            "audio/opus" => ".opus",
            "audio/webm" or "video/webm" => ".webm",
            "audio/mp4" or "video/mp4" => ".mp4",
            "audio/aac" => ".aac",
            _ => ".bin"
        };

        var form = new MultipartFormDataContent();
        try
        {
            var file = new ByteArrayContent(audio);
            file.Headers.ContentType = contentType;
            form.Add(file, "file", "audio" + extension);
            form.Add(new StringContent(model), "model");
            form.Add(new StringContent("true"), "stream");
            foreach (var (name, value) in options)
            {
                if (name is "file" or "file_url" or "file_id" or "model" or "stream" || value.ValueKind == JsonValueKind.Null)
                    continue; // The contract's complete audio is the sole input.
                if (name is "timestamp_granularities" or "context_bias")
                {
                    if (value.ValueKind != JsonValueKind.Array)
                        throw new ArgumentException($"Mistral transcription option '{name}' must be an array.");
                    foreach (var entry in value.EnumerateArray())
                    {
                        if (entry.ValueKind != JsonValueKind.String)
                            throw new ArgumentException($"Mistral transcription option '{name}' must contain strings.");
                        // Mistral's file API uses repeated multipart fields.
                        form.Add(new StringContent(entry.GetString()!), name);
                    }
                }
                else if (name is "language" or "temperature" or "diarize" or "return_language")
                {
                    if ((name == "language" && value.ValueKind != JsonValueKind.String) ||
                        (name == "temperature" && value.ValueKind != JsonValueKind.Number) ||
                        (name is "diarize" or "return_language" && value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)))
                        throw new ArgumentException($"Mistral transcription option '{name}' has an invalid type.");
                    form.Add(new StringContent(value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText()), name);
                }
                else
                    warnings.Add(new { type = "unsupported", feature = "providerOptions.mistral." + name });
            }
            return form;
        }
        catch
        {
            form.Dispose();
            throw;
        }
    }

    private static TranscriptionFinishPart CreateTranscriptionStreamFinish(
        JsonElement payload,
        List<JsonElement> streamedSegments,
        string? language)
    {
        // A present final list (even an empty one) supersedes streamed segments.
        var segments = payload.TryGetProperty("segments", out var finalSegments) && finalSegments.ValueKind == JsonValueKind.Array
            ? finalSegments.EnumerateArray().ToList()
            : streamedSegments;
        var mapped = new List<TranscriptionSegment>();
        foreach (var segment in segments)
        {
            var start = AudioNumber(segment, "start");
            var end = AudioNumber(segment, "end");
            // This legacy summary type has non-nullable floats. Original values,
            // including nullable timestamps and speaker IDs, remain in metadata.
            if (start.HasValue && end.HasValue && Math.Abs(start.Value) <= float.MaxValue && Math.Abs(end.Value) <= float.MaxValue)
                mapped.Add(new TranscriptionSegment
                {
                    Text = RequiredAudioString(segment, "text"),
                    StartSecond = (float)start.Value,
                    EndSecond = (float)end.Value
                });
        }

        var metadata = payload.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        if (!metadata.ContainsKey("segments"))
            metadata["segments"] = JsonSerializer.SerializeToElement(segments);
        else if (finalSegments.ValueKind == JsonValueKind.Null && streamedSegments.Count > 0)
            metadata["streamed_segments"] = JsonSerializer.SerializeToElement(streamedSegments);
        return new TranscriptionFinishPart
        {
            Text = RequiredAudioString(payload, "text"),
            Segments = mapped,
            Language = payload.TryGetProperty("language", out _) ? AudioString(payload, "language") : language,
            DurationInSeconds = payload.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
                ? AudioNumber(usage, "prompt_audio_seconds") : null,
            ProviderMetadata = new() { ["mistral"] = JsonSerializer.SerializeToElement(metadata) }
        };
    }
}
