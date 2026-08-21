using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.FastRouter;

public partial class FastRouterProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (audio, mediaType) = DecodeFastRouterData(request.Audio, request.MediaType);
        var result = await TranscribeFastRouterAsync(request.Model, audio, mediaType, request.ProviderOptions, null, cancellationToken);
        return ToFastRouterTranscriptionResponse(result, request.Model);
    }

    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAITranscriptionRequest();
        await using var input = options.File.OpenReadStream();
        using var memory = new MemoryStream();
        await input.CopyToAsync(memory, cancellationToken);

        var known = new Dictionary<string, object?>
        {
            ["language"] = options.Language,
            ["prompt"] = options.Prompt,
            ["response_format"] = options.ResponseFormat,
            ["temperature"] = options.Temperature,
            ["timestamp_granularities[]"] = options.TimestampGranularities,
            ["include[]"] = options.Include,
            ["chunking_strategy"] = options.ChunkingStrategy is null ? null : JsonSerializer.SerializeToElement(options.ChunkingStrategy, FastRouterJsonOptions),
            ["known_speaker_names[]"] = options.KnownSpeakerNames,
            ["known_speaker_references[]"] = options.KnownSpeakerReferences
        };
        var result = await TranscribeFastRouterAsync(options.Model, memory.ToArray(),
            options.File.ContentType ?? "audio/mpeg", options.AdditionalProperties, known, cancellationToken);
        return ToFastRouterOpenAITranscriptionResponse(result.Root, options.ResponseFormat);
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(response.Text))
            yield return new OpenAITranscriptionTextDelta { Delta = response.Text };
        yield return new OpenAITranscriptionTextDone { Text = response.Text };
    }

    private async Task<FastRouterJsonResult> TranscribeFastRouterAsync(
        string model,
        byte[] audio,
        string mediaType,
        Dictionary<string, JsonElement>? rawProperties,
        Dictionary<string, object?>? knownProperties,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("Model is required.", nameof(model));
        ApplyAuthHeader();
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audio);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
        form.Add(file, "file", ResolveFastRouterAudioFileName(mediaType));
        AddFastRouterFormValue(form, "model", model);

        foreach (var property in knownProperties ?? [])
            AddFastRouterFormValue(form, property.Key, property.Value);
        AddFastRouterAdditionalFormValues(form, rawProperties,
            "file", "model", "language", "prompt", "response_format", "temperature", "timestamp_granularities",
            "timestamp_granularities[]", "include", "include[]", "chunking_strategy", "known_speaker_names",
            "known_speaker_names[]", "known_speaker_references", "known_speaker_references[]", "stream");

        using var response = await _client.PostAsync("v1/audio/transcriptions", form, cancellationToken);
        return await ReadFastRouterJsonAsync(response, "transcription", cancellationToken);
    }

    private TranscriptionResponse ToFastRouterTranscriptionResponse(FastRouterJsonResult result, string requestedModel)
    {
        var root = result.Root;
        var segments = root.TryGetProperty("segments", out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Select(segment => new TranscriptionSegment
            {
                Text = GetFastRouterString(segment, "text") ?? string.Empty,
                StartSecond = segment.TryGetProperty("start", out var start) && start.TryGetSingle(out var startValue) ? startValue : 0,
                EndSecond = segment.TryGetProperty("end", out var end) && end.TryGetSingle(out var endValue) ? endValue : 0
            }).ToList()
            : [];

        return new TranscriptionResponse
        {
            Text = GetFastRouterString(root, "text") ?? string.Empty,
            Language = GetFastRouterString(root, "language"),
            DurationInSeconds = root.TryGetProperty("duration", out var duration) && duration.TryGetSingle(out var value) ? value : null,
            Segments = segments,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = result.Headers,
                ModelId = (GetFastRouterString(root, "model") ?? requestedModel).ToModelId(GetIdentifier()),
                Body = root
            },
            Request = new TranscriptionRequestItem { Body = "multipart/form-data" }
        };
    }

    private static IOpenAITranscriptionResponse ToFastRouterOpenAITranscriptionResponse(JsonElement root, string? responseFormat)
    {
        var text = GetFastRouterString(root, "text") ?? string.Empty;
        if (string.Equals(responseFormat, "verbose_json", StringComparison.OrdinalIgnoreCase))
        {
            var response = JsonSerializer.Deserialize<OpenAITranscriptionVerboseResponse>(root.GetRawText(), FastRouterJsonOptions);
            if (response is not null) return response;
            return new OpenAITranscriptionVerboseResponse
            {
                Text = text,
                Language = GetFastRouterString(root, "language") ?? string.Empty,
                Duration = root.TryGetProperty("duration", out var duration) && duration.TryGetDouble(out var value) ? value : 0
            };
        }

        if (string.Equals(responseFormat, "diarized_json", StringComparison.OrdinalIgnoreCase))
        {
            var response = JsonSerializer.Deserialize<OpenAITranscriptionDiarizedResponse>(root.GetRawText(), FastRouterJsonOptions);
            if (response is not null) return response;
        }

        return JsonSerializer.Deserialize<OpenAITranscriptionResponse>(root.GetRawText(), FastRouterJsonOptions)
            ?? new OpenAITranscriptionResponse { Text = text };
    }

    private static string ResolveFastRouterAudioFileName(string mediaType)
        => mediaType.ToLowerInvariant() switch
        {
            var value when value.Contains("wav") => "audio.wav",
            var value when value.Contains("webm") => "audio.webm",
            var value when value.Contains("mp4") || value.Contains("m4a") => "audio.m4a",
            var value when value.Contains("mpeg") || value.Contains("mp3") => "audio.mp3",
            _ => "audio.bin"
        };
}
