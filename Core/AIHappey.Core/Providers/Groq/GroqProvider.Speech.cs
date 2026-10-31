using System.Text;
using System.Text.Json;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.Groq;

public partial class GroqProvider
{
    public async Task<SpeechResponse> SpeechRequest(
          SpeechRequest request,
          CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var metadata =
            request.GetProviderMetadata<Dictionary<string, JsonElement>>(GetIdentifier());

        var voice =
            request.Voice
            ?? GetMetadataValue<string>(metadata, "voice")
            ?? "troy";

        var format =
            request.OutputFormat
            ?? GetMetadataValue<string>(metadata, "response_format")
            ?? "wav";

        var payload = metadata?
            .Where(pair => !IsMappedMetadataProperty(pair.Key))
            .ToDictionary(pair => pair.Key, pair => (object?)pair.Value)
            ?? [];

        payload["model"] = request.Model;
        payload["input"] = request.Text;
        payload["voice"] = voice;
        payload["response_format"] = format;

        if (request.Speed is not null)
            payload["speed"] = request.Speed.Value;

        using var content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json"
        );

        using var resp = await _client.PostAsync(
            "v1/audio/speech",
            content,
            cancellationToken
        );

        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException(
                $"Groq TTS failed ({(int)resp.StatusCode}): {err}"
            );
        }

        var mime = format.Trim().ToLowerInvariant() switch
        {
            "wav" => "audio/wav",
            "mp3" => "audio/mpeg",
            "flac" => "audio/flac",
            "mulaw" => "audio/basic",
            "ogg" => "audio/ogg",
            _ => "application/octet-stream"
        };

        var base64 = Convert
            .ToBase64String(bytes);

        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = base64,
                MimeType = mime,
                Format = format ?? "wav"
            },
            Warnings = [],
            ProviderMetadata = GetIdentifier()
                .CreatePrimitiveProviderMetadata(),
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                Headers = resp.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            },
            Request = new SpeechRequestItem
            {
                Body = payload
            }
        };
    }

    private static bool IsMappedMetadataProperty(string propertyName)
        => string.Equals(propertyName, "voice", StringComparison.OrdinalIgnoreCase)
            || string.Equals(propertyName, "response_format", StringComparison.OrdinalIgnoreCase);

    private static T? GetMetadataValue<T>(
        IReadOnlyDictionary<string, JsonElement>? metadata,
        string propertyName)
    {
        JsonElement? value = null;

        if (metadata is not null)
        {
            foreach (var property in metadata)
            {
                if (string.Equals(property.Key, propertyName, StringComparison.OrdinalIgnoreCase))
                    value = property.Value;
            }
        }

        return value is JsonElement element
            ? element.Deserialize<T>(JsonSerializerOptions.Web)
            : default;
    }

}
