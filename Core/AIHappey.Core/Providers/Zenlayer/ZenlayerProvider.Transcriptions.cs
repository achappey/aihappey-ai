using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Zenlayer;

public partial class ZenlayerProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.MediaType)) throw new ArgumentException("MediaType is required.", nameof(request));
        var audio = AudioString(request.Audio);
        if (string.IsNullOrWhiteSpace(audio)) throw new ArgumentException("Audio is required.", nameof(request));
        using var form = new MultipartFormDataContent();
        AddFormValue(form, "model", request.Model);
        AddProviderFormValues(form, request.ProviderOptions, GetIdentifier(), "model", "file");
        AddFile(form, "file", new MemoryStream(DecodeAudio(audio)), "audio" + AudioExtension(request.MediaType), request.MediaType);
        var result = await SendMultipartJsonAsync("v1/audio/transcriptions", form, "transcription", cancellationToken);
        var text = GetString(result.Root, "text") ?? throw new InvalidOperationException("Zenlayer transcription returned no text.");
        return new TranscriptionResponse
        {
            Text = text,
            Language = GetString(result.Root, "language"),
            DurationInSeconds = result.Root.TryGetProperty("duration", out var duration) && duration.TryGetSingle(out var seconds) ? seconds : null,
            Segments = ReadSegments(result.Root),
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root),
            Request = new TranscriptionRequestItem { Body = "multipart/form-data" },
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = result.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = result.Root
            }
        };
    }

    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.File is null) throw new ArgumentException("File is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Model)) throw new ArgumentException("Model is required.", nameof(options));
        using var form = new MultipartFormDataContent();
        AddFormValue(form, "model", options.Model);
        AddFormValue(form, "language", options.Language);
        AddFormValue(form, "prompt", options.Prompt);
        AddFormValue(form, "response_format", options.ResponseFormat ?? "json");
        AddFormValue(form, "temperature", options.Temperature);
        AddFormValue(form, "stream", false);
        AddFormValue(form, "timestamp_granularities", options.TimestampGranularities);
        AddFormValue(form, "include", options.Include);
        AddFormValue(form, "known_speaker_names", options.KnownSpeakerNames);
        AddFormValue(form, "known_speaker_references", options.KnownSpeakerReferences);
        if (options.ChunkingStrategy is not null) AddFormValue(form, "chunking_strategy", JsonSerializer.Serialize(options.ChunkingStrategy, MediaJson));
        AddAdditionalFormValues(form, options.AdditionalProperties,
            "file", "model", "language", "prompt", "response_format", "temperature", "stream", "timestamp_granularities",
            "include", "chunking_strategy", "known_speaker_names", "known_speaker_references");
        AddFile(form, "file", options.File.OpenReadStream(), options.File.FileName, options.File.ContentType);
        var result = await SendMultipartJsonAsync("v1/audio/transcriptions", form, "transcription", cancellationToken);
        var format = options.ResponseFormat?.ToLowerInvariant();
        var type = format switch
        {
            "verbose_json" => typeof(OpenAITranscriptionVerboseResponse),
            "diarized_json" => typeof(OpenAITranscriptionDiarizedResponse),
            _ => typeof(OpenAITranscriptionResponse)
        };
        return (IOpenAITranscriptionResponse)(JsonSerializer.Deserialize(result.Root.GetRawText(), type, MediaJson)
            ?? throw new InvalidOperationException("Zenlayer transcription returned an empty response."));
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);
        if (!string.IsNullOrWhiteSpace(response.Text)) yield return new OpenAITranscriptionTextDelta { Delta = response.Text };
        yield return new OpenAITranscriptionTextDone { Text = response.Text };
    }

    public IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(
        StreamingTranscriptionRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    private static IEnumerable<TranscriptionSegment> ReadSegments(JsonElement root)
    {
        if (!root.TryGetProperty("segments", out var segments) || segments.ValueKind != JsonValueKind.Array) return [];
        return segments.EnumerateArray().Select(segment => new TranscriptionSegment
        {
            Text = GetString(segment, "text") ?? string.Empty,
            StartSecond = segment.TryGetProperty("start", out var start) && start.TryGetSingle(out var s) ? s : 0,
            EndSecond = segment.TryGetProperty("end", out var end) && end.TryGetSingle(out var e) ? e : 0
        }).ToList();
    }

    private static string? AudioString(object audio) => audio is JsonElement { ValueKind: JsonValueKind.String } element ? element.GetString() : audio?.ToString();
    private static byte[] DecodeAudio(string audio) => Convert.FromBase64String(audio.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? audio[(audio.IndexOf(',') + 1)..] : audio);
    private static string AudioExtension(string mediaType) => mediaType.ToLowerInvariant() switch
    {
        "audio/mpeg" or "audio/mp3" => ".mp3", "audio/wav" or "audio/x-wav" => ".wav", "audio/ogg" => ".ogg",
        "audio/webm" => ".webm", "audio/mp4" or "audio/m4a" => ".m4a", "audio/flac" => ".flac", _ => ".bin"
    };
}
