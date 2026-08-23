using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Zenlayer;

public partial class ZenlayerProvider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text)) throw new ArgumentException("Text is required.", nameof(request));
        var payload = CreateVercelPayload(request.ProviderOptions, GetIdentifier(),
            "model", "input", "voice", "response_format", "speed", "instructions", "language");
        payload["model"] = request.Model;
        payload["input"] = request.Text;
        Set(payload, "voice", request.Voice);
        Set(payload, "response_format", request.OutputFormat);
        Set(payload, "speed", request.Speed);
        Set(payload, "instructions", request.Instructions);
        Set(payload, "language", request.Language);
        var result = await SynthesizeSpeechAsync(payload, cancellationToken);
        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(result.Audio),
                MimeType = result.MimeType,
                Format = AudioFormat(request.OutputFormat, result.MimeType)
            },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new { contentType = result.MimeType }),
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = result.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier())
            },
            Request = new SpeechRequestItem { Body = payload }
        };
    }

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Model)) throw new ArgumentException("Model is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Input)) throw new ArgumentException("Input is required.", nameof(options));
        var payload = CreateOpenAIPayload(options.AdditionalProperties,
            "model", "input", "voice", "response_format", "instructions", "speed", "stream_format");
        payload["model"] = options.Model;
        payload["input"] = options.Input;
        Set(payload, "voice", options.Voice);
        Set(payload, "response_format", options.ResponseFormat);
        Set(payload, "instructions", options.Instructions);
        Set(payload, "speed", options.Speed);
        var result = await SynthesizeSpeechAsync(payload, cancellationToken);
        return (result.Audio, result.MimeType);
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (audio, _) = await OpenAISpeechRequestAsync(options, cancellationToken);
        yield return new AudioSpeechStreamDelta { Audio = Convert.ToBase64String(audio) };
        yield return new AudioSpeechStreamDone();
    }

    private async Task<ZenlayerSpeechResult> SynthesizeSpeechAsync(JsonObject payload, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(payload.ToJsonString(MediaJson), System.Text.Encoding.UTF8, "application/json")
        };
        using var response = await _client.SendAsync(request, cancellationToken);
        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Zenlayer speech synthesis failed ({(int)response.StatusCode}): {System.Text.Encoding.UTF8.GetString(audio)}");
        return new ZenlayerSpeechResult(audio, response.Content.Headers.ContentType?.MediaType ?? "audio/mpeg", response.GetHeaders());
    }

    private static string AudioFormat(string? requested, string mimeType) => !string.IsNullOrWhiteSpace(requested) ? requested : mimeType switch
    {
        "audio/wav" => "wav", "audio/flac" => "flac", "audio/aac" => "aac", "audio/opus" => "opus", "audio/pcm" => "pcm", _ => "mp3"
    };
    private sealed record ZenlayerSpeechResult(byte[] Audio, string MimeType, Dictionary<string, string> Headers);
}
