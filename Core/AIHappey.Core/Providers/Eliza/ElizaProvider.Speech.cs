using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.Eliza;

public partial class ElizaProvider
{
    private const string ElizaDefaultSpeechModel = "eleven_multilingual_v2";

    private static readonly JsonSerializerOptions ElizaSpeechJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyVoiceApiKeyHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var model = NormalizeElizaVoiceModel(request.Model, ElizaDefaultSpeechModel);
        var voice = request.Voice
            ?? metadata.TryGetString("voiceId")
            ?? metadata.TryGetString("voice_id")
            ?? metadata.TryGetString("voice");

        if (string.IsNullOrWhiteSpace(voice))
            throw new ArgumentException("'voice' or providerOptions.eliza.voiceId is required.", nameof(request));

        if (!string.IsNullOrWhiteSpace(request.OutputFormat) &&
            !string.Equals(request.OutputFormat, "mp3", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "outputFormat",
                details = "Eliza TTS returns audio/mpeg. Requested outputFormat was ignored."
            });
        }

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        var payload = new Dictionary<string, object?>
        {
            ["text"] = request.Text,
            ["voiceId"] = voice,
            ["modelId"] = model,
            ["stability"] = TryGetElizaDouble(metadata, "stability"),
            ["similarity_boost"] = TryGetElizaDouble(metadata, "similarity_boost")
                ?? TryGetElizaDouble(metadata, "similarityBoost")
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/voice/tts")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, ElizaSpeechJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Eliza TTS failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        var contentType = response.Content.Headers.ContentType?.MediaType ?? "audio/mpeg";

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(bytes),
                MimeType = contentType,
                Format = ElizaAudioFormatFromMimeType(contentType)
            },
            Warnings = warnings,
            ProviderMetadata = BuildElizaProviderMetadata(new
            {
                endpoint = "v1/voice/tts",
                modelId = model,
                voiceId = voice,
                contentType,
                contentLength = bytes.LongLength
            }),
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = model
            }
        };
    }

    private static string NormalizeElizaVoiceModel(string? model, string fallback)
    {
        if (string.IsNullOrWhiteSpace(model))
            return fallback;

        var trimmed = model.Trim();
        var prefix = "eliza/";

        return trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[prefix.Length..]
            : trimmed;
    }

    private static string ElizaAudioFormatFromMimeType(string? mimeType)
    {
        return (mimeType ?? string.Empty).ToLowerInvariant() switch
        {
            "audio/mpeg" or "audio/mp3" => "mp3",
            "audio/wav" or "audio/x-wav" or "audio/wave" => "wav",
            "audio/ogg" => "ogg",
            "audio/webm" => "webm",
            "audio/aac" => "aac",
            "audio/flac" => "flac",
            _ => "mp3"
        };
    }

}
