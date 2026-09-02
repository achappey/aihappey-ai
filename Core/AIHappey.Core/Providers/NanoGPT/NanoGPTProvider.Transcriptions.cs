using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace AIHappey.Core.Providers.NanoGPT;

public partial class NanoGPTProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MediaType);
        var fields = CopyNanoGPTOptions(request.ProviderOptions);
        fields["response_format"] = "verbose_json";
        var result = await SendNanoGPTTranscriptionAsync(DecodeNanoGPTBase64(request.Audio), request.MediaType,
            NanoGPTAudioFileName(request.MediaType), request.Model, fields, cancellationToken);
        var segments = ReadNanoGPTSegments(result.Root);
        return new TranscriptionResponse
        {
            Text = NanoGPTGetString(result.Root, "text") ?? string.Join(" ", segments.Select(x => x.Text)),
            Language = NanoGPTGetString(result.Root, "language"), DurationInSeconds = NanoGPTGetFloat(result.Root, "duration"),
            Segments = segments, ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root),
            Request = new TranscriptionRequestItem { Body = "multipart/form-data" },
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow, Headers = result.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier()), Body = result.Root
            }
        };
    }

    public async IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(
        StreamingTranscriptionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = await TranscriptionRequest(new TranscriptionRequest
        {
            Model = request.Model, Audio = request.Audio,
            MediaType = ResolveNanoGPTAudioMediaType(request.InputAudioFormat?.Type), ProviderOptions = request.ProviderOptions
        }, cancellationToken);
        yield return new TranscriptionStreamStartPart();
        yield return new TranscriptionResponseMetadataPart
        {
            Timestamp = response.Response.Timestamp, ModelId = response.Response.ModelId,
            Headers = response.Response.Headers, Body = response.Response.Body
        };
        if (request.IncludeRawChunks == true) yield return new TranscriptionRawPart { RawValue = response.Response.Body };
        if (!string.IsNullOrWhiteSpace(response.Text))
            yield return new TranscriptionDeltaPart { Delta = response.Text, ProviderMetadata = response.ProviderMetadata };
        yield return new TranscriptionFinishPart
        {
            Text = response.Text, Language = response.Language, DurationInSeconds = response.DurationInSeconds,
            Segments = response.Segments, ProviderMetadata = response.ProviderMetadata
        };
    }

    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAITranscriptionRequest();
        await using var stream = options.File.OpenReadStream();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);
        var fields = CopyNanoGPTOptions(options.AdditionalProperties);
        if (!string.IsNullOrWhiteSpace(options.Language)) fields["language"] = options.Language;
        if (!string.IsNullOrWhiteSpace(options.Prompt)) fields["prompt"] = options.Prompt;
        if (!string.IsNullOrWhiteSpace(options.ResponseFormat)) fields["response_format"] = options.ResponseFormat;
        if (options.Temperature is not null) fields["temperature"] = options.Temperature.Value;
        if (options.TimestampGranularities is not null) fields["timestamp_granularities"] = options.TimestampGranularities;
        var result = await SendNanoGPTTranscriptionAsync(memory.ToArray(), options.File.ContentType,
            options.File.FileName, options.Model, fields, cancellationToken);
        var format = options.ResolveOpenAITranscriptionResponseFormat();
        if (string.Equals(format, "verbose_json", StringComparison.OrdinalIgnoreCase))
            return JsonSerializer.Deserialize<OpenAITranscriptionVerboseResponse>(result.Root.GetRawText())
                ?? throw new InvalidOperationException("NanoGPT returned an invalid verbose transcription response.");
        return JsonSerializer.Deserialize<OpenAITranscriptionResponse>(result.Root.GetRawText())
            ?? throw new InvalidOperationException("NanoGPT returned an invalid transcription response.");
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(response.Text)) yield return new OpenAITranscriptionTextDelta { Delta = response.Text };
        yield return new OpenAITranscriptionTextDone { Text = response.Text };
    }

    private async Task<NanoGPTTranscriptionResult> SendNanoGPTTranscriptionAsync(byte[] audio, string? mediaType,
        string? fileName, string model, Dictionary<string, object?> fields, CancellationToken cancellationToken)
    {
        ApplyAuthHeader(); using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audio);
        if (!string.IsNullOrWhiteSpace(mediaType)) file.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
        form.Add(file, "file", string.IsNullOrWhiteSpace(fileName) ? "audio.bin" : fileName);
        form.Add(new StringContent(model), "model");
        fields.Remove("file"); fields.Remove("model"); fields.Remove("stream");
        foreach (var (name, value) in fields)
        {
            if (value is null) continue;
            if (value is string[] strings)
                foreach (var item in strings) form.Add(new StringContent(item), name.EndsWith("[]") ? name : name + "[]");
            else if (value is JsonElement element && element.ValueKind == JsonValueKind.Array)
                foreach (var item in element.EnumerateArray()) form.Add(new StringContent(NanoGPTJsonText(item)), name.EndsWith("[]") ? name : name + "[]");
            else form.Add(new StringContent(value is JsonElement json ? NanoGPTJsonText(json)
                : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!), name);
        }
        using var response = await _client.PostAsync("v1/audio/transcriptions", form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureNanoGPTSuccess(response, raw, "transcription request");
        var requestedFormat = fields.TryGetValue("response_format", out var format) ? format?.ToString() : null;
        JsonElement root;
        if (requestedFormat?.ToLowerInvariant() is "text" or "srt" or "vtt") root = JsonSerializer.SerializeToElement(new { text = raw });
        else { using var document = JsonDocument.Parse(raw); root = document.RootElement.Clone(); }
        return new NanoGPTTranscriptionResult(root, response.GetHeaders());
    }

    private static List<TranscriptionSegment> ReadNanoGPTSegments(JsonElement root)
        => root.TryGetProperty("segments", out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(x => new TranscriptionSegment
            {
                Text = NanoGPTGetString(x, "text") ?? string.Empty,
                StartSecond = NanoGPTGetFloat(x, "start") ?? 0, EndSecond = NanoGPTGetFloat(x, "end") ?? 0
            }).ToList() : [];

    private static string ResolveNanoGPTAudioMediaType(string? type) => type?.Trim().ToLowerInvariant() switch
    {
        "mp3" or "mpeg" or "mpga" => "audio/mpeg", "mp4" => "audio/mp4", "m4a" => "audio/m4a",
        "wav" => "audio/wav", "webm" => "audio/webm", "ogg" => "audio/ogg", "aac" => "audio/aac", _ => "application/octet-stream"
    };
    private static string NanoGPTAudioFileName(string mediaType) => "audio" + mediaType.ToLowerInvariant() switch
    {
        "audio/mpeg" => ".mp3", "audio/mp4" => ".mp4", "audio/m4a" => ".m4a", "audio/wav" => ".wav",
        "audio/webm" => ".webm", "audio/ogg" => ".ogg", "audio/aac" => ".aac", _ => ".bin"
    };
    private sealed record NanoGPTTranscriptionResult(JsonElement Root, Dictionary<string, string> Headers);
}
