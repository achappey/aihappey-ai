using System.Globalization;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Deepgram;

public sealed partial class DeepgramProvider
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

        // Deepgram TTS does not accept these in the REST API request shape we support.
        if (!string.IsNullOrWhiteSpace(request.Voice))
            warnings.Add(new { type = "unsupported", feature = "voice" });
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (request.Speed is { } speed)
        {
            if (request.Model.StartsWith("aura-2-", StringComparison.OrdinalIgnoreCase))
            {
                var speedStep = speed * 20;
                if (!double.IsFinite(speed) || speed is < 0.7 or > 1.5 || Math.Abs(speedStep - Math.Round(speedStep)) > 0.0001)
                    throw new ArgumentOutOfRangeException(nameof(request.Speed), "Deepgram Aura-2 speed must be between 0.7 and 1.5 in 0.05 increments.");
            }
            else
            {
                warnings.Add(new { type = "unsupported", feature = "speed" });
            }
        }
        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        if (metadata.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined))
            throw new JsonException("Deepgram speech provider options must be a JSON object.");

        // Default Deepgram output: mp3 @ 22050 Hz (not configurable)
        var (encodingFromFormat, containerFromFormat) = MapOutputFormatToDeepgram(request.OutputFormat);

        var encoding = (GetMetadataValue<string>(metadata, "encoding") ?? encodingFromFormat ?? "mp3").Trim();
        var container = (GetMetadataValue<string>(metadata, "container") ?? containerFromFormat)?.Trim();
        var sampleRate = GetMetadataValue<int?>(metadata, "sample_rate");
        var bitRate = GetMetadataValue<int?>(metadata, "bit_rate");
        var mipOptOut = GetMetadataValue<bool?>(metadata, "mip_opt_out");
        var callback = GetMetadataValue<string>(metadata, "callback");
        var callbackMethod = GetMetadataValue<string>(metadata, "callback_method");

        // Build query string.
        var query = new List<string>
        {
            $"model={Uri.EscapeDataString(request.Model)}",
        };

        if (request.Speed is { } speechSpeed
            && request.Model.StartsWith("aura-2-", StringComparison.OrdinalIgnoreCase))
            query.Add($"speed={speechSpeed.ToString("0.##", CultureInfo.InvariantCulture)}");

        if (!string.IsNullOrWhiteSpace(encoding))
            query.Add($"encoding={Uri.EscapeDataString(encoding)}");
        if (!string.IsNullOrWhiteSpace(container))
            query.Add($"container={Uri.EscapeDataString(container!)}");
        if (sampleRate is not null)
            query.Add($"sample_rate={sampleRate.Value.ToString(CultureInfo.InvariantCulture)}");
        if (bitRate is not null)
            query.Add($"bit_rate={bitRate.Value.ToString(CultureInfo.InvariantCulture)}");
        if (mipOptOut is not null)
            query.Add($"mip_opt_out={mipOptOut.Value.ToString().ToLowerInvariant()}");
        if (!string.IsNullOrWhiteSpace(callback))
            query.Add($"callback={Uri.EscapeDataString(callback)}");
        if (!string.IsNullOrWhiteSpace(callbackMethod))
            query.Add($"callback_method={Uri.EscapeDataString(callbackMethod)}");

        if (metadata.ValueKind == JsonValueKind.Object
            && TryGetMetadataProperty(metadata, "tag", out var tag))
        {
            // Deepgram accepts: tag=<string> OR tag=<string>&tag=<string>...
            if (tag.ValueKind == JsonValueKind.String)
            {
                var tagValue = tag.GetString();
                if (!string.IsNullOrWhiteSpace(tagValue))
                    query.Add($"tag={Uri.EscapeDataString(tagValue)}");
            }
            else if (tag.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in tag.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.String) continue;
                    var tagValue = el.GetString();
                    if (!string.IsNullOrWhiteSpace(tagValue))
                        query.Add($"tag={Uri.EscapeDataString(tagValue)}");
                }
            }
        }

        if (metadata.ValueKind == JsonValueKind.Object)
        {
            var knownProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "encoding", "container", "sample_rate", "bit_rate", "mip_opt_out",
                "callback", "callback_method", "tag"
            };

            foreach (var property in metadata.EnumerateObject())
            {
                if (!knownProperties.Contains(property.Name))
                    AddMetadataQueryParameter(query, property.Name, property.Value);
            }
        }

        var url = "v1/speak" + (query.Count > 0 ? "?" + string.Join("&", query) : string.Empty);

        var body = new { text = request.Text };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, SpeechJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        // Avoid mutating shared HttpClient headers.
        httpRequest.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("audio/*"));

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException($"Deepgram TTS failed ({(int)resp.StatusCode}): {err}");
        }

        var contentType = resp.Content.Headers.ContentType?.MediaType;
        var mime = ResolveSpeechMimeType(contentType, encoding, container, request.OutputFormat);

        var audio = Convert.ToBase64String(bytes);

        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = audio,
                Format = encoding,
                MimeType = mime
            },
            Warnings = warnings,
            Request = new()
            {
                Body = body
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

    private static T? GetMetadataValue<T>(JsonElement metadata, string propertyName)
    {
        if (metadata.ValueKind != JsonValueKind.Object
            || !TryGetMetadataProperty(metadata, propertyName, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return default;

        return JsonSerializer.Deserialize<T>(value.GetRawText());
    }

    private static bool TryGetMetadataProperty(JsonElement metadata, string propertyName, out JsonElement value)
    {
        value = default;
        var found = false;
        foreach (var property in metadata.EnumerateObject())
        {
            if (!property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                continue;

            value = property.Value;
            found = true;
        }

        return found;
    }

    private static void AddMetadataQueryParameter(List<string> query, string name, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                AddMetadataQueryValue(query, name, item);
            return;
        }

        AddMetadataQueryValue(query, name, value);
    }

    private static void AddMetadataQueryValue(List<string> query, string name, JsonElement value)
    {
        var serializedValue = value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.GetRawText();

        query.Add($"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(serializedValue)}");
    }


    private static (string? Encoding, string? Container) MapOutputFormatToDeepgram(string? outputFormat)
    {
        var fmt = (outputFormat ?? string.Empty).Trim().ToLowerInvariant();

        // Default: mp3
        if (string.IsNullOrWhiteSpace(fmt) || fmt is "mp3" or "mpeg")
            return ("mp3", null);

        if (fmt is "wav" or "wave")
            return ("linear16", "wav");

        if (fmt is "ogg")
            return ("opus", "ogg");

        if (fmt is "opus")
            return ("opus", "ogg");

        if (fmt is "aac")
            return ("aac", null);

        if (fmt is "flac")
            return ("flac", null);

        // Unknown: fall back to mp3.
        return ("mp3", null);
    }

    private static string ResolveSpeechMimeType(string? contentType, string encoding, string? container, string? outputFormat)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
            return contentType;

        var fmt = (outputFormat ?? string.Empty).Trim().ToLowerInvariant();
        if (fmt is "mp3" or "mpeg")
            return "audio/mpeg";
        if (fmt is "wav" or "wave")
            return "audio/wav";
        if (fmt is "ogg" or "opus")
            return "audio/ogg";
        if (fmt is "aac")
            return "audio/aac";
        if (fmt is "flac")
            return "audio/flac";

        var enc = (encoding ?? string.Empty).Trim().ToLowerInvariant();
        return enc switch
        {
            "mp3" => "audio/mpeg",
            "linear16" => (container ?? string.Empty).Equals("wav", StringComparison.OrdinalIgnoreCase)
                ? "audio/wav"
                : "audio/L16",
            "opus" => "audio/ogg",
            "aac" => "audio/aac",
            "flac" => "audio/flac",
            _ => "application/octet-stream"
        };
    }
}
