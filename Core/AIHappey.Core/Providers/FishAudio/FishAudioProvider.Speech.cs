using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
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

        var payload = BuildFishSpeechPayload(request);
        format = (string)payload["format"]!;

        using var httpRequest = FishRequest(HttpMethod.Post, "v1/tts", payload);

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
            Request = new()
            {
                Body = payload
            },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new()
            {
                Timestamp = now,
                Headers = resp.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier()),
            }
        };
    }

    private static string NormalizeSpeechFormat(string? outputFormat)
    {
        var fmt = (outputFormat ?? string.Empty).Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(fmt) || fmt is "mp3" or "mpeg" or "audio/mpeg" or "audio/mp3")
            return "mp3";

        if (fmt is "wav" or "wave" or "audio/wav" or "audio/x-wav")
            return "wav";

        if (fmt is "pcm" or "audio/pcm")
            return "pcm";

        if (fmt is "opus" or "ogg" or "audio/opus" or "audio/ogg")
            return "opus";

        throw new NotSupportedException($"Fish Audio speech format '{outputFormat}' is not supported.");
    }

    private static string ResolveSpeechMimeType(string? contentType, string format)
    {
        if (format == "pcm") return "audio/pcm"; // signed 16-bit little-endian mono, not network-order audio/L16
        if (!string.IsNullOrWhiteSpace(contentType) && contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            return contentType;

        return format switch
        {
            "wav" => "audio/wav",
            "pcm" => "audio/pcm",
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

