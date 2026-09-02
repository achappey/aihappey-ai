using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.NavyAI;

public partial class NavyAIProvider
{


    public async IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(
        StreamingTranscriptionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var mediaType = NavyResolveAudioMediaType(request.InputAudioFormat?.Type);
        var response = await TranscriptionRequest(new TranscriptionRequest
        {
            Model = request.Model,
            Audio = request.Audio,
            MediaType = mediaType,
            ProviderOptions = request.ProviderOptions
        }, cancellationToken);
        yield return new TranscriptionStreamStartPart();
        yield return new TranscriptionResponseMetadataPart
        {
            Timestamp = response.Response.Timestamp, ModelId = response.Response.ModelId,
            Headers = response.Response.Headers, Body = response.Response.Body
        };
        if (request.IncludeRawChunks == true)
            yield return new TranscriptionRawPart { RawValue = response.Response.Body };
        if (!string.IsNullOrWhiteSpace(response.Text))
            yield return new TranscriptionDeltaPart { Delta = response.Text, ProviderMetadata = response.ProviderMetadata };
        yield return new TranscriptionFinishPart
        {
            Text = response.Text, Language = response.Language, DurationInSeconds = response.DurationInSeconds,
            Segments = response.Segments, ProviderMetadata = response.ProviderMetadata
        };
    }
    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MediaType);
        var bytes = NavyDecodeBase64(request.Audio);
        var result = await SendNavyTranscriptionAsync(bytes, request.MediaType,
            NavyAudioFileName(request.MediaType), request.Model, request.ProviderOptions, cancellationToken);
        var root = result.Root;
        var segments = NavyReadSegments(root);
        return new TranscriptionResponse
        {
            Text = NavyGetString(root, "text") ?? string.Join(" ", segments.Select(x => x.Text)),
            Language = NavyGetString(root, "language"),
            DurationInSeconds = NavyGetFloat(root, "duration"),
            Segments = segments,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Request = new TranscriptionRequestItem { Body = "multipart/form-data" },
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow, Headers = result.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier()), Body = root
            }
        };
    }


    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAITranscriptionRequest();
        await using var stream = options.File.OpenReadStream();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);
        var fields = NavyCopyOptions(options.AdditionalProperties);
        if (!string.IsNullOrWhiteSpace(options.Language)) fields["language"] = options.Language;
        if (!string.IsNullOrWhiteSpace(options.Prompt)) fields["prompt"] = options.Prompt;
        if (!string.IsNullOrWhiteSpace(options.ResponseFormat)) fields["response_format"] = options.ResponseFormat;
        if (options.Temperature is not null) fields["temperature"] = options.Temperature.Value;
        if (options.TimestampGranularities is not null) fields["timestamp_granularities"] = options.TimestampGranularities;
        var result = await SendNavyTranscriptionAsync(memory.ToArray(), options.File.ContentType,
            options.File.FileName, options.Model, fields, cancellationToken);
        var format = options.ResolveOpenAITranscriptionResponseFormat();
        if (string.Equals(format, "verbose_json", StringComparison.OrdinalIgnoreCase))
            return JsonSerializer.Deserialize<OpenAITranscriptionVerboseResponse>(result.Root.GetRawText())
                ?? throw new InvalidOperationException("NavyAI returned an invalid verbose transcription response.");
        return JsonSerializer.Deserialize<OpenAITranscriptionResponse>(result.Root.GetRawText())
            ?? throw new InvalidOperationException("NavyAI returned an invalid transcription response.");
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);
        if (!string.IsNullOrWhiteSpace(response.Text))
            yield return new OpenAITranscriptionTextDelta { Delta = response.Text };
        yield return new OpenAITranscriptionTextDone { Text = response.Text };
    }

    private async Task<NavyTranscriptionResult> SendNavyTranscriptionAsync(byte[] audio, string? mediaType,
        string? fileName, string model, Dictionary<string, JsonElement>? fields, CancellationToken cancellationToken)
        => await SendNavyTranscriptionAsync(audio, mediaType, fileName, model, NavyCopyOptions(fields), cancellationToken);

    private async Task<NavyTranscriptionResult> SendNavyTranscriptionAsync(byte[] audio, string? mediaType,
        string? fileName, string model, Dictionary<string, object?> fields, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audio);
        if (!string.IsNullOrWhiteSpace(mediaType)) file.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
        form.Add(file, "file", string.IsNullOrWhiteSpace(fileName) ? "audio.bin" : fileName);
        form.Add(new StringContent(model), "model");
        fields.Remove("file"); fields.Remove("model"); fields.Remove("stream");
        foreach (var (name, value) in fields)
        {
            if (value is null) continue;
            if (value is string[] values)
                foreach (var item in values) form.Add(new StringContent(item), "timestamp_granularities[]");
            else if (value is JsonElement element && element.ValueKind == JsonValueKind.Array)
                foreach (var item in element.EnumerateArray()) form.Add(new StringContent(NavyJsonText(item)), name.EndsWith("[]") ? name : name + "[]");
            else form.Add(new StringContent(value is JsonElement json ? NavyJsonText(json) : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!), name);
        }
        using var response = await _client.PostAsync("v1/audio/transcriptions", form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"NavyAI transcription request failed ({(int)response.StatusCode}): {raw}");
        var format = fields.TryGetValue("response_format", out var requested) ? requested?.ToString() : null;
        JsonElement root;
        if (string.Equals(format, "text", StringComparison.OrdinalIgnoreCase)
            || string.Equals(format, "srt", StringComparison.OrdinalIgnoreCase)
            || string.Equals(format, "vtt", StringComparison.OrdinalIgnoreCase))
            root = JsonSerializer.SerializeToElement(new { text = raw });
        else
        {
            using var document = JsonDocument.Parse(raw);
            root = document.RootElement.Clone();
        }
        return new NavyTranscriptionResult(root, response.GetHeaders());
    }

    private static List<TranscriptionSegment> NavyReadSegments(JsonElement root)
        => root.TryGetProperty("segments", out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(x => new TranscriptionSegment
            {
                Text = NavyGetString(x, "text") ?? string.Empty,
                StartSecond = NavyGetFloat(x, "start") ?? 0,
                EndSecond = NavyGetFloat(x, "end") ?? 0
            }).ToList() : [];

    private static string NavyResolveAudioMediaType(string? type) => type?.Trim().ToLowerInvariant() switch
    {
        "mp3" or "mpeg" => "audio/mpeg", "mp4" => "audio/mp4", "m4a" => "audio/m4a",
        "wav" => "audio/wav", "webm" => "audio/webm", "flac" => "audio/flac", _ => "application/octet-stream"
    };

    private static string NavyAudioFileName(string mediaType) => "audio" + mediaType.ToLowerInvariant() switch
    {
        "audio/mpeg" => ".mp3", "audio/mp4" => ".mp4", "audio/m4a" => ".m4a", "audio/wav" => ".wav",
        "audio/webm" => ".webm", "audio/flac" => ".flac", _ => ".bin"
    };

    private sealed record NavyTranscriptionResult(JsonElement Root, Dictionary<string, string> Headers);

}
