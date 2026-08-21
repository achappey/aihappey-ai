using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.FastRouter;

public partial class FastRouterProvider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text)) throw new ArgumentException("Text is required.", nameof(request));

        var payload = CreateFastRouterPayload(request.ProviderOptions,
            "model", "input", "voice", "response_format", "output_audio_codec", "speed", "instructions", "language");
        payload["model"] = request.Model;
        payload["input"] = request.Text;
        AddFastRouterSpeechOption(payload, "voice", request.Voice);
        AddFastRouterSpeechOption(payload, "output_audio_codec", request.OutputFormat);
        AddFastRouterSpeechOption(payload, "speed", request.Speed);
        AddFastRouterSpeechOption(payload, "instructions", request.Instructions);
        AddFastRouterSpeechOption(payload, "language", request.Language);

        var result = await SynthesizeFastRouterSpeechAsync(payload, request.OutputFormat, cancellationToken);
        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(result.Audio),
                MimeType = result.MimeType,
                Format = ResolveFastRouterAudioFormat(request.OutputFormat, result.MimeType)
            },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root),
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = result.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = result.Root
            },
            Request = new SpeechRequestItem { Body = payload }
        };
    }

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Model)) throw new ArgumentException("'model' is a required field", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Input)) throw new ArgumentException("'input' is a required field", nameof(options));

        var payload = CreateFastRouterPayload(options.AdditionalProperties,
            "model", "input", "voice", "response_format", "instructions", "speed", "stream_format");
        payload["model"] = options.Model;
        payload["input"] = options.Input;
        AddFastRouterSpeechOption(payload, "voice", options.Voice);
        AddFastRouterSpeechOption(payload, "output_audio_codec", options.ResponseFormat);
        AddFastRouterSpeechOption(payload, "instructions", options.Instructions);
        AddFastRouterSpeechOption(payload, "speed", options.Speed);

        var result = await SynthesizeFastRouterSpeechAsync(payload, options.ResponseFormat, cancellationToken);
        return (result.Audio, result.MimeType);
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (audio, _) = await OpenAISpeechRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        yield return new AudioSpeechStreamDelta { Audio = Convert.ToBase64String(audio) };
        yield return new AudioSpeechStreamDone();
    }

    private async Task<FastRouterSpeechResult> SynthesizeFastRouterSpeechAsync(
        JsonObject payload,
        string? requestedFormat,
        CancellationToken cancellationToken)
    {
        var result = await SendFastRouterJsonAsync(HttpMethod.Post, "v1/audio/speech", payload, "speech synthesis", cancellationToken);
        if (!result.Root.TryGetProperty("audios", out var audios) || audios.ValueKind != JsonValueKind.Array
            || audios.GetArrayLength() == 0 || audios[0].ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("FastRouter speech response did not contain an audio clip.");

        var base64 = audios[0].GetString();
        if (string.IsNullOrWhiteSpace(base64))
            throw new InvalidOperationException("FastRouter speech response contained an empty audio clip.");

        try
        {
            return new FastRouterSpeechResult(
                Convert.FromBase64String(base64),
                ResolveFastRouterAudioMimeType(requestedFormat),
                result.Root,
                result.Headers);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("FastRouter speech response contained invalid base64 audio.", ex);
        }
    }

    private static void AddFastRouterSpeechOption(JsonObject payload, string name, object? value)
    {
        if (value is not null) payload[name] = JsonValue.Create(value);
    }

    private sealed record FastRouterSpeechResult(
        byte[] Audio,
        string MimeType,
        JsonElement Root,
        Dictionary<string, string> Headers);
}
