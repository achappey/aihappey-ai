using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.NanoGPT;

public partial class NanoGPTProvider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
        var format = string.IsNullOrWhiteSpace(request.OutputFormat) ? "mp3" : request.OutputFormat;
        var payload = CreateNanoGPTSpeechPayload(request.Model, request.Text, request.Voice, format,
            request.Language, request.Instructions, request.Speed, request.ProviderOptions, false);
        var result = await SendNanoGPTSpeechAsync(payload, format, cancellationToken);
        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse { Base64 = Convert.ToBase64String(result.Audio), MimeType = result.MimeType, Format = format },
            Warnings = [], ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Metadata),
            Request = new SpeechRequestItem { Body = payload },
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow, Headers = result.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier()), Body = result.Metadata
            }
        };
    }

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Input);
        var format = string.IsNullOrWhiteSpace(options.ResponseFormat) ? "mp3" : options.ResponseFormat;
        var result = await SendNanoGPTSpeechAsync(CreateNanoGPTSpeechPayload(options.Model, options.Input,
            options.Voice, format, null, options.Instructions, options.Speed, options.AdditionalProperties, false), format, cancellationToken);
        return (result.Audio, result.MimeType);
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Input);
        var format = string.IsNullOrWhiteSpace(options.ResponseFormat) ? "mp3" : options.ResponseFormat;
        var payload = CreateNanoGPTSpeechPayload(options.Model, options.Input, options.Voice, format,
            null, options.Instructions, options.Speed, options.AdditionalProperties, true);
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, MediaTypeNames.Application.Json) };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            EnsureNanoGPTSuccess(response, raw, "speech request");
        }
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            yield return new AudioSpeechStreamDelta { Audio = Convert.ToBase64String(buffer, 0, read) };
        }
        yield return new AudioSpeechStreamDone();
    }

    private async Task<NanoGPTSpeechResult> SendNanoGPTSpeechAsync(Dictionary<string, object?> payload,
        string? format, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, MediaTypeNames.Application.Json) };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"NanoGPT speech request failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(bytes)}");
        var metadata = JsonSerializer.SerializeToElement(new
        {
            content_type = response.Content.Headers.ContentType?.MediaType,
            content_length = bytes.LongLength
        });
        return new NanoGPTSpeechResult(bytes,
            response.Content.Headers.ContentType?.MediaType ?? ResolveNanoGPTSpeechMimeType(format),
            response.GetHeaders(), metadata);
    }

    private static Dictionary<string, object?> CreateNanoGPTSpeechPayload(string model, string input,
        string? voice, string? format, string? language, string? instructions, float? speed,
        Dictionary<string, JsonElement>? providerOptions, bool stream)
    {
        var payload = CopyNanoGPTOptions(providerOptions);
        payload["model"] = model; payload["input"] = input;
        if (!string.IsNullOrWhiteSpace(voice)) payload["voice"] = voice;
        if (!string.IsNullOrWhiteSpace(format)) payload["response_format"] = format;
        if (!string.IsNullOrWhiteSpace(language)) payload["language"] = language;
        if (!string.IsNullOrWhiteSpace(instructions)) payload["instructions"] = instructions;
        if (speed is not null) payload["speed"] = speed.Value;
        payload["stream"] = stream; payload.Remove("stream_format");
        return payload;
    }

    private static string ResolveNanoGPTSpeechMimeType(string? format) => format?.Trim().ToLowerInvariant() switch
    {
        "mp3" => "audio/mpeg", "opus" => "audio/opus", "aac" => "audio/aac",
        "flac" => "audio/flac", "wav" => "audio/wav", "pcm" => "audio/pcm", _ => "application/octet-stream"
    };

    private sealed record NanoGPTSpeechResult(byte[] Audio, string MimeType,
        Dictionary<string, string> Headers, JsonElement Metadata);
}
