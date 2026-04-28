using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.MCP.Media;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.xAI;

public partial class XAIProvider
{
    private static readonly HashSet<string> XAISttRawFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "pcm",
        "mulaw",
        "alaw"
    };

    private static readonly HashSet<string> XAISttSupportedSampleRates = new(StringComparer.OrdinalIgnoreCase)
    {
        "8000",
        "16000",
        "22050",
        "24000",
        "44100",
        "48000"
    };

    private async Task<TranscriptionResponse> TranscriptionRequestInternal(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        var now = DateTime.UtcNow;
        var providerOptions = request.GetProviderMetadata<JsonElement>(GetIdentifier());

        ValidateXAISttOptions(providerOptions);

        var hasUrl = TryGetString(providerOptions, "url") is not null;
        var hasAudio = request.Audio is not null;

        if (!hasUrl && !hasAudio)
            throw new ArgumentException("Either Audio or providerOptions.xai.url is required for xAI transcription.", nameof(request));

        if (hasUrl && hasAudio)
            throw new ArgumentException("Provide either Audio or providerOptions.xai.url for xAI transcription, not both.", nameof(request));

        using var form = new MultipartFormDataContent();

        AddXAISttProviderOptions(form, providerOptions);

        if (hasAudio)
        {
            if (string.IsNullOrWhiteSpace(request.MediaType))
                throw new ArgumentException("MediaType is required when Audio is provided.", nameof(request));

            var bytes = DecodeAudioBytes(request);
            var fileName = "audio" + request.MediaType.GetAudioExtension();
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);

            // xAI requires the file part to be the final multipart field.
            form.Add(file, "file", fileName);
        }

        using var response = await _client.PostAsync("v1/stt", form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} transcription failed ({(int)response.StatusCode}): {raw}");

        return ConvertXAISttResponse(raw, request.Model, now, providerOptions);
    }

    private static void AddXAISttProviderOptions(MultipartFormDataContent form, JsonElement providerOptions)
    {
        if (providerOptions.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in providerOptions.EnumerateObject())
        {
            if (string.Equals(property.Name, "file", StringComparison.OrdinalIgnoreCase))
                continue;

            if (TryConvertFormScalar(property.Value, out var value))
                form.Add(new StringContent(value), property.Name);
        }
    }

    private static bool TryConvertFormScalar(JsonElement value, out string scalar)
    {
        scalar = string.Empty;

        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                scalar = value.GetString() ?? string.Empty;
                return true;
            case JsonValueKind.Number:
                scalar = value.GetRawText();
                return true;
            case JsonValueKind.True:
                scalar = "true";
                return true;
            case JsonValueKind.False:
                scalar = "false";
                return true;
            default:
                return false;
        }
    }

    private static byte[] DecodeAudioBytes(TranscriptionRequest request)
    {
        var audioString = request.Audio switch
        {
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
            _ => request.Audio?.ToString()
        };

        if (string.IsNullOrWhiteSpace(audioString))
            throw new ArgumentException("Audio is required.", nameof(request));

        if (MediaContentHelpers.TryParseDataUrl(audioString, out _, out var parsedBase64))
            audioString = parsedBase64;

        try
        {
            return Convert.FromBase64String(audioString);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Audio must be base64 or a data-url containing base64.", ex);
        }
    }

    private static void ValidateXAISttOptions(JsonElement providerOptions)
    {
        var audioFormat = TryGetString(providerOptions, "audio_format");
        var sampleRate = TryGetString(providerOptions, "sample_rate") ?? TryGetString(providerOptions, "sample_rate_hertz");

        if (!string.IsNullOrWhiteSpace(audioFormat)
            && XAISttRawFormats.Contains(audioFormat)
            && string.IsNullOrWhiteSpace(sampleRate))
        {
            throw new ArgumentException("providerOptions.xai.sample_rate or providerOptions.xai.sample_rate_hertz is required for raw xAI STT audio formats.");
        }

        if (!string.IsNullOrWhiteSpace(sampleRate) && !XAISttSupportedSampleRates.Contains(sampleRate))
            throw new ArgumentOutOfRangeException(nameof(providerOptions), sampleRate, "xAI STT sample rate must be one of 8000, 16000, 22050, 24000, 44100, or 48000.");

        var multichannel = TryGetBool(providerOptions, "multichannel") == true;
        var channels = TryGetInt(providerOptions, "channels");

        if (channels is < 2 or > 8)
            throw new ArgumentOutOfRangeException(nameof(providerOptions), channels, "xAI STT channels must be between 2 and 8.");

        if (multichannel
            && !string.IsNullOrWhiteSpace(audioFormat)
            && XAISttRawFormats.Contains(audioFormat)
            && channels is null)
        {
            throw new ArgumentException("providerOptions.xai.channels is required for multichannel raw xAI STT audio.");
        }
    }

    private static TranscriptionResponse ConvertXAISttResponse(
        string raw,
        string? requestModel,
        DateTime timestamp,
        JsonElement providerOptions)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var segments = ExtractXAISttSegments(root);
        var text = TryGetString(root, "text") ?? string.Join(" ", segments.Select(segment => segment.Text));
        var language = TryGetString(root, "language");
        var duration = TryGetFloat(root, "duration");

        var metadata = new Dictionary<string, object?>
        {
            ["endpoint"] = "v1/stt",
            ["request"] = providerOptions.ValueKind == JsonValueKind.Object
                ? JsonSerializer.Deserialize<object>(providerOptions.GetRawText(), JsonSerializerOptions.Web)
                : null,
            ["response"] = JsonSerializer.Deserialize<object>(raw, JsonSerializerOptions.Web)
        };

        return new TranscriptionResponse
        {
            Text = text,
            Language = string.IsNullOrWhiteSpace(language) ? null : language,
            DurationInSeconds = duration,
            Segments = segments,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [XAIRequestExtensions.XAIIdentifier] = JsonSerializer.SerializeToElement(metadata, JsonSerializerOptions.Web)
            },
            Response = new ResponseData
            {
                Timestamp = timestamp,
                ModelId = string.IsNullOrWhiteSpace(requestModel) ? "stt" : requestModel,
                Body = raw
            }
        };
    }

    private static List<TranscriptionSegment> ExtractXAISttSegments(JsonElement root)
    {
        var segments = new List<TranscriptionSegment>();

        if (root.TryGetProperty("words", out var wordsEl) && wordsEl.ValueKind == JsonValueKind.Array)
            AddWordSegments(segments, wordsEl, channelPrefix: null);

        if (root.TryGetProperty("channels", out var channelsEl) && channelsEl.ValueKind == JsonValueKind.Array)
        {
            var channelIndex = 0;
            foreach (var channel in channelsEl.EnumerateArray())
            {
                channelIndex++;
                if (channel.ValueKind != JsonValueKind.Object)
                    continue;

                var channelPrefix = TryGetString(channel, "channel") ?? channelIndex.ToString();
                if (channel.TryGetProperty("words", out var channelWords) && channelWords.ValueKind == JsonValueKind.Array)
                    AddWordSegments(segments, channelWords, channelPrefix);
            }
        }

        return [.. segments.OrderBy(segment => segment.StartSecond).ThenBy(segment => segment.EndSecond)];
    }

    private static void AddWordSegments(List<TranscriptionSegment> segments, JsonElement wordsEl, string? channelPrefix)
    {
        foreach (var word in wordsEl.EnumerateArray())
        {
            if (word.ValueKind != JsonValueKind.Object)
                continue;

            var text = TryGetString(word, "text", "word");
            var start = TryGetFloat(word, "start");
            var end = TryGetFloat(word, "end");

            if (string.IsNullOrWhiteSpace(text) || start is null || end is null)
                continue;

            var speaker = TryGetString(word, "speaker");
            if (!string.IsNullOrWhiteSpace(speaker))
                text = $"Speaker {speaker}: {text}";

            if (!string.IsNullOrWhiteSpace(channelPrefix))
                text = $"Channel {channelPrefix}: {text}";

            segments.Add(new TranscriptionSegment
            {
                Text = text,
                StartSecond = start.Value,
                EndSecond = end.Value < start.Value ? start.Value : end.Value
            });
        }
    }

    private static string? TryGetString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.String)
                return value.GetString();

            if (value.ValueKind == JsonValueKind.Number)
                return value.GetRawText();

            if (value.ValueKind == JsonValueKind.True)
                return "true";

            if (value.ValueKind == JsonValueKind.False)
                return "false";
        }

        return null;
    }

    private static float? TryGetFloat(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.Number)
                return (float)value.GetDouble();

            if (value.ValueKind == JsonValueKind.String
                && float.TryParse(value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }

        return null;
    }

    private static int? TryGetInt(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                return number;

            if (value.ValueKind == JsonValueKind.String
                && int.TryParse(value.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }

        return null;
    }

    private static bool? TryGetBool(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.True)
                return true;

            if (value.ValueKind == JsonValueKind.False)
                return false;

            if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed))
                return parsed;
        }

        return null;
    }
}
