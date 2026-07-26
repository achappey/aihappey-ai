using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.MCP.Media;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Aether;

public partial class AetherProvider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (audio, mimeType) = await OpenAISpeechRequestAsync(new AudioSpeechRequest
        {
            Model = request.Model,
            Input = request.Text,
            Voice = request.Voice,
            ResponseFormat = request.OutputFormat,
            Speed = request.Speed
        }, cancellationToken);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(audio),
                MimeType = mimeType,
                Format = ResolveAetherSpeechFormat(request.OutputFormat, mimeType)
            },
            Warnings = string.IsNullOrWhiteSpace(request.Instructions)
                ? []
                : [new { type = "unsupported", feature = "instructions" }],
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
        if (string.IsNullOrWhiteSpace(options.Model))
            throw new ArgumentException("'model' is a required field", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Input))
            throw new ArgumentException("'input' is a required field", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Voice))
            throw new ArgumentException("'voice' is a required field", nameof(options));

        ApplyAuthHeader();
        var payload = new Dictionary<string, object?>
        {
            ["model"] = options.Model,
            ["input"] = options.Input,
            ["voice"] = options.Voice,
            ["response_format"] = options.ResponseFormat,
            ["speed"] = options.Speed
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Aether speech failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(audio)}");

        return (audio, response.Content.Headers.ContentType?.MediaType ?? ResolveAetherSpeechMimeType(options.ResponseFormat));
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

    private static string ResolveAetherSpeechFormat(string? format, string mimeType)
        => string.IsNullOrWhiteSpace(format)
            ? mimeType switch { "audio/mpeg" => "mp3", "audio/opus" => "opus", "audio/aac" => "aac", "audio/flac" => "flac", _ => "mp3" }
            : format;

    private static string ResolveAetherSpeechMimeType(string? format)
        => format?.Trim().ToLowerInvariant() switch
        {
            "opus" => "audio/opus", "aac" => "audio/aac", "flac" => "audio/flac", _ => "audio/mpeg"
        };
}
