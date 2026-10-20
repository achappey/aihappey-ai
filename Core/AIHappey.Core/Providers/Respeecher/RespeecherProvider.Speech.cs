using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Respeecher;

public sealed partial class RespeecherProvider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (payload, model, voice) = BuildPayload(request, streaming: false);
        var timestamp = DateTime.UtcNow;
        using var message = CreateSpeechMessage(payload, model, streaming: false);
        using var response = await _client.SendAsync(message, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (audio.Length == 0)
            throw new InvalidOperationException("Respeecher returned no speech audio.");
        return new SpeechResponse
        {
            Audio = new() { Base64 = Convert.ToBase64String(audio), MimeType = "audio/wav", Format = "wav" },
            Request = new() { Body = payload },
            Response = new() { Timestamp = timestamp, ModelId = $"{GetIdentifier()}/{model}/{voice}" }
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
            throw new NotSupportedException("Respeecher speech events require stream_format 'sse'.");
        var (payload, model, _) = BuildPayload(ToSpeechRequest(options), streaming: true);
        using var message = CreateSpeechMessage(payload, model, streaming: true);
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var receivedAudio = false;
        // Despite its /sse name, Space sends newline-delimited JSON, not SSE frames.
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line))
                continue;
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var type = GetString(root, "type");
            if (type == "error")
            {
                var status = root.TryGetProperty("status_code", out var code) &&
                    code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var number) &&
                    number is >= 100 and <= 599 ? (System.Net.HttpStatusCode?)number : null;
                throw new HttpRequestException($"Respeecher speech failed: {root.GetRawText()}", null, status);
            }
            if (type != "chunk" || GetString(root, "data") is not { } data)
                throw new JsonException("Expected a Respeecher chunk with base64 data.");
            var audio = Convert.FromBase64String(data);
            if (audio.Length == 0)
                continue;
            receivedAudio = true;
            yield return new AudioSpeechStreamDelta { Audio = Convert.ToBase64String(audio) };
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!receivedAudio)
            throw new InvalidOperationException("Respeecher returned no speech audio.");
        yield return new AudioSpeechStreamDone();
    }

    private HttpRequestMessage CreateSpeechMessage(JsonElement payload, string model, bool streaming)
    {
        var key = ResolveKey();
        var message = new HttpRequestMessage(HttpMethod.Post,
            $"{Uri.EscapeDataString(model)}/tts/{(streaming ? "sse" : "bytes")}")
        {
            Content = JsonContent.Create(payload)
        };
        message.Headers.Add("X-API-Key", key);
        return message;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Respeecher request failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(cancellationToken)}",
                null, response.StatusCode);
    }

    private (JsonElement Payload, string Model, string Voice) BuildPayload(SpeechRequest request, bool streaming)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model) || string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Model and text are required.", nameof(request));
        if (request.Speed is not null || request.Instructions is not null)
            throw new NotSupportedException("Respeecher does not document standard speed or instructions controls. Use native sampling_params.");
        if (request.Language is not null)
            throw new NotSupportedException("Respeecher language is selected by the model path, not a separate language field.");
        if (request.OutputFormat is not null && request.OutputFormat != (streaming ? "pcm" : "wav"))
            throw new NotSupportedException(streaming
                ? "Respeecher native streaming returns raw PCM only."
                : "Respeecher byte synthesis returns WAV only.");
        var modelId = request.Model.StartsWith("respeecher/", StringComparison.OrdinalIgnoreCase)
            ? request.Model["respeecher/".Length..] : request.Model;
        var separator = modelId.IndexOf('/');
        if (separator < 1 || separator == modelId.Length - 1)
            throw new ArgumentException("Respeecher models must contain model/voice_id.", nameof(request));
        var model = modelId[..separator];
        if (model.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new ArgumentException("Invalid Respeecher model path segment.", nameof(request));
        var voiceId = request.Voice ?? modelId[(separator + 1)..];
        if (string.IsNullOrWhiteSpace(voiceId))
            throw new ArgumentException("A Respeecher voice is required.", nameof(request));
        var payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var native) == true)
        {
            if (native.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Respeecher provider options must be an object.", nameof(request));
            foreach (var property in native.EnumerateObject())
                payload[property.Name] = property.Value.Clone();
        }
        var voice = ReadObject(payload, "voice");
        voice["id"] = JsonSerializer.SerializeToElement(voiceId);
        payload["voice"] = JsonSerializer.SerializeToElement(voice);
        payload["transcript"] = JsonSerializer.SerializeToElement(request.Text);
        if (streaming && request.OutputFormat == "pcm")
        {
            var format = ReadObject(payload, "output_format");
            format["encoding"] = JsonSerializer.SerializeToElement("pcm_s16le");
            payload["output_format"] = JsonSerializer.SerializeToElement(format);
        }
        return (JsonSerializer.SerializeToElement(payload), model, voiceId);
    }

    private static Dictionary<string, JsonElement> ReadObject(Dictionary<string, JsonElement> payload, string name)
    {
        if (!payload.TryGetValue(name, out var element) || element.ValueKind == JsonValueKind.Null)
            return new(StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Object)
            throw new ArgumentException($"Respeecher {name} must be an object.");
        return element.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
    }

    private static SpeechRequest ToSpeechRequest(AudioSpeechRequest options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var request = options.ToSpeechRequest();
        if (options.AdditionalProperties is not null)
        {
            var native = options.AdditionalProperties
                .Where(property => property.Key != "providerOptions")
                .ToDictionary(property => property.Key, property => property.Value.Clone(), StringComparer.Ordinal);
            if (options.AdditionalProperties.TryGetValue("providerOptions", out var envelope))
            {
                if (envelope.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException("providerOptions must be an object.", nameof(options));
                if (envelope.TryGetProperty("respeecher", out var providerOptions))
                {
                    if (providerOptions.ValueKind != JsonValueKind.Object)
                        throw new ArgumentException("Respeecher provider options must be an object.", nameof(options));
                    foreach (var property in providerOptions.EnumerateObject())
                        native[property.Name] = property.Value.Clone();
                }
            }
            request.ProviderOptions = new() { ["respeecher"] = JsonSerializer.SerializeToElement(native) };
        }
        return request;
    }
}
