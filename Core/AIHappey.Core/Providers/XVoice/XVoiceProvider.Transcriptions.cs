using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Extensions;
using AIHappey.Core.MCP.Media;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.XVoice;

public partial class XVoiceProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var options = TranscriptionOptions(request.ProviderOptions);
        using var message = CreateTranscriptionMessage(request.Model, DecodeTranscriptionFile(request), request.MediaType, options);
        ApplyAuthHeader();
        using var response = await SendAsync(message, cancellationToken);
        var timestamp = DateTime.UtcNow;
        JsonElement body;
        string text;
        if (TranscriptionStreamRequested(options))
        {
            body = default;
            await foreach (var item in ReadEventsAsync(response, "transcript.text.done", cancellationToken))
            {
                if (TranscriptionString(item, "type") == "transcript.text.done")
                    body = item.Clone();
            }
            text = RequiredTranscriptionString(body, "text");
        }
        else
        {
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            if (TranscriptionResponseFormat(options) == "text")
            {
                text = raw;
                body = JsonSerializer.SerializeToElement(raw);
            }
            else
            {
                using var document = JsonDocument.Parse(raw);
                body = document.RootElement.Clone();
                text = TranscriptionString(body, "text")
                    ?? throw new JsonException("xVoice transcription response is missing text.");
            }
        }
        return new TranscriptionResponse
        {
            Text = text,
            Language = TranscriptionString(body, "language"),
            DurationInSeconds = TranscriptionDuration(body) is { } duration ? (float)duration : null,
            ProviderMetadata = new() { [ProviderId] = body },
            Response = new()
            {
                Timestamp = timestamp,
                ModelId = $"{ProviderId}/{TranscriptionModel(request.Model)}",
                Headers = response.GetHeaders(),
                Body = body
            }
        };
    }

    // This contract contains a complete encoded file, not live microphone input.
    public async IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(
        StreamingTranscriptionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var type = request.InputAudioFormat?.Type
            ?? throw new ArgumentException("inputAudioFormat.type is required.", nameof(request));
        var mediaType = type.Contains('/') ? type : type.ToLowerInvariant() switch
        {
            "mp3" => "audio/mpeg",
            "m4a" or "mp4" => "audio/mp4",
            "flac" => "audio/flac",
            "wav" => "audio/wav",
            "ogg" => "audio/ogg",
            "webm" => "audio/webm",
            _ => throw new NotSupportedException("xVoice file transcription requires FLAC, M4A, MP3, MP4, OGG, WAV, or WebM. Live raw audio requires a documented realtime protocol.")
        };
        var fileRequest = new TranscriptionRequest
        {
            Model = request.Model, Audio = request.Audio, MediaType = mediaType, ProviderOptions = request.ProviderOptions
        };
        var options = TranscriptionOptions(request.ProviderOptions);
        options["stream"] = JsonSerializer.SerializeToElement(true);
        using var message = CreateTranscriptionMessage(request.Model, DecodeTranscriptionFile(fileRequest), mediaType, options);
        ApplyAuthHeader();
        using var response = await SendAsync(message, cancellationToken);
        yield return new TranscriptionStreamStartPart();
        yield return new TranscriptionResponseMetadataPart
        {
            Timestamp = DateTime.UtcNow,
            ModelId = $"{ProviderId}/{TranscriptionModel(request.Model)}",
            Headers = response.GetHeaders()
        };
        await foreach (var item in ReadEventsAsync(response, "transcript.text.done", cancellationToken))
        {
            if (request.IncludeRawChunks == true)
                yield return new TranscriptionRawPart { RawValue = item.Clone() };
            if (TranscriptionString(item, "type") == "transcript.text.delta")
            {
                var delta = RequiredTranscriptionString(item, "delta");
                yield return new TranscriptionDeltaPart { Delta = delta };
            }
            if (TranscriptionString(item, "type") == "transcript.text.done")
            {
                var fullText = RequiredTranscriptionString(item, "text");
                yield return new TranscriptionFinalPart { Text = fullText };
                yield return new TranscriptionFinishPart
                {
                    Text = fullText,
                    Language = TranscriptionString(item, "language"),
                    DurationInSeconds = TranscriptionDuration(item),
                    ProviderMetadata = new() { [ProviderId] = item.Clone() }
                };
            }
        }
    }

    private static Dictionary<string, JsonElement> TranscriptionOptions(Dictionary<string, JsonElement>? providerOptions)
    {
        if (providerOptions?.TryGetValue(ProviderId, out var native) != true)
            return new(StringComparer.Ordinal);
        if (native.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("providerOptions.xvoice must be an object.");
        return native.EnumerateObject().Where(p => p.Value.ValueKind != JsonValueKind.Null)
            .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
    }

    private static string TranscriptionModel(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var value = model.Trim();
        var local = value.StartsWith(ProviderId + "/", StringComparison.OrdinalIgnoreCase)
            ? value[(ProviderId.Length + 1)..] : value;
        ArgumentException.ThrowIfNullOrWhiteSpace(local, nameof(model));
        return local;
    }

    private static string TranscriptionResponseFormat(Dictionary<string, JsonElement> options)
    {
        var format = options.TryGetValue("response_format", out var value) ? value.GetString() : "json";
        if (format is not ("json" or "text"))
            throw new NotSupportedException($"xVoice transcription response_format '{format}' is unsupported; use json or text.");
        return format;
    }

    private static bool TranscriptionStreamRequested(Dictionary<string, JsonElement> options)
        => options.TryGetValue("stream", out var value) && value.GetBoolean();

    private static HttpRequestMessage CreateTranscriptionMessage(
        string model, byte[] bytes, string mediaType, Dictionary<string, JsonElement> options)
    {
        TranscriptionResponseFormat(options);
        TranscriptionStreamRequested(options);
        if (options.TryGetValue("prompt", out var prompt)
            && (prompt.ValueKind != JsonValueKind.String || prompt.GetString()!.EnumerateRunes().Count() > 1000))
            throw new ArgumentException("xVoice transcription prompt must be a string of at most 1000 characters.");
        var extension = TranscriptionExtension(mediaType);
        var form = new MultipartFormDataContent();
        try
        {
            form.Add(new StringContent(TranscriptionModel(model)), "model");
            foreach (var field in options)
            {
                if (field.Key is "model" or "file" || field.Value.ValueKind == JsonValueKind.Null)
                    continue;
                form.Add(new StringContent(field.Value.ValueKind == JsonValueKind.String
                    ? field.Value.GetString()! : field.Value.GetRawText()), field.Key);
            }
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
            form.Add(file, "file", "audio" + extension);
            return new HttpRequestMessage(HttpMethod.Post, "v1/audio/transcriptions") { Content = form };
        }
        catch
        {
            form.Dispose();
            throw;
        }
    }

    private static string TranscriptionExtension(string mediaType) => mediaType?.ToLowerInvariant() switch
    {
        "audio/flac" or "audio/x-flac" => ".flac",
        "audio/mp4" or "audio/m4a" or "audio/x-m4a" => ".m4a",
        "video/mp4" => ".mp4",
        "audio/mpeg" or "audio/mp3" => ".mp3",
        "audio/ogg" or "application/ogg" => ".ogg",
        "audio/wav" or "audio/x-wav" or "audio/wave" => ".wav",
        "audio/webm" or "video/webm" => ".webm",
        _ => throw new NotSupportedException($"xVoice transcription does not support media type '{mediaType}'.")
    };

    private static byte[] DecodeTranscriptionFile(TranscriptionRequest request)
    {
        var audio = request.Audio is JsonElement element && element.ValueKind == JsonValueKind.String
            ? element.GetString() : request.Audio?.ToString();
        ArgumentException.ThrowIfNullOrWhiteSpace(audio);
        if (MediaContentHelpers.TryParseDataUrl(audio, out _, out var base64))
            audio = base64;
        try
        {
            var bytes = Convert.FromBase64String(audio);
            if (bytes.Length == 0)
                throw new ArgumentException("Audio is required.");
            if (bytes.Length > 25 * 1024 * 1024)
                throw new ArgumentException("xVoice transcription files must not exceed 25 MB.", nameof(request));
            return bytes;
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Audio must be valid base64 or a base64 data URL.", nameof(request), exception);
        }
    }

    private static string? TranscriptionString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string RequiredTranscriptionString(JsonElement element, string property)
        => TranscriptionString(element, property)
            ?? throw new JsonException($"xVoice transcription event is missing '{property}'.");

    private static double? TranscriptionDuration(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        if (element.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
            && usage.TryGetProperty("seconds", out var seconds) && seconds.TryGetDouble(out var duration))
            return duration;
        return element.TryGetProperty("duration", out var value) && value.TryGetDouble(out var fallback)
            ? fallback : null;
    }
}
