using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
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

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        if (metadata is { ValueKind: not JsonValueKind.Object })
            throw new JsonException("YourVoic provider options must be a JSON object.");

        var format = NormalizeTtsFormat(request.OutputFormat ?? ReadMetadataValue<string>(metadata, "format"));
        var pitch = ReadMetadataValue<float?>(metadata, "pitch");

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

        if (pitch is not null)
            payload["pitch"] = pitch.Value;

        if (!string.IsNullOrWhiteSpace(format))
            payload["format"] = format;

        if (metadata is { } metadataObject)
        {
            foreach (var property in metadataObject.EnumerateObject())
            {
                if (property.Name.Equals("format", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("pitch", StringComparison.OrdinalIgnoreCase)
                    || payload.Keys.Any(key => key.Equals(property.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                payload[property.Name] = property.Value.Clone();
            }
        }

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
            Request = new()
            {
                Body = payload
            },
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new()
            {
                Timestamp = now,
                Headers = resp.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier()),
            }
        };
    }

    private static T? ReadMetadataValue<T>(JsonElement? metadata, string name)
    {
        if (metadata is not { ValueKind: JsonValueKind.Object } metadataObject)
            return default;

        JsonElement value = default;
        var found = false;
        foreach (var property in metadataObject.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                found = true;
            }
        }

        return found ? value.Deserialize<T>(JsonSerializerOptions.Web) : default;
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
