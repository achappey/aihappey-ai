using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.MumeAI;

public partial class MumeAIProvider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var payload = MumePayload(GetMumeProviderOptions(request.ProviderOptions));
        payload["model"] = request.Model;
        payload["input"] = request.Text;
        payload["voice"] = request.Voice;
        if (!string.IsNullOrWhiteSpace(request.OutputFormat)) payload["response_format"] = request.OutputFormat;
        if (request.Speed.HasValue) payload["speed"] = request.Speed.Value;

        var result = await SendMumeSpeechAsync(payload, request.Model, request.Text, request.Voice, request.OutputFormat, cancellationToken);
        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(result.Audio),
                MimeType = result.MimeType,
                Format = ResolveMumeSpeechFormat(request.OutputFormat, result.MimeType)
            },
            Warnings = string.IsNullOrWhiteSpace(request.Instructions)
                ? []
                : [new { type = "unsupported", feature = "instructions" }],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new
            {
                generationId = Header(result.Headers, "X-Generation-Id"),
                mediaUrl = Header(result.Headers, "X-Media-Url")
            }),
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = result.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = options.Model,
            ["input"] = options.Input,
            ["voice"] = options.Voice,
            ["response_format"] = options.ResponseFormat,
            ["speed"] = options.Speed
        };
        var result = await SendMumeSpeechAsync(payload, options.Model, options.Input, options.Voice, options.ResponseFormat, cancellationToken);
        return (result.Audio, result.MimeType);
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (audio, _) = await OpenAISpeechRequestAsync(options, cancellationToken);
        yield return new AudioSpeechStreamDelta { Audio = Convert.ToBase64String(audio) };
        yield return new AudioSpeechStreamDone();
    }

    private async Task<(byte[] Audio, string MimeType, Dictionary<string, string> Headers)> SendMumeSpeechAsync(
        Dictionary<string, object?> payload,
        string model,
        string input,
        string? voice,
        string? format,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("Model is required.", nameof(model));
        if (string.IsNullOrWhiteSpace(input)) throw new ArgumentException("Input is required.", nameof(input));
        if (string.IsNullOrWhiteSpace(voice)) throw new ArgumentException("Voice is required.", nameof(voice));
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Mume AI speech failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(bytes)}");
        return (bytes, response.Content.Headers.ContentType?.MediaType ?? ResolveMumeSpeechMimeType(format), response.GetHeaders());
    }

    private static string? Header(IReadOnlyDictionary<string, string> headers, string name)
        => headers.TryGetValue(name, out var value) ? value : null;

    private static string ResolveMumeSpeechFormat(string? format, string mimeType)
        => !string.IsNullOrWhiteSpace(format) ? format : mimeType switch
        {
            "audio/wav" => "wav", "audio/opus" => "opus", "audio/aac" => "aac", "audio/flac" => "flac", _ => "mp3"
        };

    private static string ResolveMumeSpeechMimeType(string? format)
        => format?.ToLowerInvariant() switch
        {
            "wav" => "audio/wav", "opus" => "audio/opus", "aac" => "audio/aac", "flac" => "audio/flac", _ => "audio/mpeg"
        };

}
