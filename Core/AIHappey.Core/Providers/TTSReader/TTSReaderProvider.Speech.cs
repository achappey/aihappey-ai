using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Core.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.TTSReader;

public partial class TTSReaderProvider
{
    private static readonly JsonSerializerOptions SpeechJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
    
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        if (metadata.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Object)
            throw new JsonException("TTSReader provider options must be a JSON object.");

        var metadataVoice = ReadMetadataString(metadata, "voice");
        var metadataLanguage = ReadMetadataString(metadata, "language");
        var metadataRate = ReadMetadataSingle(metadata, "rate");
        var metadataQuality = ReadMetadataString(metadata, "quality");

        var voiceId = (request.Voice ?? metadataVoice)?.Trim();
        if (string.IsNullOrWhiteSpace(voiceId))
            throw new ArgumentException("Voice required", nameof(request));

        var language = (request.Language ?? metadataLanguage)?.Trim();
        var rate = request.Speed ?? metadataRate;
        if (string.IsNullOrWhiteSpace(language))
            throw new ArgumentException("Language required", nameof(request));

        var payload = GetUnknownMetadataProperties(metadata, "voice", "language", "rate", "quality");
        payload["text"] = request.Text;
        payload["voice"] = voiceId;
        payload["language"] = language;

        if (!string.IsNullOrWhiteSpace(metadataQuality))
            payload["quality"] = metadataQuality;

        if (rate is not null)
        {
            payload["rate"] = rate;
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "ttsSync")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, SpeechJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"TTSReader TTS failed ({(int)resp.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        var mime = "audio/mpeg";
        var base64 = Convert.ToBase64String(bytes);

        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = base64,
                MimeType = mime,
                Format = "mp3",
            },
            Warnings = [],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Request = new()
            {
                Body = payload
            },
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private static Dictionary<string, object?> GetUnknownMetadataProperties(JsonElement metadata, params string[] knownProperties)
    {
        var result = new Dictionary<string, object?>();
        if (metadata.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var property in metadata.EnumerateObject())
        {
            if (knownProperties.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                continue;

            result[property.Name] = property.Value.Clone();
        }

        return result;
    }

    private static string? ReadMetadataString(JsonElement metadata, string name)
    {
        if (metadata.ValueKind != JsonValueKind.Object
            || !TryGetMetadataProperty(metadata, name, out var value)
            || value.ValueKind == JsonValueKind.Null)
            return null;

        return value.Deserialize<string>(JsonSerializerOptions.Web);
    }

    private static float? ReadMetadataSingle(JsonElement metadata, string name)
    {
        if (metadata.ValueKind != JsonValueKind.Object
            || !TryGetMetadataProperty(metadata, name, out var value)
            || value.ValueKind == JsonValueKind.Null)
            return null;

        return value.Deserialize<float>(JsonSerializerOptions.Web);
    }

    private static bool TryGetMetadataProperty(JsonElement metadata, string name, out JsonElement value)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

        var found = false;
        value = default;
        foreach (var property in metadata.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                found = true;
            }
        }

        return found;
    }

}
