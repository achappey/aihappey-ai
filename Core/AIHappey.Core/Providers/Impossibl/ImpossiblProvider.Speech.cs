using AIHappey.Core.AI;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Impossibl;

public partial class ImpossiblProvider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var options = new AudioSpeechRequest
        {
            Model = request.Model,
            Input = request.Text,
            Voice = request.Voice,
            ResponseFormat = request.OutputFormat,
            Speed = request.Speed,
            Instructions = request.Instructions,
            AdditionalProperties = request.ProviderOptions is not null
                && request.ProviderOptions.TryGetValue(GetIdentifier(), out var providerOptions)
                && providerOptions.ValueKind == JsonValueKind.Object
                    ? providerOptions.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone())
                    : null
        };
        var (audio, mimeType) = await OpenAISpeechRequestAsync(options, cancellationToken);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(audio),
                MimeType = mimeType,
                Format = ResolveImpossiblSpeechFormat(request.OutputFormat, mimeType)
            },
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Model)) throw new ArgumentException("'model' is a required field", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Input)) throw new ArgumentException("'input' is a required field", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Voice)) throw new ArgumentException("'voice' is a required field", nameof(options));

        var payload = options.AdditionalProperties is null
            ? new Dictionary<string, object?>()
            : options.AdditionalProperties.ToDictionary(x => x.Key, x => (object?)x.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        payload["model"] = options.Model;
        payload["input"] = options.Input;
        payload["voice"] = options.Voice;
        if (!string.IsNullOrWhiteSpace(options.ResponseFormat)) payload["response_format"] = options.ResponseFormat;
        if (options.Speed is not null) payload["speed"] = options.Speed;
        if (!string.IsNullOrWhiteSpace(options.Instructions)) payload["instructions"] = options.Instructions;

        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Impossibl speech failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(audio)}");

        return (audio, response.Content.Headers.ContentType?.MediaType ?? ResolveImpossiblSpeechMimeType(options.ResponseFormat));
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

    private static string ResolveImpossiblSpeechFormat(string? format, string mimeType)
        => string.IsNullOrWhiteSpace(format)
            ? mimeType switch { "audio/wav" or "audio/x-wav" => "wav", "audio/pcm" => "pcm", _ => "mp3" }
            : format;

    private static string ResolveImpossiblSpeechMimeType(string? format)
        => format?.Trim().ToLowerInvariant() switch
        {
            "wav" => "audio/wav",
            "pcm" => "audio/pcm",
            _ => "audio/mpeg"
        };
}
