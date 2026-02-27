using System.Text;
using System.Text.Json;
using AIHappey.Common.Model.Providers.UVoiceAI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.UVoiceAI;

public partial class UVoiceAIProvider
{
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
        var voiceId = ParseVoiceIdFromModel(request.Model);

        if (!string.IsNullOrWhiteSpace(request.Voice)
            && !string.Equals(request.Voice.Trim(), voiceId, StringComparison.OrdinalIgnoreCase))
            warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "ignored", feature = "language", reason = "language is derived from selected voice model" });

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        if (request.Text.Trim().Length < 5)
            throw new ArgumentException("Text must be at least 5 characters.", nameof(request));

        var metadata = request.GetProviderMetadata<UVoiceAISpeechProviderMetadata>(GetIdentifier());

        var outputFormat = NormalizeFormat(request.OutputFormat ?? metadata?.OutputFormat);
        if (outputFormat is not ("wav" or "mp3"))
            throw new ArgumentException("outputFormat must be 'wav' or 'mp3'.", nameof(request));

        var outputType = NormalizeOutputType(metadata?.OutputType);
        var autoBreak = metadata?.AutoBreak;
        var speed = request.Speed;
        var volume = metadata?.Volume;
        var pitch = metadata?.Pitch;
        var key = metadata?.Key;

        if (speed is < 0.5f or > 1.5f)
            throw new ArgumentOutOfRangeException(nameof(request.Speed), "UVoiceAI speed must be between 0.5 and 1.5.");
        if (volume is < 0.5f or > 1.5f)
            throw new ArgumentOutOfRangeException(nameof(metadata.Volume), "UVoiceAI volume must be between 0.5 and 1.5.");
        if (pitch is < 0.5f or > 1.5f)
            throw new ArgumentOutOfRangeException(nameof(metadata.Pitch), "UVoiceAI pitch must be between 0.5 and 1.5.");
        if (key is < -6 or > 6)
            throw new ArgumentOutOfRangeException(nameof(metadata.Key), "UVoiceAI key must be between -6 and 6.");

        var settings = new Dictionary<string, object?>
        {
            ["voiceID"] = voiceId,
            ["text"] = request.Text,
            ["outputFormat"] = outputFormat,
            ["outputType"] = outputType
        };

        if (autoBreak is not null)
            settings["autoBreak"] = autoBreak.Value;

        if (speed is not null)
            settings["speed"] = speed.Value;

        if (volume is not null)
            settings["volume"] = volume.Value;

        if (pitch is not null)
            settings["pitch"] = pitch.Value;

        if (key is not null)
            settings["key"] = key.Value;

        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["settings"] = settings
        });

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "generate")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} TTS failed ({(int)resp.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        string? audioUrl = null;
        byte[] audioBytes = bytes;
        var contentType = resp.Content.Headers.ContentType?.MediaType;

        if (string.Equals(outputType, "url", StringComparison.OrdinalIgnoreCase)
            || IsLikelyJson(contentType, bytes))
        {
            using var doc = JsonDocument.Parse(bytes);
            audioUrl = ReadAudioUrl(doc.RootElement)
                ?? throw new InvalidOperationException($"{ProviderName} returned URL output without an audio URL.");

            using var audioResp = await _client.GetAsync(audioUrl, cancellationToken);
            var audioUrlBytes = await audioResp.Content.ReadAsByteArrayAsync(cancellationToken);

            if (!audioResp.IsSuccessStatusCode)
                throw new InvalidOperationException($"{ProviderName} audio URL fetch failed ({(int)audioResp.StatusCode}): {Encoding.UTF8.GetString(audioUrlBytes)}");

            audioBytes = audioUrlBytes;
            contentType = audioResp.Content.Headers.ContentType?.MediaType;
        }

        var mimeType = ResolveSpeechMimeType(contentType, outputFormat);
        var responseFormat = ResolveSpeechFormat(outputFormat, mimeType);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(audioBytes),
                MimeType = mimeType,
                Format = responseFormat
            },
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(new
                {
                    voiceId,
                    outputType,
                    outputFormat,
                    audioUrl,
                    remainingCredits = TryReadHeader(resp, "X-Remaining-Credits")
                })
            },
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model
            }
        };
    }

    private static string ParseVoiceIdFromModel(string model)
    {
        var trimmed = model.Trim();

        if (trimmed.StartsWith("uvoiceai/", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed.Split('/', 2)[1];

        if (trimmed.StartsWith(UVoiceModelPrefix, StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[UVoiceModelPrefix.Length..];

        if (string.IsNullOrWhiteSpace(trimmed))
            throw new ArgumentException("Model must contain a voice id.", nameof(model));

        return trimmed;
    }

    private static string NormalizeFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return "wav";

        var value = format.Trim().ToLowerInvariant();
        return value switch
        {
            "mpeg" => "mp3",
            "wave" => "wav",
            _ => value
        };
    }

    private static string NormalizeOutputType(string? outputType)
    {
        if (string.IsNullOrWhiteSpace(outputType))
            return "binary";

        var value = outputType.Trim().ToLowerInvariant();
        return value == "url" ? "url" : "binary";
    }

    private static bool IsLikelyJson(string? contentType, byte[] bytes)
    {
        if (!string.IsNullOrWhiteSpace(contentType)
            && contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
            return true;

        var prefix = Encoding.UTF8.GetString([.. bytes.Take(64)]).TrimStart();
        return prefix.StartsWith("{", StringComparison.Ordinal) || prefix.StartsWith("[", StringComparison.Ordinal);
    }

    private static string? ReadAudioUrl(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.String)
            return root.GetString();

        if (root.ValueKind != JsonValueKind.Object)
            return null;

        var keys = new[] { "url", "audioUrl", "audio_url", "file", "fileUrl", "file_url", "result" };
        foreach (var key in keys)
        {
            if (!TryGetPropertyIgnoreCase(root, key, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.String)
            {
                var url = value.GetString();
                if (!string.IsNullOrWhiteSpace(url))
                    return url;
            }
        }

        return null;
    }

    private static string ResolveSpeechMimeType(string? contentType, string outputFormat)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
            return contentType;

        return outputFormat == "wav" ? "audio/wav" : "audio/mpeg";
    }

    private static string ResolveSpeechFormat(string outputFormat, string mimeType)
    {
        if (!string.IsNullOrWhiteSpace(outputFormat))
            return outputFormat;

        if (mimeType.Contains("wav", StringComparison.OrdinalIgnoreCase))
            return "wav";

        return "mp3";
    }

    private static string? TryReadHeader(HttpResponseMessage response, string headerName)
        => response.Headers.TryGetValues(headerName, out var values) ? values.FirstOrDefault() : null;
}

