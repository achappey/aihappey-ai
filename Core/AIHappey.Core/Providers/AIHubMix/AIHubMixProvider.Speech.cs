using AIHappey.Core.AI;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.AIHubMix;

public partial class AIHubMixProvider
{
    private static readonly JsonSerializerOptions AIHubMixSpeechJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);

        var format = string.IsNullOrWhiteSpace(request.OutputFormat) ? "mp3" : request.OutputFormat.Trim().ToLowerInvariant();
        var payload = CreateAIHubMixSpeechPayload(request.Model, request.Text,
            string.IsNullOrWhiteSpace(request.Voice) ? "alloy" : request.Voice,
            format, request.Instructions, request.Speed, request.ProviderOptions);
        var result = await SendAIHubMixSpeechAsync(payload, format, cancellationToken);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(result.Audio),
                MimeType = result.MimeType,
                Format = format
            },
            Warnings = string.IsNullOrWhiteSpace(request.Language)
                ? []
                : [new { type = "unsupported", feature = "language" }],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new
            {
                contentType = result.MimeType,
                contentLength = result.Audio.LongLength
            }),
            Request = new SpeechRequestItem { Body = payload },
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = result.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }



    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Input);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Voice);

        var format = string.IsNullOrWhiteSpace(options.ResponseFormat) ? "mp3" : options.ResponseFormat;
        var result = await SendAIHubMixSpeechAsync(
            CreateAIHubMixSpeechPayload(options.Model, options.Input, options.Voice, format,
                options.Instructions, options.Speed, null), format, cancellationToken);
        return (result.Audio, result.MimeType);
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // AIHubMix documents SSE speech, but not every upstream TTS model supports it.
        // The shared compatibility adapter is used for native SSE models; legacy TTS
        // models are represented as one protocol-correct audio delta followed by done.
        if (!options.Model.Equals("tts-1", StringComparison.OrdinalIgnoreCase)
            && !options.Model.Equals("tts-1-hd", StringComparison.OrdinalIgnoreCase))
        {
            ApplyAuthHeader();
            await foreach (var streamEvent in _client.OpenAICompatibleStreamingSpeechAsync(
                options, cancellationToken: cancellationToken))
                yield return streamEvent;
            yield break;
        }

        var (audio, _) = await OpenAISpeechRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        yield return new AudioSpeechStreamDelta { Audio = Convert.ToBase64String(audio) };
        yield return new AudioSpeechStreamDone();
    }

    private async Task<AIHubMixSpeechResult> SendAIHubMixSpeechAsync(
        Dictionary<string, object?> payload,
        string? responseFormat,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, AIHubMixSpeechJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"AIHubMix speech request failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(audio)}");

        return new AIHubMixSpeechResult(audio,
            response.Content.Headers.ContentType?.MediaType ?? ResolveAIHubMixSpeechMimeType(responseFormat),
            response.GetHeaders());
    }

    private static Dictionary<string, object?> CreateAIHubMixSpeechPayload(
        string model, string input, string voice, string? responseFormat,
        string? instructions, float? speed, Dictionary<string, JsonElement>? providerOptions)
    {
        var payload = CopyAIHubMixProviderOptions(providerOptions);
        payload["model"] = model;
        payload["input"] = input;
        payload["voice"] = voice;
        if (!string.IsNullOrWhiteSpace(responseFormat)) payload["response_format"] = responseFormat;
        if (!string.IsNullOrWhiteSpace(instructions)) payload["instructions"] = instructions;
        if (speed is not null) payload["speed"] = speed.Value;
        payload.Remove("stream_format");
        return payload;
    }

    private static string ResolveAIHubMixSpeechMimeType(string? format) => format?.Trim().ToLowerInvariant() switch
    {
        "mp3" => "audio/mpeg",
        "opus" => "audio/opus",
        "aac" => "audio/aac",
        "flac" => "audio/flac",
        "wav" => "audio/wav",
        "pcm" => "audio/pcm",
        _ => "application/octet-stream"
    };

    private sealed record AIHubMixSpeechResult(byte[] Audio, string MimeType, Dictionary<string, string> Headers);
}
