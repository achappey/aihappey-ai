using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.NexosAI;

public partial class NexosAIProvider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var timestamp = DateTime.UtcNow;
        var (audio, mimeType) = await OpenAISpeechRequestAsync(new AudioSpeechRequest
        {
            Model = request.Model,
            Input = request.Text,
            Voice = request.Voice,
            ResponseFormat = request.OutputFormat,
            Speed = request.Speed
        }, cancellationToken);
        var warnings = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.Instructions)) warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (!string.IsNullOrWhiteSpace(request.Language)) warnings.Add(new { type = "unsupported", feature = "language" });

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(audio), MimeType = mimeType,
                Format = ResolveSpeechFormat(request.OutputFormat, mimeType)
            },
            Warnings = warnings,
            Response = new ResponseData { Timestamp = timestamp, ModelId = request.Model.ToModelId(GetIdentifier()) }
        };
    }

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Model)) throw new ArgumentException("'model' is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Input)) throw new ArgumentException("'input' is required.", nameof(options));
        if (options.Input.Length > 4096) throw new ArgumentException("'input' cannot exceed 4096 characters.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Voice)) throw new ArgumentException("'voice' is required.", nameof(options));
        if (options.Speed is < 0.25f or > 4f) throw new ArgumentOutOfRangeException(nameof(options), "Speed must be between 0.25 and 4.");

        ApplyAuthHeader();
        var format = string.IsNullOrWhiteSpace(options.ResponseFormat) ? "mp3" : options.ResponseFormat;
        var payload = new Dictionary<string, object?>
        {
            ["model"] = options.Model, ["input"] = options.Input, ["voice"] = options.Voice,
            ["response_format"] = format, ["speed"] = options.Speed
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"NexosAI speech failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(audio)}");
        return (audio, response.Content.Headers.ContentType?.MediaType ?? ResolveSpeechMimeType(format));
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (audio, _) = await OpenAISpeechRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        yield return new AudioSpeechStreamDelta { Audio = Convert.ToBase64String(audio) };
        yield return new AudioSpeechStreamDone();
    }

    private static string ResolveSpeechFormat(string? format, string mimeType)
        => !string.IsNullOrWhiteSpace(format) ? format : mimeType switch
        {
            "audio/opus" => "opus", "audio/aac" => "aac", "audio/flac" => "flac",
            "audio/wav" or "audio/x-wav" => "wav", "audio/pcm" => "pcm", _ => "mp3"
        };

    private static string ResolveSpeechMimeType(string? format) => format?.Trim().ToLowerInvariant() switch
    {
        "opus" => "audio/opus", "aac" => "audio/aac", "flac" => "audio/flac",
        "wav" => "audio/wav", "pcm" => "audio/pcm", _ => "audio/mpeg"
    };
}
