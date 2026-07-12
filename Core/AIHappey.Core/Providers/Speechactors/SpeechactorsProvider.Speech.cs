using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
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

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        if (request.Speed is not null)
            warnings.Add(new { type = "ignored", feature = "speed", reason = "use providerOptions.speechactors.speakingRate instead" });

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var modelRef = ParseSpeechactorsTtsModel(request.Model);
        var primitiveVid = request.Voice;
        var primitiveLocale = request.Language;

        var metadataVid = metadata.TryGetString("vid");
        var metadataLocale = metadata.TryGetString("locale");

        var resolvedVid = modelRef.Vid ?? primitiveVid ?? metadataVid;
        var resolvedLocale = modelRef.Locale ?? primitiveLocale ?? metadataLocale;

        if (string.IsNullOrWhiteSpace(resolvedVid))
            throw new ArgumentException("Voice is required. Use model 'tts/{voiceId}', request.Voice, or providerOptions.speechactors.vid.", nameof(request));

        if (string.IsNullOrWhiteSpace(resolvedLocale))
            throw new ArgumentException("Locale is required. Use model 'tts/{voiceId}/{locale}', request.Language, or providerOptions.speechactors.locale.", nameof(request));

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
            ["text"] = request.Text,
            ["speakingRate"] = speakingRate,
            ["vid"] = resolvedVid,
            ["locale"] = resolvedLocale,
            ["pitch"] = pitch
        };

        string? style = metadata.TryGetString("style");
        if (!string.IsNullOrEmpty(style))
        {
            payload["style"] = style;
        }

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

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(bytes),
                MimeType = mimeType,
                Format = format
            },
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Request = new()
            {
                Body = payload
            },
            Response = new ResponseData
            {
                Timestamp = now,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }
    
    private static SpeechactorsTtsModelRef ParseSpeechactorsTtsModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(model));

        var parts = model.Split('/', StringSplitOptions.TrimEntries);

        if (parts.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Speechactors TTS model must not contain empty path segments.", nameof(model));

        if (!string.Equals(parts[0], "tts", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Speechactors TTS model must be 'tts', 'tts/{voiceId}', or 'tts/{voiceId}/{locale}'.", nameof(model));

        if (parts.Length > 3)
            throw new ArgumentException("Speechactors TTS model must be 'tts', 'tts/{voiceId}', or 'tts/{voiceId}/{locale}'.", nameof(model));

        return new SpeechactorsTtsModelRef(
            Vid: parts.Length >= 2 ? parts[1] : null,
            Locale: parts.Length >= 3 ? parts[2] : null
        );
    }

    private sealed record SpeechactorsTtsModelRef(string? Vid, string? Locale);

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

