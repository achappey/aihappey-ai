using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model.Providers.Astica;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Astica;

public partial class AsticaProvider
{
    private static readonly JsonSerializerOptions SpeechJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var apiKey = ResolveRequiredApiKey();
        ApplyAuthHeader(apiKey);

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        var voiceId = ParseVoiceIdFromModel(request.Model);

        if (!string.IsNullOrWhiteSpace(request.Voice)
            && !string.Equals(request.Voice.Trim(), voiceId, StringComparison.OrdinalIgnoreCase))
            warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "ignored", feature = "language", reason = "language is voice-dependent in Astica" });

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });

        if (!string.IsNullOrWhiteSpace(request.OutputFormat)
            && !string.Equals(NormalizeFormat(request.OutputFormat), "wav", StringComparison.OrdinalIgnoreCase))
            warnings.Add(new { type = "ignored", feature = "outputFormat", reason = "astica non-streaming endpoint returns WAV in audio_b64" });

        var metadata = request.GetProviderMetadata<AsticaSpeechProviderMetadata>(GetIdentifier());

        var payload = new Dictionary<string, object?>
        {
            ["tkn"] = apiKey,
            ["text"] = request.Text,
            ["voice"] = voiceId,
            ["stream"] = false,
            ["timestamps"] = metadata?.Timestamps,
            ["prompt"] = string.IsNullOrWhiteSpace(metadata?.Prompt) ? null : metadata!.Prompt!.Trim()
        };

        var raw = await PostJsonAndReadAsync("api/tts", payload, cancellationToken);
        using var doc = JsonDocument.Parse(raw);

        var status = ReadString(doc.RootElement, "status");
        if (string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{ProviderName} TTS failed: {raw}");

        var audioBase64 = ReadString(doc.RootElement, "audio_b64");
        if (string.IsNullOrWhiteSpace(audioBase64))
            throw new InvalidOperationException($"{ProviderName} TTS response did not include audio_b64: {raw}");

        var responseVoice = ReadString(doc.RootElement, "voice") ?? voiceId;
        var engine = ReadString(doc.RootElement, "engine");
        var audioFormat = NormalizeFormat(ReadString(doc.RootElement, "audio_format")) ?? "wav";

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = audioBase64,
                MimeType = "audio/wav",
                Format = audioFormat
            },
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(new
                {
                    requestVoice = voiceId,
                    responseVoice,
                    engine,
                    timestamps = metadata?.Timestamps,
                    prompt = metadata?.Prompt,
                    response = JsonSerializer.Deserialize<JsonElement>(raw)
                })
            },
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = JsonSerializer.SerializeToElement(new
                {
                    response = JsonSerializer.Deserialize<JsonElement>(raw)
                })
            }
        };
    }

    private static string ParseVoiceIdFromModel(string model)
    {
        var trimmed = model.Trim();

        if (trimmed.StartsWith($"{ProviderId}/", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[(ProviderId.Length + 1)..];

        if (string.IsNullOrWhiteSpace(trimmed))
            throw new ArgumentException($"Model must be '{ProviderId}/[voiceId]'.", nameof(model));

        return trimmed;
    }

    private async Task<string> PostJsonAndReadAsync(string path, object payload, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, SpeechJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        req.Headers.Accept.ParseAdd(MediaTypeNames.Application.Json);

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} request '{path}' failed ({(int)resp.StatusCode}): {body}");

        return body;
    }

    private static string? NormalizeFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return null;

        var value = format.Trim().ToLowerInvariant();
        return value switch
        {
            "wave" => "wav",
            "mpeg" => "mp3",
            _ => value
        };
    }
}

