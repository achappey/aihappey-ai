using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.NagaAI;

public partial class NagaAIProvider
{
    private const string NagaAISpeechEndpoint = "v1/audio/speech";
    private static readonly HashSet<string> NagaAISpeechReserved = new(
        ["model", "input", "voice", "speed", "instructions", "response_format", "responseFormat"],
        StringComparer.OrdinalIgnoreCase);

    public async Task<SpeechResponse> SpeechRequest(
        SpeechRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var voice = request.Voice ?? ReadNagaAIString(metadata, "voice");
        if (string.IsNullOrWhiteSpace(voice))
            throw new ArgumentException("Voice is required.", nameof(request));

        var options = new AudioSpeechRequest
        {
            Model = request.Model,
            Input = request.Text,
            Voice = voice,
            Speed = request.Speed ?? (float?)ReadNagaAIDouble(metadata, "speed"),
            Instructions = request.Instructions ?? ReadNagaAIString(metadata, "instructions"),
            ResponseFormat = request.OutputFormat
                ?? ReadNagaAIString(metadata, "response_format", "responseFormat"),
            AdditionalProperties = CopyNagaAIProperties(metadata, NagaAISpeechReserved)
        };

        var now = DateTime.UtcNow;
        var (audio, mimeType) = await OpenAISpeechRequestAsync(options, cancellationToken);
        var format = options.ResponseFormat ?? NagaAIAudioFormatFromMimeType(mimeType);
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
                response_format = format,
                mime_type = mimeType,
                content_length = audio.LongLength
            }),
            Request = new() { Body = options },
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = new { contentType = mimeType, contentLength = audio.LongLength }
            }
        };
    }

    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ApplyAuthHeader();
        return _client.OpenAICompatibleSpeechRequestAsync(
            options,
            NagaAISpeechEndpoint,
            cancellationToken);
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

    private static string NagaAIAudioFormatFromMimeType(string mimeType)
        => mimeType.Trim().ToLowerInvariant() switch
        {
            "audio/mpeg" => "mp3",
            "audio/opus" => "opus",
            "audio/aac" => "aac",
            "audio/flac" => "flac",
            "audio/wav" or "audio/x-wav" => "wav",
            "audio/pcm" => "pcm",
            _ => "mp3"
        };
}
