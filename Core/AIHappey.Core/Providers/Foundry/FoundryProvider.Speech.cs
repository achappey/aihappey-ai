using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.Foundry;

public partial class FoundryProvider
{
    private const string FoundrySpeechEndpoint = "openai/v1/audio/speech?api-version=preview";

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);

        var format = string.IsNullOrWhiteSpace(request.OutputFormat) ? "mp3" : request.OutputFormat;
        var options = new AudioSpeechRequest
        {
            Model = request.Model,
            Input = request.Text,
            Voice = string.IsNullOrWhiteSpace(request.Voice) ? "alloy" : request.Voice,
            ResponseFormat = format,
            Instructions = request.Instructions,
            Speed = request.Speed
        };

        ApplyAuthHeader();
        var now = DateTime.UtcNow;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, FoundrySpeechEndpoint)
        {
            Content = FoundryCreateSpeechContent(options)
        };
        using var response = await _client.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = System.Text.Encoding.UTF8.GetString(audio);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"Foundry speech request failed ({(int)response.StatusCode} {response.ReasonPhrase})."
                : $"Foundry speech request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {error}");
        }

        var mimeType = response.Content.Headers.ContentType?.MediaType
            ?? FoundryResolveAudioMimeType(format);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(audio),
                MimeType = mimeType,
                Format = format
            },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new
            {
                responseHeaders = response.GetHeaders(),
                contentType = mimeType,
                contentLength = audio.LongLength
            }),
            Request = new SpeechRequestItem { Body = options },
            Response = new ResponseData
            {
                Timestamp = now,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return _client.OpenAICompatibleSpeechRequestAsync(
            options,
            FoundrySpeechEndpoint,
            cancellationToken);
    }

    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return _client.OpenAICompatibleStreamingSpeechAsync(
            options,
            FoundrySpeechEndpoint,
            cancellationToken);
    }

    private static StringContent FoundryCreateSpeechContent(AudioSpeechRequest options)
        => new(
            System.Text.Json.JsonSerializer.Serialize(options),
            System.Text.Encoding.UTF8,
            "application/json");

    private static string FoundryResolveAudioMimeType(string? format)
        => format?.Trim().ToLowerInvariant() switch
        {
            "mp3" => "audio/mpeg",
            "opus" => "audio/opus",
            "aac" => "audio/aac",
            "flac" => "audio/flac",
            "wav" => "audio/wav",
            "pcm" => "audio/pcm",
            _ => "application/octet-stream"
        };
}
