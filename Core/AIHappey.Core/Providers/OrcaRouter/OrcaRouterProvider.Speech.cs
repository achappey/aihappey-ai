using AIHappey.Core.AI;
using AIHappey.Common.Extensions;
using AIHappey.Core.Extensions;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.OrcaRouter;

public partial class OrcaRouterProvider
{
    private const string SpeechEndpoint = "v1/audio/speech";

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Voice))
            throw new ArgumentException("Voice is required.", nameof(request));

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var options = new AudioSpeechRequest
        {
            Model = request.Model,
            Input = request.Text,
            Voice = request.Voice,
            Instructions = request.Instructions,
            Speed = request.Speed,
            ResponseFormat = request.OutputFormat ?? GetString(metadata, "response_format", "responseFormat")
        };

        var (audio, mimeType) = await OpenAISpeechRequestAsync(options, cancellationToken);
        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = Convert.ToBase64String(audio),
                MimeType = mimeType,
                Format = options.ResponseFormat ?? "mp3"
            },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new
            {
                response_format = options.ResponseFormat,
                mime_type = mimeType
            }),
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }


    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ApplyAuthHeader();

        return _client.OpenAICompatibleSpeechRequestAsync(
            options,
            SpeechEndpoint,
            cancellationToken);
    }

    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ApplyAuthHeader();

        return this.SpeechStreamingAsync(options, cancellationToken);
    }

}
