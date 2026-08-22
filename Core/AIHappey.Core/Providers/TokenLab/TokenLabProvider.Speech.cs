using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.TokenLab;

public partial class TokenLabProvider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text)) throw new ArgumentException("Text is required.", nameof(request));

        var payload = CreateTokenLabPayload(GetTokenLabProviderOptions(request.ProviderOptions));
        payload["model"] = request.Model;
        payload["input"] = request.Text;
        if (!string.IsNullOrWhiteSpace(request.Voice)) payload["voice"] = request.Voice;
        if (!string.IsNullOrWhiteSpace(request.OutputFormat)) payload["response_format"] = request.OutputFormat;
        if (!string.IsNullOrWhiteSpace(request.Instructions)) payload["instructions"] = request.Instructions;
        if (request.Speed is not null) payload["speed"] = request.Speed;
        if (!string.IsNullOrWhiteSpace(request.Language)) payload["language"] = request.Language;

        var result = await SendTokenLabBinaryAsync("v1/audio/speech", ToJsonContent(payload), "speech", cancellationToken);
        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(result.Bytes),
                MimeType = result.MimeType,
                Format = request.OutputFormat ?? GetFormatFromMimeType(result.MimeType)
            },
            ProviderMetadata = CreateTokenLabMetadata(new { contentType = result.MimeType }),
            Request = new SpeechRequestItem { Body = payload },
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Headers = result.Headers
            }
        };
    }

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Model)) throw new ArgumentException("Model is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Input)) throw new ArgumentException("Input is required.", nameof(options));
        var payload = JsonSerializer.SerializeToNode(options, TokenLabJson)!.AsObject();
        CopyAdditionalProperties(payload, options.AdditionalProperties);
        var result = await SendTokenLabBinaryAsync("v1/audio/speech", ToJsonContent(payload), "speech", cancellationToken);
        return (result.Bytes, result.MimeType);
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var result = await OpenAISpeechRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.Audio.Length > 0)
            yield return new AudioSpeechStreamDelta { Audio = Convert.ToBase64String(result.Audio) };
        yield return new AudioSpeechStreamDone();
    }
}
