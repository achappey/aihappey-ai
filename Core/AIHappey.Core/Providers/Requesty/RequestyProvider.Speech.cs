using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.Requesty;

public partial class RequestyProvider
{
    private static readonly JsonSerializerOptions RequestySpeechJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
          AudioSpeechRequest options,
          CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return _client.OpenAICompatibleSpeechRequestAsync(
            options,
            cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<IAudioSpeechStreamEvent>
        OpenAISpeechStreamingAsync(
            AudioSpeechRequest options,
            CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return _client.OpenAICompatibleStreamingSpeechAsync(
            options,
            cancellationToken: cancellationToken);
    }

    private async Task<SpeechResponse> RequestySpeechRequest(
        SpeechRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        var responseFormat = request.OutputFormat?.Trim();
        if (string.IsNullOrWhiteSpace(responseFormat))
            responseFormat = "mp3";

        var voice = request.Voice?.Trim();
        if (string.IsNullOrWhiteSpace(voice))
            voice = "alloy";

        var speed = request.Speed;
        if (speed is { } s)
        {
            if (s < 0.25f)
            {
                warnings.Add(new { type = "unsupported", feature = "speed", details = "Minimum speed is 0.25. Using 0.25." });
                speed = 0.25f;
            }
            else if (s > 4f)
            {
                warnings.Add(new { type = "unsupported", feature = "speed", details = "Maximum speed is 4. Using 4." });
                speed = 4f;
            }
        }

        var streamFormat = TryGetRequestyProviderString(request.ProviderOptions, "stream_format", "streamFormat");
        if (!string.IsNullOrWhiteSpace(streamFormat))
            warnings.Add(new { type = "unsupported", feature = "stream_format", details = "SSE speech streaming is not supported by this adapter; raw audio response is requested." });

        var payload = new
        {
            model = request.Model,
            input = request.Text,
            voice,
            instructions = request.Instructions,
            response_format = responseFormat,
            speed
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, RequestySpeechJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/*"));

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException($"Requesty TTS failed ({(int)response.StatusCode}): {err}");
        }

        var mime = ResolveRequestySpeechMimeType(responseFormat, response.Content.Headers.ContentType?.MediaType);

        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = Convert.ToBase64String(bytes),
                MimeType = mime,
                Format = responseFormat
            },
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Request = new()
            {
                Body = payload
            },
            Response = new()
            {
                Timestamp = now,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private static string ResolveRequestySpeechMimeType(string? responseFormat, string? contentType)
    {
        var fmt = (responseFormat ?? string.Empty).Trim().ToLowerInvariant();
        return fmt switch
        {
            "mp3" => "audio/mpeg",
            "opus" => "audio/opus",
            "aac" => "audio/aac",
            "flac" => "audio/flac",
            "wav" => "audio/wav",
            "pcm" => "audio/pcm",
            _ => contentType ?? "application/octet-stream"
        };
    }
}
