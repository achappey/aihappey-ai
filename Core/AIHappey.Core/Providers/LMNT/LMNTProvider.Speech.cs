using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.LMNT;

public sealed partial class LMNTProvider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        var payload = BuildPayload(request);
        var timestamp = DateTime.UtcNow;
        using var message = CreateSpeechRequest(payload);
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length == 0)
            throw new InvalidOperationException("LMNT returned no speech audio.");
        var format = payload.TryGetValue("format", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : "";
        var mimeType = response.Content.Headers.ContentType?.MediaType;
        if (string.IsNullOrWhiteSpace(mimeType) || mimeType == "application/octet-stream")
            mimeType = format switch
            {
                "mp3" => "audio/mpeg",
                "wav" or "ulaw" => "audio/wav",
                "webm" => "audio/webm",
                "aac" => "audio/aac",
                _ => "application/octet-stream"
            };
        if (format.Length == 0)
            format = mimeType switch
            {
                "audio/mpeg" => "mp3",
                "audio/wav" or "audio/x-wav" => "wav",
                "audio/webm" => "webm",
                "audio/aac" => "aac",
                _ => ""
            };
        var metadata = JsonSerializer.SerializeToElement(new { bytes = bytes.Length, headers = response.GetHeaders() });
        return new SpeechResponse
        {
            Audio = new() { Base64 = Convert.ToBase64String(bytes), MimeType = mimeType, Format = format },
            Request = new() { Body = payload },
            ProviderMetadata = new() { [GetIdentifier()] = metadata },
            Response = new()
            {
                Timestamp = timestamp,
                ModelId = $"{GetIdentifier()}/{payload["voice"].GetString()}",
                Headers = response.GetHeaders(),
                Body = metadata
            }
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
            throw new NotSupportedException("LMNT speech events require stream_format 'sse'.");
        var payload = BuildPayload(ToSpeechRequest(options));
        using var message = CreateSpeechRequest(payload);
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[8192];
        var receivedAudio = false;
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            receivedAudio = true;
            yield return new AudioSpeechStreamDelta { Audio = Convert.ToBase64String(buffer, 0, count) };
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!receivedAudio)
            throw new InvalidOperationException("LMNT returned no speech audio.");
        yield return new AudioSpeechStreamDone();
    }

    private static SpeechRequest ToSpeechRequest(AudioSpeechRequest options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var request = options.ToSpeechRequest();
        if (options.AdditionalProperties is not null)
            request.ProviderOptions = new() { ["lmnt"] = JsonSerializer.SerializeToElement(options.AdditionalProperties) };
        return request;
    }

    private Dictionary<string, JsonElement> BuildPayload(SpeechRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Model) || string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Model and text are required.", nameof(request));
        if (request.Speed is not null || request.Instructions is not null)
            throw new NotSupportedException("LMNT does not document speed or instructions fields.");
        var voice = request.Model.StartsWith("lmnt/", StringComparison.OrdinalIgnoreCase)
            ? request.Model["lmnt/".Length..] : request.Model;
        if (string.IsNullOrWhiteSpace(voice))
            throw new ArgumentException("An LMNT voice ID is required.", nameof(request));
        if (request.Voice is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Voice))
                throw new ArgumentException("Voice must not be empty.", nameof(request));
            voice = request.Voice;
        }
        var payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var options) == true
            && options.ValueKind == JsonValueKind.Object)
            foreach (var property in options.EnumerateObject())
                payload[property.Name] = property.Value.Clone();
        payload["text"] = JsonSerializer.SerializeToElement(request.Text);
        payload["voice"] = JsonSerializer.SerializeToElement(voice);
        if (request.OutputFormat is not null)
            payload["format"] = JsonSerializer.SerializeToElement(request.OutputFormat == "pcm" ? "pcm_s16le" : request.OutputFormat);
        if (request.Language is not null)
            payload["language"] = JsonSerializer.SerializeToElement(request.Language);
        return payload;
    }

    private HttpRequestMessage CreateSpeechRequest(Dictionary<string, JsonElement> payload)
    {
        var message = CreateRequest(HttpMethod.Post, "v1/ai/speech/bytes");
        message.Content = JsonContent.Create(payload);
        message.Headers.Accept.ParseAdd("application/octet-stream");
        return message;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"LMNT request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {await response.Content.ReadAsStringAsync(cancellationToken)}",
                null, response.StatusCode);
    }
}
