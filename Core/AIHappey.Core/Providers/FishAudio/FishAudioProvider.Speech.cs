using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.FishAudio;

public partial class FishAudioProvider
{
    private static readonly JsonSerializerOptions SpeechJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<SpeechResponse> SpeechRequestInternal(
        SpeechRequest request,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        var model = NormalizeModel(request.Model);
        var format = NormalizeSpeechFormat(request.OutputFormat);

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        var payload = new Dictionary<string, object?>
        {
            ["text"] = request.Text,
            ["format"] = format,
        };

        if (!string.IsNullOrWhiteSpace(request.Voice))
            payload["reference_id"] = request.Voice;

        if (request.Speed is not null)
        {
            payload["prosody"] = new
            {
                speed = request.Speed
            };
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/tts")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, SpeechJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        httpRequest.Headers.Add("model", model);
        httpRequest.Headers.Accept.Add(new("audio/*"));

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException($"FishAudio TTS failed ({(int)resp.StatusCode}): {err}");
        }

        var contentType = resp.Content.Headers.ContentType?.MediaType;
        var mimeType = ResolveSpeechMimeType(contentType, format);

        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = Convert.ToBase64String(bytes),
                Format = format,
                MimeType = mimeType
            },
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
            }
        };
    }

    private static string NormalizeSpeechFormat(string? outputFormat)
    {
        var fmt = (outputFormat ?? string.Empty).Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(fmt) || fmt is "mp3" or "mpeg")
            return "mp3";

        if (fmt is "wav" or "wave")
            return "wav";

        if (fmt is "pcm")
            return "pcm";

        if (fmt is "opus" or "ogg")
            return "opus";

        return "mp3";
    }

    private static string ResolveSpeechMimeType(string? contentType, string format)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
            return contentType;

        return format switch
        {
            "wav" => "audio/wav",
            "pcm" => "audio/L16",
            "opus" => "audio/ogg",
            _ => "audio/mpeg"
        };
    }

    private string NormalizeModel(string model)
    {
        var prefix = GetIdentifier() + "/";
        return model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? model.SplitModelId().Model
            : model.Trim();
    }
}

