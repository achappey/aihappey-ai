using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model.Providers.APIpie;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.APIpie;

public partial class APIpieProvider
{
    private static readonly JsonSerializerOptions SpeechSettings = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(
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

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        var metadata = request.GetProviderMetadata<APIpieSpeechProviderMetadata>(GetIdentifier());

        var provider = metadata?.Provider?.Trim();
        if (string.IsNullOrWhiteSpace(provider))
            provider = "openai";

        var voice = request.Voice?.Trim();
        if (string.IsNullOrWhiteSpace(voice))
            voice = metadata?.Voice?.Trim();
        if (string.IsNullOrWhiteSpace(voice))
            voice = "alloy";

        var responseFormat = request.OutputFormat?.Trim();
        if (string.IsNullOrWhiteSpace(responseFormat))
            responseFormat = metadata?.ResponseFormat?.Trim();
        if (string.IsNullOrWhiteSpace(responseFormat))
            responseFormat = "mp3";

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

        var payload = new
        {
            provider,
            model = request.Model,
            input = request.Text,
            voice,
            voiceSettings = metadata?.VoiceSettings,
            responseFormat,
            speed,
            stream = false
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, SpeechSettings),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/*"));

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException($"APIpie TTS failed ({(int)resp.StatusCode}): {err}");
        }

        var mime = ResolveSpeechMimeType(responseFormat, resp.Content.Headers.ContentType?.MediaType);
        var base64 = Convert.ToBase64String(bytes);

        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = base64,
                MimeType = mime,
                Format = responseFormat
            },
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model
            }
        };
    }

    private static string ResolveSpeechMimeType(string? responseFormat, string? contentType)
    {
        var fmt = (responseFormat ?? string.Empty).Trim().ToLowerInvariant();
        return fmt switch
        {
            "mp3" => "audio/mpeg",
            "opus" => "audio/opus",
            "aac" => "audio/aac",
            "flac" => "audio/flac",
            "wav" => "audio/wav",
            _ => contentType ?? "application/octet-stream"
        };
    }
}

