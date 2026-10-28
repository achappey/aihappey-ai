using AIHappey.Core.AI;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.OpenAI;
using AIHappey.Vercel.Models;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Volink;

public partial class VolinkProvider
{
    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return _client.OpenAICompatibleSpeechRequestAsync(options, cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return _client.OpenAICompatibleStreamingSpeechAsync(options, cancellationToken: cancellationToken);
    }

    public async Task<SpeechResponse> SpeechRequest(
        SpeechRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);

        var voice = string.IsNullOrWhiteSpace(request.Voice) ? "alloy" : request.Voice;
        var format = string.IsNullOrWhiteSpace(request.OutputFormat) ? "mp3" : request.OutputFormat;
        var (audio, mimeType) = await OpenAISpeechRequestAsync(new AudioSpeechRequest
        {
            Model = request.Model,
            Input = request.Text,
            Voice = voice,
            ResponseFormat = format,
            Instructions = request.Instructions,
            Speed = request.Speed
        }, cancellationToken);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(audio),
                MimeType = mimeType,
                Format = format
            },
            Warnings = string.IsNullOrWhiteSpace(request.Language)
                ? []
                : [new { type = "unsupported", feature = "language" }],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new
            {
                contentType = mimeType,
                contentLength = audio.LongLength
            }),
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }
}
