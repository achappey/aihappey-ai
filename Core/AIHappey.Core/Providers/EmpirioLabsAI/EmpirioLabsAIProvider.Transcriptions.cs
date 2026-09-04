using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.EmpirioLabsAI;

public partial class EmpirioLabsAIProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.MediaType)) throw new ArgumentException("MediaType is required.", nameof(request));
        var audio = EmpirioAudioString(request.Audio);
        if (string.IsNullOrWhiteSpace(audio)) throw new ArgumentException("Audio is required.", nameof(request));

        using var form = new MultipartFormDataContent();
        AddEmpirioFormValue(form, "model", request.Model);
        AddEmpirioProviderFormValues(form, request.ProviderOptions, "model", "file");
        AddEmpirioFile(form, new MemoryStream(DecodeEmpirioBase64(audio)),
            "audio" + EmpirioAudioExtension(request.MediaType), request.MediaType);
        var submitted = await SendEmpirioMultipartAsync("v1/audio/transcriptions", form, "transcription", cancellationToken);
        var result = await AwaitEmpirioJobAsync(submitted, "transcription", cancellationToken);
        var root = GetEmpirioPayloadRoot(result.Root);
        var text = GetEmpirioString(root, "text") ?? GetEmpirioString(result.Root, "text")
            ?? throw new InvalidOperationException("EmpirioLabs transcription response contained no text.");
        var segments = ReadEmpirioSegments(root);
        return new TranscriptionResponse
        {
            Text = text,
            Language = GetEmpirioString(root, "language"),
            DurationInSeconds = root.TryGetProperty("duration", out var duration) && duration.TryGetSingle(out var seconds) ? seconds : null,
            Segments = segments,
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
        AddEmpirioFormValue(form, "model", options.Model);
        AddEmpirioFormValue(form, "language", options.Language);
        AddEmpirioFormValue(form, "response_format", options.ResponseFormat ?? "json");
        AddEmpirioFormValue(form, "timestamp_granularities", options.TimestampGranularities);
        AddEmpirioAdditionalFormValues(form, options.AdditionalProperties,
            "file", "model", "language", "response_format", "timestamp_granularities", "stream");
        AddEmpirioFile(form, options.File.OpenReadStream(), options.File.FileName, options.File.ContentType);
        var submitted = await SendEmpirioMultipartAsync("v1/audio/transcriptions", form, "transcription", cancellationToken);
        var result = await AwaitEmpirioJobAsync(submitted, "transcription", cancellationToken);
        var root = GetEmpirioPayloadRoot(result.Root);
        var type = (options.ResponseFormat ?? "json").ToLowerInvariant() switch
        {
            "verbose_json" => typeof(OpenAITranscriptionVerboseResponse),
            "diarized_json" => typeof(OpenAITranscriptionDiarizedResponse),
            _ => typeof(OpenAITranscriptionResponse)
        };
        return (IOpenAITranscriptionResponse)(JsonSerializer.Deserialize(root.GetRawText(), type, EmpirioMediaJson)
            ?? throw new InvalidOperationException("EmpirioLabs transcription response was empty."));
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var result = await OpenAITranscriptionRequestAsync(options, cancellationToken);
        if (!string.IsNullOrWhiteSpace(result.Text)) yield return new OpenAITranscriptionTextDelta { Delta = result.Text };
        yield return new OpenAITranscriptionTextDone { Text = result.Text };
    }

    public async IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(
        StreamingTranscriptionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        yield return new TranscriptionStreamStartPart { Warnings = [] };
        var mediaType = EmpirioStreamingAudioMediaType(request.InputAudioFormat.Type);
        var nonStreaming = await TranscriptionRequest(new TranscriptionRequest
        {
            Model = request.Model,
            Audio = request.Audio,
            MediaType = mediaType,
            ProviderOptions = request.ProviderOptions
        }, cancellationToken);
        yield return new TranscriptionResponseMetadataPart
        {
            Timestamp = nonStreaming.Response.Timestamp,
            ModelId = nonStreaming.Response.ModelId,
            Headers = nonStreaming.Response.Headers,
            Body = nonStreaming.Response.Body
        };
        if (request.IncludeRawChunks == true)
            yield return new TranscriptionRawPart { RawValue = nonStreaming.Response.Body };
        if (!string.IsNullOrWhiteSpace(nonStreaming.Text))
            yield return new TranscriptionDeltaPart { Delta = nonStreaming.Text, ProviderMetadata = nonStreaming.ProviderMetadata };
        yield return new TranscriptionFinalPart
        {
            Text = nonStreaming.Text,
            StartSecond = 0,
            EndSecond = nonStreaming.DurationInSeconds,
            ProviderMetadata = nonStreaming.ProviderMetadata
        };
        yield return new TranscriptionFinishPart
        {
            Text = nonStreaming.Text,
            Language = nonStreaming.Language,
            DurationInSeconds = nonStreaming.DurationInSeconds,
            Segments = nonStreaming.Segments ?? [],
            ProviderMetadata = nonStreaming.ProviderMetadata
        };
    }

    private static IEnumerable<TranscriptionSegment> ReadEmpirioSegments(JsonElement root)
    {
        if (!root.TryGetProperty("segments", out var segments) || segments.ValueKind != JsonValueKind.Array) return [];
        return segments.EnumerateArray().Select(segment => new TranscriptionSegment
        {
            Text = GetEmpirioString(segment, "text") ?? string.Empty,
            StartSecond = segment.TryGetProperty("start", out var start) && start.TryGetSingle(out var s) ? s : 0,
            EndSecond = segment.TryGetProperty("end", out var end) && end.TryGetSingle(out var e) ? e : 0
        }).ToList();
    }

    private static string? EmpirioAudioString(object audio)
        => audio is JsonElement { ValueKind: JsonValueKind.String } element ? element.GetString() : audio?.ToString();

    private static byte[] DecodeEmpirioBase64(string value)
        => Convert.FromBase64String(value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? value[(value.IndexOf(',') + 1)..] : value);

    private static string EmpirioAudioExtension(string mediaType) => mediaType.ToLowerInvariant() switch
    {
        "audio/mpeg" or "audio/mp3" => ".mp3", "audio/wav" or "audio/x-wav" => ".wav", "audio/ogg" => ".ogg",
        "audio/webm" => ".webm", "audio/mp4" or "audio/m4a" => ".m4a", "audio/flac" => ".flac", _ => ".bin"
    };

    private static string EmpirioStreamingAudioMediaType(string? type) => type?.ToLowerInvariant() switch
    {
        "mp3" or "mpeg" => "audio/mpeg", "wav" or "wave" or "pcm" => "audio/wav", "ogg" or "opus" => "audio/ogg",
        "webm" => "audio/webm", "m4a" or "mp4" => "audio/mp4", "flac" => "audio/flac", _ => "application/octet-stream"
    };
}
