using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.PayPerQ;

public partial class PayPerQProvider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
        var format = string.IsNullOrWhiteSpace(request.OutputFormat) ? "mp3" : request.OutputFormat;
        var payload = CreatePayPerQSpeechPayload(request.Model, request.Text, request.Voice, format,
            request.Language, request.Instructions, request.Speed, request.ProviderOptions);
        var result = await SendPayPerQSpeechAsync(payload, format, cancellationToken);
        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse { Base64 = Convert.ToBase64String(result.Audio), MimeType = result.MimeType, Format = format },
            Warnings = [],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Metadata),
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
        var result = await SendPayPerQSpeechAsync(CreatePayPerQSpeechPayload(options.Model, options.Input,
            options.Voice, format, null, options.Instructions, options.Speed, options.AdditionalProperties), format, cancellationToken);
        return (result.Audio, result.MimeType);
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (audio, _) = await OpenAISpeechRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        yield return new AudioSpeechStreamDelta { Audio = Convert.ToBase64String(audio) };
        yield return new AudioSpeechStreamDone();
    }

    private async Task<PayPerQSpeechResult> SendPayPerQSpeechAsync(Dictionary<string, object?> payload,
        string? format, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, MediaTypeNames.Application.Json) };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"PayPerQ speech request failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(bytes)}");
        var metadata = JsonSerializer.SerializeToElement(new
        {
            content_type = response.Content.Headers.ContentType?.MediaType,
            content_length = bytes.LongLength
        });
        return new PayPerQSpeechResult(bytes,
            response.Content.Headers.ContentType?.MediaType ?? ResolvePayPerQSpeechMimeType(format),
            response.GetHeaders(), metadata);
    }

    private static Dictionary<string, object?> CreatePayPerQSpeechPayload(string model, string input,
        string? voice, string? format, string? language, string? instructions, float? speed,
        Dictionary<string, JsonElement>? providerOptions)
    {
        var payload = CopyPayPerQOptions(providerOptions);
        payload["model"] = model; payload["input"] = input;
        if (!string.IsNullOrWhiteSpace(voice)) payload["voice"] = voice;
        if (!string.IsNullOrWhiteSpace(format)) payload["response_format"] = format;
        if (!string.IsNullOrWhiteSpace(language)) payload["language"] = language;
        if (!string.IsNullOrWhiteSpace(instructions)) payload["instructions"] = instructions;
        if (speed is not null) payload["speed"] = speed.Value;
        payload.Remove("stream"); payload.Remove("stream_format");
        return payload;
    }

    private static string ResolvePayPerQSpeechMimeType(string? format) => format?.Trim().ToLowerInvariant() switch
    {
        "mp3" => "audio/mpeg", "opus" => "audio/opus", "aac" => "audio/aac",
        "flac" => "audio/flac", "wav" => "audio/wav", "pcm" => "audio/pcm", _ => "application/octet-stream"
    };

    private sealed record PayPerQSpeechResult(byte[] Audio, string MimeType,
        Dictionary<string, string> Headers, JsonElement Metadata);
}
