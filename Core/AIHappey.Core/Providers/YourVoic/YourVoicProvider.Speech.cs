using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model.Providers.YourVoic;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.YourVoic;

public partial class YourVoicProvider
{
    private static readonly JsonSerializerOptions SpeechJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        var metadata = request.GetProviderMetadata<YourVoicSpeechProviderMetadata>(GetIdentifier());

        var format = NormalizeTtsFormat(request.OutputFormat ?? metadata?.Format);

        var payload = new Dictionary<string, object?>
        {
            ["text"] = request.Text,
            ["model"] = request.Model,
        };

        if (!string.IsNullOrWhiteSpace(request.Voice))
            payload["voice"] = request.Voice;

        if (!string.IsNullOrWhiteSpace(request.Language))
            payload["language"] = request.Language;

        if (request.Speed is not null)
            payload["speed"] = request.Speed.Value;

        if (metadata?.Pitch is not null)
            payload["pitch"] = metadata.Pitch.Value;

        if (!string.IsNullOrWhiteSpace(format))
            payload["format"] = format;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "tts/generate")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, SpeechJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"YourVoic TTS failed ({(int)resp.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        var mime = ResolveSpeechMimeType(resp.Content.Headers.ContentType?.MediaType, format);
        var responseFormat = ResolveSpeechFormat(format, mime);

        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = Convert.ToBase64String(bytes),
                MimeType = mime,
                Format = responseFormat,
            },
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model
            }
        };
    }

    private static string? NormalizeTtsFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return null;

        var fmt = format.Trim().ToLowerInvariant();
        if (fmt == "mpeg")
            return "mp3";
        if (fmt == "wave")
            return "wav";

        return fmt;
    }

    private static string ResolveSpeechMimeType(string? contentType, string? requestedFormat)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
            return contentType;

        return requestedFormat switch
        {
            "wav" => "audio/wav",
            "ogg" => "audio/ogg",
            "flac" => "audio/flac",
            "aac" => "audio/aac",
            _ => "audio/mpeg"
        };
    }

    private static string ResolveSpeechFormat(string? requestedFormat, string mimeType)
    {
        if (!string.IsNullOrWhiteSpace(requestedFormat))
            return requestedFormat;

        return mimeType switch
        {
            "audio/wav" => "wav",
            "audio/ogg" => "ogg",
            "audio/flac" => "flac",
            "audio/aac" => "aac",
            _ => "mp3"
        };
    }
}

