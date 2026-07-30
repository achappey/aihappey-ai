using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.SudoRouter;

public partial class SudoRouterProvider
{
    private static readonly JsonSerializerOptions SudoRouterSpeechJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Voice))
            throw new ArgumentException("Voice is required.", nameof(request));

        var payload = CreateSudoRouterSpeechPayload(request);
        var result = await SendSudoRouterSpeechAsync(payload, request.OutputFormat, cancellationToken);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(result.Audio),
                MimeType = result.MimeType,
                Format = ResolveSudoRouterSpeechFormat(request.OutputFormat, result.MimeType)
            },
            Warnings = string.IsNullOrWhiteSpace(request.Language)
                ? []
                : [new { type = "unsupported", feature = "language" }],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Metadata),
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = result.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = result.Metadata
            },
            Request = new SpeechRequestItem { Body = payload }
        };
    }

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Model))
            throw new ArgumentException("Model is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Input))
            throw new ArgumentException("Input is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Voice))
            throw new ArgumentException("Voice is required.", nameof(options));

        var payload = JsonSerializer.SerializeToNode(options, SudoRouterSpeechJsonOptions)?.AsObject()
            ?? throw new InvalidOperationException("Could not serialize the SudoRouter speech request.");
        var result = await SendSudoRouterSpeechAsync(payload, options.ResponseFormat, cancellationToken);
        return (result.Audio, result.MimeType);
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // SudoRouter documents a binary response for this endpoint, not SSE audio events.
        var (audio, _) = await OpenAISpeechRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        yield return new AudioSpeechStreamDelta { Audio = Convert.ToBase64String(audio) };
        yield return new AudioSpeechStreamDone();
    }

    private async Task<SudoRouterSpeechResult> SendSudoRouterSpeechAsync(
        JsonObject payload,
        string? requestedFormat,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(payload.ToJsonString(SudoRouterSpeechJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = Encoding.UTF8.GetString(audio);
            throw new InvalidOperationException($"SudoRouter speech request failed ({(int)response.StatusCode}): {error}");
        }

        var mimeType = response.Content.Headers.ContentType?.MediaType
            ?? ResolveSudoRouterSpeechMimeType(requestedFormat);
        var headers = response.GetHeaders();
        var metadata = JsonSerializer.SerializeToElement(new
        {
            status_code = (int)response.StatusCode,
            content_type = mimeType,
            content_length = audio.LongLength,
            headers
        });
        return new SudoRouterSpeechResult(audio, mimeType, headers, metadata);
    }

    private JsonObject CreateSudoRouterSpeechPayload(SpeechRequest request)
    {
        var payload = GetSudoRouterProviderOptions(request.ProviderOptions);
        payload["model"] = request.Model;
        payload["input"] = request.Text;
        payload["voice"] = request.Voice;

        if (!string.IsNullOrWhiteSpace(request.OutputFormat))
            payload["response_format"] = request.OutputFormat;
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            payload["instructions"] = request.Instructions;
        if (request.Speed.HasValue)
            payload["speed"] = request.Speed.Value;

        return payload;
    }

    private static string ResolveSudoRouterSpeechFormat(string? format, string mimeType)
        => string.IsNullOrWhiteSpace(format)
            ? mimeType switch
            {
                "audio/mpeg" => "mp3",
                "audio/opus" => "opus",
                "audio/aac" => "aac",
                "audio/flac" => "flac",
                "audio/wav" => "wav",
                _ => "mp3"
            }
            : format;

    private static string ResolveSudoRouterSpeechMimeType(string? format)
        => format?.Trim().ToLowerInvariant() switch
        {
            "opus" => "audio/opus",
            "aac" => "audio/aac",
            "flac" => "audio/flac",
            "wav" => "audio/wav",
            "pcm" => "audio/pcm",
            _ => "audio/mpeg"
        };

    private sealed record SudoRouterSpeechResult(
        byte[] Audio,
        string MimeType,
        IDictionary<string, string> Headers,
        JsonElement Metadata);

}
