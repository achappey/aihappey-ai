using System.Net.Mime;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Azerion;

public partial class AzerionProvider
{
    private static readonly JsonSerializerOptions SpeechJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        var voice = request.Voice?.Trim();
        if (string.IsNullOrWhiteSpace(voice))
            throw new ArgumentException("Voice is required.", nameof(request));

        var responseFormat = request.OutputFormat?.Trim().ToLowerInvariant() ?? "mp3";
        var speed = request.Speed;

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model.Trim(),
            ["input"] = request.Text,
            ["voice"] = voice,
            ["response_format"] = responseFormat,
            ["speed"] = speed
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, SpeechJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/*"));

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException(
                $"Azerion TTS failed ({(int)resp.StatusCode}): {err}"
            );
        }

        var mime = ResolveSpeechMimeType(responseFormat, resp.Content.Headers.ContentType?.MediaType);
        var audio = Convert.ToBase64String(bytes);

        return new SpeechResponse
        {
            Audio = new()
            {
                MimeType = mime,
                Base64 = audio,
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
            "pcm" => contentType ?? "application/octet-stream",
            _ => contentType ?? "application/octet-stream"
        };
    }
}
