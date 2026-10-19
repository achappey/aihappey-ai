using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Neuphonic;

public sealed partial class NeuphonicProvider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (payload, language, voice) = BuildPayload(request);
        var timestamp = DateTime.UtcNow;
        using var audio = new MemoryStream();
        var events = new List<JsonElement>();
        await foreach (var frame in ReadSpeechAsync(payload, language, cancellationToken))
        {
            await audio.WriteAsync(frame.Audio, cancellationToken);
            events.Add(frame.Metadata);
        }
        if (audio.Length == 0)
            throw new InvalidOperationException("Neuphonic returned no speech audio.");
        var format = GetString(payload, "output_format");
        var encoding = GetString(payload, "encoding");
        var (mimeType, audioFormat) = format switch
        {
            "wav" => ("audio/wav", "wav"),
            "mp3" => ("audio/mpeg", "mp3"),
            null when encoding is null or "pcm_linear" => ("audio/pcm", "pcm"),
            _ => ("application/octet-stream", format ?? encoding ?? "")
        };
        return new SpeechResponse
        {
            Audio = new() { Base64 = Convert.ToBase64String(audio.ToArray()), MimeType = mimeType, Format = audioFormat },
            Request = new() { Body = payload },
            ProviderMetadata = new() { [GetIdentifier()] = JsonSerializer.SerializeToElement(new { events }) },
            Response = new() { Timestamp = timestamp, ModelId = $"{GetIdentifier()}/{language}/{voice}" }
        };
    }

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options, CancellationToken cancellationToken = default)
        => (await SpeechRequest(ToSpeechRequest(options), cancellationToken)).ToOpenAISpeechAudio();

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(options);
        if (!string.Equals(options.StreamFormat, "sse", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Neuphonic speech events require stream_format 'sse'.");
        var (payload, language, _) = BuildPayload(ToSpeechRequest(options));
        var receivedAudio = false;
        await foreach (var frame in ReadSpeechAsync(payload, language, cancellationToken))
        {
            if (frame.Audio.Length == 0)
                continue;
            receivedAudio = true;
            yield return new AudioSpeechStreamDelta { Audio = Convert.ToBase64String(frame.Audio) };
        }
        if (!receivedAudio)
            throw new InvalidOperationException("Neuphonic returned no speech audio.");
        yield return new AudioSpeechStreamDone();
    }

    private async IAsyncEnumerable<(byte[] Audio, JsonElement Metadata)> ReadSpeechAsync(
        JsonElement payload, string language, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string> { ["x-api-key"] = ResolveKey() };
        await foreach (var item in _client.GetChatCompletionSseEvents(payload,
                           $"sse/speak/{Uri.EscapeDataString(language)}", headers, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Data is null)
                continue;
            using var document = JsonDocument.Parse(item.Data);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new JsonException("Expected a Neuphonic speech event object.");
            int? statusCode = root.TryGetProperty("status_code", out var status) && status.TryGetInt32(out var code) ? code : null;
            if (statusCode is < 200 or >= 300 ||
                (root.TryGetProperty("errors", out var errors) && errors.ValueKind != JsonValueKind.Null &&
                 (errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() > 0)))
                throw new HttpRequestException($"Neuphonic speech failed: {root.GetRawText()}",
                    null, statusCode is >= 100 and <= 599 ? (System.Net.HttpStatusCode)statusCode.Value : null);

            var audio = Array.Empty<byte>();
            var metadata = root.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                if (data.TryGetProperty("audio", out var chunk) && chunk.ValueKind != JsonValueKind.Null)
                {
                    if (chunk.ValueKind != JsonValueKind.String)
                        throw new JsonException("Expected base64 Neuphonic audio.");
                    audio = Convert.FromBase64String(chunk.GetString()!);
                }
                metadata["data"] = JsonSerializer.SerializeToElement(data.EnumerateObject()
                    .Where(p => p.Name != "audio").ToDictionary(p => p.Name, p => p.Value.Clone()));
            }
            yield return (audio, JsonSerializer.SerializeToElement(metadata));
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private (JsonElement Payload, string Language, string Voice) BuildPayload(SpeechRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model) || string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Model and text are required.", nameof(request));
        if (request.Instructions is not null)
            throw new NotSupportedException("Neuphonic does not document an instructions field.");
        var model = request.Model.StartsWith("neuphonic/", StringComparison.OrdinalIgnoreCase)
            ? request.Model["neuphonic/".Length..] : request.Model;
        var separator = model.IndexOf('/');
        if (separator < 1 || separator == model.Length - 1)
            throw new ArgumentException("Neuphonic models must contain lang_code/voice_id.", nameof(request));
        var payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var native) == true)
        {
            if (native.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Neuphonic provider options must be an object.", nameof(request));
            foreach (var property in native.EnumerateObject())
                payload[property.Name] = property.Value.Clone();
        }
        var language = request.Language ?? (payload.TryGetValue("lang_code", out var lang) && lang.ValueKind == JsonValueKind.String
            ? lang.GetString() : model[..separator]);
        var voice = request.Voice ?? model[(separator + 1)..];
        if (string.IsNullOrWhiteSpace(language) || language is "." or ".." || string.IsNullOrWhiteSpace(voice))
            throw new ArgumentException("A Neuphonic language and voice are required.", nameof(request));
        payload["text"] = JsonSerializer.SerializeToElement(request.Text);
        payload["voice_id"] = JsonSerializer.SerializeToElement(voice);
        payload["lang_code"] = JsonSerializer.SerializeToElement(language);
        if (request.Speed is not null)
            payload["speed"] = JsonSerializer.SerializeToElement(request.Speed);
        if (request.OutputFormat == "pcm")
        {
            payload.Remove("output_format");
            payload["encoding"] = JsonSerializer.SerializeToElement("pcm_linear");
        }
        else if (request.OutputFormat is not null)
            payload["output_format"] = JsonSerializer.SerializeToElement(request.OutputFormat);
        return (JsonSerializer.SerializeToElement(payload), language, voice);
    }

    private static SpeechRequest ToSpeechRequest(AudioSpeechRequest options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var request = options.ToSpeechRequest();
        if (options.AdditionalProperties is not null)
            request.ProviderOptions = new() { ["neuphonic"] = JsonSerializer.SerializeToElement(options.AdditionalProperties) };
        return request;
    }
}
