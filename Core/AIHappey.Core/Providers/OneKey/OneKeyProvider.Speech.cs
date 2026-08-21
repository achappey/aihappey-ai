using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.OneKey;

public partial class OneKeyProvider
{
    private const string OneKeySpeechEndpoint = "v1/audio/speech";
    private static readonly HashSet<string> OneKeySpeechReserved =
        new(["model", "input", "voice", "response_format", "instructions", "speed", "stream_format"], StringComparer.OrdinalIgnoreCase);

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text)) throw new ArgumentException("Text is required.", nameof(request));

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var additional = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (metadata.ValueKind == JsonValueKind.Object)
            foreach (var property in metadata.EnumerateObject())
                if (!OneKeySpeechReserved.Contains(property.Name)) additional[property.Name] = property.Value.Clone();

        var options = new AudioSpeechRequest
        {
            Model = request.Model,
            Input = request.Text,
            Voice = request.Voice ?? ReadOneKeySpeechString(metadata, "voice"),
            ResponseFormat = request.OutputFormat ?? ReadOneKeySpeechString(metadata, "response_format", "responseFormat"),
            Instructions = request.Instructions,
            Speed = request.Speed,
            AdditionalProperties = additional.Count == 0 ? null : additional
        };
        var (audio, mimeType) = await OpenAISpeechRequestAsync(options, cancellationToken);
        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(audio), MimeType = mimeType,
                Format = options.ResponseFormat ?? MimeTypeToOneKeyFormat(mimeType)
            },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new
            {
                response_format = options.ResponseFormat, mime_type = mimeType
            }),
            Response = new ResponseData { Timestamp = DateTime.UtcNow, ModelId = request.Model.ToModelId(GetIdentifier()) }
        };
    }

    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ApplyAuthHeader();
        return _client.OpenAICompatibleSpeechRequestAsync(options, OneKeySpeechEndpoint, cancellationToken);
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (audio, _) = await OpenAISpeechRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        yield return new AudioSpeechStreamDelta { Audio = Convert.ToBase64String(audio) };
        yield return new AudioSpeechStreamDone();
    }

    private static string? ReadOneKeySpeechString(JsonElement metadata, params string[] names)
    {
        if (metadata.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
            if (metadata.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String) return value.GetString();
        return null;
    }
    private static string MimeTypeToOneKeyFormat(string mimeType) => mimeType.ToLowerInvariant() switch
    {
        "audio/mpeg" => "mp3", "audio/opus" => "opus", "audio/aac" => "aac", "audio/flac" => "flac",
        "audio/wav" or "audio/x-wav" => "wav", "audio/pcm" => "pcm", _ => "mp3"
    };
}
