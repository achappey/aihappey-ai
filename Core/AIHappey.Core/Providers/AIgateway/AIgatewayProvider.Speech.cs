using System.Runtime.CompilerServices;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.AIgateway;

public partial class AIgatewayProvider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (audio, mimeType) = await SynthesizeSpeechAsync(request.Model, request.Text, request.Voice,
            request.OutputFormat, request.Speed, request.ProviderOptions, cancellationToken);
        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse { Base64 = Convert.ToBase64String(audio), MimeType = mimeType, Format = ResolveAIgatewaySpeechFormat(request.OutputFormat, mimeType) },
            Warnings = string.IsNullOrWhiteSpace(request.Instructions) && string.IsNullOrWhiteSpace(request.Language)
                ? []
                : new object[] { new { type = "unsupported", feature = "instructions/language" } },
            Response = new ResponseData { Timestamp = DateTime.UtcNow, ModelId = request.Model.ToModelId(GetIdentifier()) }
        };
    }

    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Model)) throw new ArgumentException("'model' is a required field", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Input)) throw new ArgumentException("'input' is a required field", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Voice)) throw new ArgumentException("'voice' is a required field", nameof(options));
        return SynthesizeSpeechAsync(options.Model, options.Input, options.Voice, options.ResponseFormat, options.Speed, null, cancellationToken);
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (audio, _) = await OpenAISpeechRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        yield return new AudioSpeechStreamDelta { Audio = Convert.ToBase64String(audio) };
        yield return new AudioSpeechStreamDone();
    }

    private async Task<(byte[] Audio, string MimeType)> SynthesizeSpeechAsync(string model, string input, string? voice,
        string? responseFormat, float? speed, Dictionary<string, System.Text.Json.JsonElement>? providerOptions,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        var payload = CreateAIgatewayPayload(new()
        {
            ["model"] = model, ["input"] = input, ["voice"] = voice,
            ["response_format"] = responseFormat, ["speed"] = speed
        }, providerOptions, "model", "input", "voice", "response_format", "speed", "stream");
        payload.Remove("stream");
        using var request = CreateAIgatewayJsonRequest(HttpMethod.Post, "v1/audio/speech", payload);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"AIgateway speech failed ({(int)response.StatusCode}): {System.Text.Encoding.UTF8.GetString(audio)}");
        return (audio, response.Content.Headers.ContentType?.MediaType ?? ResolveAIgatewaySpeechMimeType(responseFormat));
    }
}
