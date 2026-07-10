using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.Extensions;
using AIHappey.Core.Providers.OpenAI;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.DeepInfra;

public sealed partial class DeepInfraProvider
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

        ApplyAuthHeader();

        if (request.Model.StartsWith("hexgrad/"))
        {
            return await HexgradSpeechRequest(request, cancellationToken);
        }

        if (request.Model.StartsWith("ResembleAI/"))
        {
            return await ResembleAISpeechRequest(request, cancellationToken);
        }

        if (request.Model.StartsWith("sesame/"))
        {
            return await SesameSpeechRequest(request, cancellationToken);
        }

        if (request.Model.StartsWith("canopylabs/"))
        {
            return await CanopyLabsSpeechRequest(request, cancellationToken);
        }

        if (request.Model.StartsWith("Zyphra/"))
        {
            return await ZyphraSpeechRequest(request, cancellationToken);
        }

        throw new NotImplementedException(request.Model);
    }

    private static (string Base64, string MimeType, string Format) ParseAudioDataUrl(
    string value,
    string? fallbackFormat,
    string? fallbackMimeType)
    {
        const string marker = ";base64,";

        if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var mime = string.IsNullOrWhiteSpace(fallbackMimeType)
                ? "application/octet-stream"
                : fallbackMimeType;

            var f = string.IsNullOrWhiteSpace(fallbackFormat)
                ? MimeToAudioFormat(mime)
                : fallbackFormat;

            return (value, mime, f);
        }

        var markerIndex = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            throw new InvalidOperationException("Audio data URL did not contain base64 audio data.");

        var mimeType = value["data:".Length..markerIndex];

        if (string.IsNullOrWhiteSpace(mimeType))
            mimeType = string.IsNullOrWhiteSpace(fallbackMimeType)
                ? "application/octet-stream"
                : fallbackMimeType;

        var base64 = value[(markerIndex + marker.Length)..];

        if (string.IsNullOrWhiteSpace(base64))
            throw new InvalidOperationException("Audio data URL contained empty base64 audio data.");

        var format = MimeToAudioFormat(mimeType);

        if (string.IsNullOrWhiteSpace(format))
            format = string.IsNullOrWhiteSpace(fallbackFormat)
                ? "bin"
                : fallbackFormat;

        return (base64, mimeType, format);
    }


    private static string MimeToAudioFormat(string mimeType)
    {
        return mimeType.ToLowerInvariant() switch
        {
            "audio/wav" or "audio/wave" or "audio/x-wav" => "wav",
            "audio/mpeg" or "audio/mp3" => "mp3",
            "audio/ogg" => "ogg",
            "audio/webm" => "webm",
            "audio/flac" => "flac",
            "audio/aac" => "aac",
            "audio/mp4" or "audio/m4a" => "m4a",
            "audio/pcm" => "pcm",
            _ when mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
                => mimeType["audio/".Length..],
            _ => ""
        };
    }

    private async Task<SpeechResponse> DeepInfraSpeechRequest(string model,
        Dictionary<string, object?> payload,
        List<object> warnings,
        DateTime started,
        string outputFormat,
        CancellationToken cancellationToken = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"v1/inference/{model}")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, SpeechJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"DeepInfra TTS failed ({(int)resp.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        if (!root.TryGetProperty("audio", out var audioEl) || audioEl.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("DeepInfra TTS response did not contain audio data.");

        var audioBase64 = audioEl.GetString();
        if (string.IsNullOrWhiteSpace(audioBase64))
            throw new InvalidOperationException("DeepInfra TTS returned empty audio data.");

        var mime = OpenAIProvider.MapToAudioMimeType(outputFormat);

        if (!root.TryGetProperty("audio", out var a) || a.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("DeepInfra TTS response did not contain audio data.");

        var audioRaw = audioEl.GetString();
        if (string.IsNullOrWhiteSpace(audioRaw))
            throw new InvalidOperationException("DeepInfra TTS returned empty audio data.");

        var (audio, mimeType, format) = ParseAudioDataUrl(
            audioRaw,
            fallbackFormat: outputFormat,
            fallbackMimeType: OpenAIProvider.MapToAudioMimeType(outputFormat));

        var providerMetadata = GetIdentifier().CreatePrimitiveProviderMetadata();

        if (TryGetDeepInfraCost(root, out var cost))
        {
            providerMetadata["gateway"] = JsonSerializer.SerializeToElement(new
            {
                cost
            }, JsonSerializerOptions.Web);
        }

        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = audio,
                MimeType = mimeType,
                Format = format
            },
            Warnings = warnings,
            ProviderMetadata = providerMetadata,
            Response = new()
            {
                Timestamp = started,
                ModelId = model,
                Body = root.Clone()
            },
            Request = new SpeechRequestItem
            {
                Body = payload
            }
        };
    }

    private static bool TryGetDeepInfraCost(JsonElement root, out decimal cost)
    {
        cost = 0;

        if (!root.TryGetProperty("inference_status", out var inferenceStatusEl) ||
            inferenceStatusEl.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!inferenceStatusEl.TryGetProperty("cost", out var costEl) ||
            costEl.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        return costEl.TryGetDecimal(out cost);
    }

}
