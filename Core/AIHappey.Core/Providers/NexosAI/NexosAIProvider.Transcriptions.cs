using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.NexosAI;

public partial class NexosAIProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (bytes, mediaType) = DecodeAudio(request.Audio, request.MediaType);
        var result = await TranscribeAsync(request.Model, bytes, mediaType, GetTranscriptionOptions(request.ProviderOptions), cancellationToken);
        return ToGenericTranscriptionResponse(result, request.Model);
    }

    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAITranscriptionRequest();
        await using var stream = options.File.OpenReadStream();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);
        var values = new NexosTranscriptionOptions(options.Language, options.Prompt, options.ResponseFormat,
            options.Temperature, options.TimestampGranularities);
        var result = await TranscribeAsync(options.Model, memory.ToArray(), options.File.ContentType ?? "audio/mpeg",
            values, cancellationToken, options.File.FileName);
        var text = ReadString(result.Root, "text") ?? result.Raw;
        return string.Equals(options.ResolveOpenAITranscriptionResponseFormat(), "verbose_json", StringComparison.OrdinalIgnoreCase)
            ? new OpenAITranscriptionVerboseResponse
            {
                Text = text, Language = ReadString(result.Root, "language") ?? string.Empty,
                Duration = ReadNullableFloat(result.Root, "duration") ?? 0
            }
            : new OpenAITranscriptionResponse { Text = text };
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(response.Text)) yield return new OpenAITranscriptionTextDelta { Delta = response.Text };
        yield return new OpenAITranscriptionTextDone { Text = response.Text };
    }

    private async Task<NexosTranscriptionResult> TranscribeAsync(string model, byte[] audio, string mediaType,
        NexosTranscriptionOptions values, CancellationToken cancellationToken, string? fileName = null)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("Model is required.", nameof(model));
        if (audio.Length == 0) throw new ArgumentException("Audio is required.", nameof(audio));
        ApplyAuthHeader();
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audio);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
        form.Add(file, "file", string.IsNullOrWhiteSpace(fileName) ? ResolveAudioFileName(mediaType) : fileName);
        form.Add(new StringContent(model), "model");
        AddFormValue(form, "language", values.Language);
        AddFormValue(form, "prompt", values.Prompt);
        AddFormValue(form, "response_format", values.ResponseFormat ?? "json");
        AddFormValue(form, "temperature", values.Temperature?.ToString(CultureInfo.InvariantCulture));
        foreach (var granularity in values.TimestampGranularities ?? [])
            AddFormValue(form, "timestamp_granularities[]", granularity);

        var timestamp = DateTime.UtcNow;
        using var response = await _client.PostAsync("v1/audio/transcriptions", form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"NexosAI transcription failed ({(int)response.StatusCode}): {raw}");

        JsonElement root;
        try { root = JsonDocument.Parse(raw).RootElement.Clone(); }
        catch (JsonException) { root = JsonSerializer.SerializeToElement(new { text = raw, model }); }
        return new(root, raw, response.GetHeaders(), timestamp);
    }

    private TranscriptionResponse ToGenericTranscriptionResponse(NexosTranscriptionResult result, string requestedModel)
    {
        var root = result.Root;
        var segments = root.TryGetProperty("segments", out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Select(item => new TranscriptionSegment
            {
                Text = ReadString(item, "text") ?? string.Empty,
                StartSecond = ReadFloat(item, "start"), EndSecond = ReadFloat(item, "end")
            }).ToList() : [];
        return new TranscriptionResponse
        {
            Text = ReadString(root, "text") ?? result.Raw,
            Language = ReadString(root, "language"),
            DurationInSeconds = ReadNullableFloat(root, "duration"),
            Segments = segments,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new ResponseData
            {
                Timestamp = result.Timestamp, Headers = result.Headers,
                ModelId = (ReadString(root, "model") ?? requestedModel).ToModelId(GetIdentifier()), Body = result.Raw
            }
        };
    }

    private NexosTranscriptionOptions GetTranscriptionOptions(Dictionary<string, JsonElement>? options)
    {
        if (options is null || !options.TryGetValue(GetIdentifier(), out var value) || value.ValueKind != JsonValueKind.Object)
            return new(null, null, "verbose_json", null, null);
        string? GetString(string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
        float? temperature = value.TryGetProperty("temperature", out var temp) && temp.TryGetSingle(out var number) ? number : null;
        var granularities = value.TryGetProperty("timestamp_granularities", out var granularity) && granularity.ValueKind == JsonValueKind.Array
            ? granularity.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray() : null;
        return new(GetString("language"), GetString("prompt"), GetString("response_format") ?? "verbose_json", temperature, granularities);
    }

    private static (byte[] Bytes, string MediaType) DecodeAudio(object audio, string mediaType)
    {
        var value = audio is JsonElement element && element.ValueKind == JsonValueKind.String ? element.GetString() : audio?.ToString();
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Audio is required.", nameof(audio));
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = value.IndexOf(',');
            if (comma <= 5) throw new FormatException("Invalid audio data URL.");
            var header = value[5..comma];
            mediaType = header.Split(';')[0];
            value = value[(comma + 1)..];
        }
        return (Convert.FromBase64String(value), mediaType);
    }

    private static string ResolveAudioFileName(string mediaType) => mediaType.ToLowerInvariant() switch
    {
        "audio/flac" => "audio.flac", "audio/mp4" => "audio.mp4", "audio/m4a" => "audio.m4a",
        "audio/ogg" => "audio.ogg", "audio/wav" or "audio/x-wav" => "audio.wav", "audio/webm" => "audio.webm", _ => "audio.mp3"
    };
    private static void AddFormValue(MultipartFormDataContent form, string name, string? value) { if (!string.IsNullOrWhiteSpace(value)) form.Add(new StringContent(value), name); }
    private static string? ReadString(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static float ReadFloat(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetSingle(out var number) ? number : 0;
    private static float? ReadNullableFloat(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.TryGetSingle(out var number) ? number
        : root.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.String && float.TryParse(value.GetString(), CultureInfo.InvariantCulture, out number) ? number : null;

    private sealed record NexosTranscriptionOptions(string? Language, string? Prompt, string? ResponseFormat, float? Temperature, string[]? TimestampGranularities);
    private sealed record NexosTranscriptionResult(JsonElement Root, string Raw, IDictionary<string, string> Headers, DateTime Timestamp);
}
