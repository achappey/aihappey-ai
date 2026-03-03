using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Speechactors;

public partial class SpeechactorsProvider
{
    private static readonly JsonSerializerOptions SpeechactorsSpeechJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private const int MinSpeakingRate = -100;
    private const int MaxSpeakingRate = 200;
    private const int MinPitch = -50;
    private const int MaxPitch = 50;

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

        var normalizedModel = NormalizeSpeechactorsModel(request.Model);
        var (vid, locale, style) = ParseSpeechactorsTtsModel(normalizedModel);

        if (!string.IsNullOrWhiteSpace(request.Voice))
            warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "ignored", feature = "language", reason = "language/locale is derived from model id" });

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        if (request.Speed is not null)
            warnings.Add(new { type = "ignored", feature = "speed", reason = "use providerOptions.speechactors.speakingRate instead" });

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());

        int? speakingRate = null;
        if (TryGetIntPropertyIgnoreCase(metadata, "speakingRate", out var speakingRateCandidate))
        {
            if (speakingRateCandidate is >= MinSpeakingRate and <= MaxSpeakingRate)
                speakingRate = speakingRateCandidate;
            else
                warnings.Add(new { type = "ignored", feature = "speakingRate", reason = $"out of range [{MinSpeakingRate},{MaxSpeakingRate}]" });
        }

        int? pitch = null;
        if (TryGetIntPropertyIgnoreCase(metadata, "pitch", out var pitchCandidate))
        {
            if (pitchCandidate is >= MinPitch and <= MaxPitch)
                pitch = pitchCandidate;
            else
                warnings.Add(new { type = "ignored", feature = "pitch", reason = $"out of range [{MinPitch},{MaxPitch}]" });
        }

        var payload = new Dictionary<string, object?>
        {
            ["locale"] = locale,
            ["vid"] = vid,
            ["text"] = request.Text,
            ["style"] = style,
            ["speakingRate"] = speakingRate,
            ["pitch"] = pitch
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/generate")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, SpeechactorsSpeechJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/*"));

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{nameof(Speechactors)} TTS failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        var mimeType = ResolveSpeechactorsMimeType(response.Content.Headers.ContentType?.MediaType);
        var format = ResolveSpeechactorsFormat(mimeType);

        var providerMeta = new
        {
            endpoint = "v1/generate",
            locale,
            vid,
            style,
            speakingRate,
            pitch,
            status = (int)response.StatusCode,
            contentType = response.Content.Headers.ContentType?.MediaType,
            bytes = bytes.Length
        };

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(bytes),
                MimeType = mimeType,
                Format = format
            },
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(providerMeta, JsonSerializerOptions.Web)
            },
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = JsonSerializer.SerializeToElement(providerMeta, JsonSerializerOptions.Web)
            }
        };
    }

    private static string NormalizeSpeechactorsModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(model));

        var trimmed = model.Trim();
        var prefix = "speechactors/";
        return trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed.SplitModelId().Model
            : trimmed;
    }

    private static (string Vid, string Locale, string? Style) ParseSpeechactorsTtsModel(string model)
    {
        if (!model.StartsWith(SpeechactorsTtsModelPrefix, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"{nameof(Speechactors)} speech model '{model}' is not supported. Expected '{SpeechactorsTtsModelPrefix}[vid]/[locale]' with optional '/style/[style]'.");

        var tail = model[SpeechactorsTtsModelPrefix.Length..].Trim();
        var parts = tail.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length < 2)
            throw new ArgumentException("Model must include both voice id and locale after 'tts/'.", nameof(model));

        var vid = parts[0];
        var locale = parts[1];
        string? style = null;

        if (parts.Length == 4 && string.Equals(parts[2], "style", StringComparison.OrdinalIgnoreCase))
            style = parts[3];
        else if (parts.Length != 2)
            throw new ArgumentException("Model must match 'tts/{vid}/{locale}' or 'tts/{vid}/{locale}/style/{style}'.", nameof(model));

        if (string.IsNullOrWhiteSpace(vid) || string.IsNullOrWhiteSpace(locale))
            throw new ArgumentException("Model must include non-empty voice id and locale.", nameof(model));

        return (vid.Trim(), locale.Trim(), string.IsNullOrWhiteSpace(style) ? null : style.Trim());
    }

    private static bool TryGetIntPropertyIgnoreCase(JsonElement metadata, string propertyName, out int value)
    {
        value = default;

        if (metadata.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var prop in metadata.EnumerateObject())
        {
            if (!string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out value))
                return true;

            if (prop.Value.ValueKind == JsonValueKind.String
                && int.TryParse(prop.Value.GetString(), out value))
                return true;

            return false;
        }

        return false;
    }

    private static string ResolveSpeechactorsMimeType(string? contentType)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
            return contentType;

        return "audio/mpeg";
    }

    private static string ResolveSpeechactorsFormat(string mimeType)
    {
        var normalized = (mimeType ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "audio/mpeg" or "audio/mp3" => "mp3",
            "audio/wav" or "audio/x-wav" => "wav",
            "audio/ogg" => "ogg",
            "audio/flac" => "flac",
            "audio/aac" => "aac",
            _ => "mp3"
        };
    }
}

