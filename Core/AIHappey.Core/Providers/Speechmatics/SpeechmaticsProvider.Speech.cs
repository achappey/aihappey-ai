using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Speechmatics;

public partial class SpeechmaticsProvider
{
    private const string TtsBaseUrl = "https://preview.tts.speechmatics.com";

    private static readonly JsonSerializerOptions SpeechJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

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

        var audioFormat = request.OutputFormat?.ToLowerInvariant().Contains("pcm",
            StringComparison.InvariantCultureIgnoreCase) == true ? "pcm_16000" : "wav_16000";
        var (modelId, modelVoice) = ParseSpeechModelAndVoice(request.Model);
        var voice = modelVoice ?? request.Voice?.Trim();

        if (string.IsNullOrWhiteSpace(voice))
            throw new ArgumentException("A Speechmatics voice is required. Use a '/voice/[voice]' model id or set Voice.", nameof(request));

        if (!string.IsNullOrWhiteSpace(modelVoice)
            && !string.IsNullOrWhiteSpace(request.Voice)
            && !string.Equals(modelVoice, request.Voice.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });
        }

        var payload = new Dictionary<string, object?>
        {
            ["text"] = request.Text,
        };

        var endpoint = $"{TtsBaseUrl}/generate/{Uri.EscapeDataString(voice)}?output_format={audioFormat}";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, SpeechJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Speechmatics TTS failed ({(int)resp.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        var mime = audioFormat.StartsWith("pcm", StringComparison.OrdinalIgnoreCase)
            ? "application/octet-stream"
            : "audio/wav";
        var base64 = Convert.ToBase64String(bytes);

        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = base64,
                MimeType = mime,
                Format = audioFormat.StartsWith("pcm", StringComparison.OrdinalIgnoreCase) ? "pcm" : "wav",
            },
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Request = new()
            {
                Body = payload
            },
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                Headers = resp.GetHeaders(),
                ModelId = modelId.ToModelId(GetIdentifier())
            }
        };
    }

    private (string Model, string? Voice) ParseSpeechModelAndVoice(string model)
    {
        var value = model.Trim();
        var providerPrefix = GetIdentifier() + "/";
        if (value.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            value = value[providerPrefix.Length..];

        const string voiceSegment = "/voice/";
        var voiceIndex = value.LastIndexOf(voiceSegment, StringComparison.OrdinalIgnoreCase);
        if (voiceIndex < 0)
            return (value, null);

        var voice = value[(voiceIndex + voiceSegment.Length)..].Trim();
        if (string.IsNullOrWhiteSpace(voice))
            throw new ArgumentException("Speech voice model ids must use '[model]/voice/[voice]'.", nameof(model));

        return (value[..voiceIndex].Trim(), voice);
    }

}

