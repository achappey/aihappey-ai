using AIHappey.Vercel.Models;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.Zyphra;

public partial class ZyphraProvider
{
    private static readonly JsonSerializerOptions ZyphraSpeechJson = new(JsonSerializerDefaults.Web)
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

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        var apiKey = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException($"No {nameof(Zyphra)} API key.");

        var (model, modelVoice) = ParseZyphraSpeechModelAndVoice(request.Model);
        var outputMimeType = NormalizeZyphraMimeType(request.OutputFormat);
        var zyphraOptions = TryGetZyphraOptions(request);

        var payload = new JsonObject
        {
            ["text"] = request.Text,
            ["model"] = model
        };

        var requestVoice = string.IsNullOrWhiteSpace(request.Voice) ? null : request.Voice.Trim();
        var effectiveVoice = modelVoice ?? requestVoice;
        if (!string.IsNullOrWhiteSpace(effectiveVoice))
            payload["default_voice_name"] = effectiveVoice;

        if (!string.IsNullOrWhiteSpace(request.Language))
            payload["language_iso_code"] = request.Language.Trim();

        if (request.Speed is not null)
            payload["speaking_rate"] = request.Speed;

        if (!string.IsNullOrWhiteSpace(outputMimeType))
            payload["mime_type"] = outputMimeType;

        MergeZyphraOptions(payload, zyphraOptions);

        // Model shortcut voices (baseModel/voice) are authoritative.
        // Ensure provider options cannot override the voice selected by model id.
        if (!string.IsNullOrWhiteSpace(modelVoice))
        {
            payload["default_voice_name"] = modelVoice;
            payload["voice_name"] = modelVoice;
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/audio/text-to-speech")
        {
            Content = new StringContent(payload.ToJsonString(ZyphraSpeechJson), Encoding.UTF8, "application/json")
        };

        httpRequest.Headers.Add("X-API-Key", apiKey);
        httpRequest.Headers.Accept.Clear();
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/*"));

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException($"Zyphra TTS failed ({(int)resp.StatusCode}): {err}");
        }

        var responseMimeType = resp.Content.Headers.ContentType?.MediaType ?? outputMimeType;
        var format = ResolveZyphraOutputFormat(request.OutputFormat, responseMimeType, outputMimeType);

        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = Convert.ToBase64String(bytes),
                MimeType = responseMimeType ?? "application/octet-stream",
                Format = format ?? "webm"
            },
            Warnings = warnings,
            ProviderMetadata = BuildZyphraProviderMetadata(model, payload, responseMimeType),
            Response = new()
            {
                Timestamp = now,
                ModelId = model
            }
        };
    }

    private static (string BaseModelId, string? VoiceName) ParseZyphraSpeechModelAndVoice(string model)
    {
        var localModel = NormalizeZyphraModelId(model);
        if (string.IsNullOrWhiteSpace(localModel))
            throw new ArgumentException("Model is required.", nameof(model));

        var slashIndex = localModel.IndexOf('/');
        if (slashIndex < 0)
            return (localModel, null);

        if (slashIndex == 0 || slashIndex >= localModel.Length - 1)
            throw new ArgumentException("Zyphra speech model must include both base model id and voice name in the form '[baseModel]/[voiceName]'.", nameof(model));

        var baseModelId = localModel[..slashIndex].Trim();
        var voiceName = localModel[(slashIndex + 1)..].Trim();

        if (string.IsNullOrWhiteSpace(baseModelId) || string.IsNullOrWhiteSpace(voiceName))
            throw new ArgumentException("Zyphra speech model must include both base model id and voice name in the form '[baseModel]/[voiceName]'.", nameof(model));

        return (baseModelId, voiceName);
    }

    private static string NormalizeZyphraModelId(string model)
    {
        var trimmed = model.Trim();
        if (trimmed.StartsWith("zyphra/", StringComparison.OrdinalIgnoreCase))
            return trimmed["zyphra/".Length..];

        return trimmed;
    }

    private static JsonElement? TryGetZyphraOptions(SpeechRequest request)
    {
        if (request.ProviderOptions is null)
            return null;

        if (!request.ProviderOptions.TryGetValue("zyphra", out var zyphra))
            return null;

        if (zyphra.ValueKind != JsonValueKind.Object)
            return null;

        return zyphra;
    }

    private static void MergeZyphraOptions(JsonObject payload, JsonElement? zyphraOptions)
    {
        if (zyphraOptions is null)
            return;

        var options = zyphraOptions.Value;
        if (options.ValueKind != JsonValueKind.Object)
            return;

        foreach (var prop in options.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "speaker_audio":
                case "voice_name":
                case "default_voice_name":
                case "emotion":
                case "vqscore":
                case "fmax":
                case "pitchStd":
                case "speaking_rate":
                case "language_iso_code":
                case "mime_type":
                case "model":
                case "speaker_noised":
                    payload[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
                    break;
            }
        }
    }

    private static string? NormalizeZyphraMimeType(string? outputFormat)
    {
        if (string.IsNullOrWhiteSpace(outputFormat))
            return null;

        var trimmed = outputFormat.Trim().ToLowerInvariant();
        if (trimmed.Contains('/'))
            return trimmed;

        return trimmed switch
        {
            "mp3" => "audio/mpeg",
            "mpeg" => "audio/mpeg",
            "wav" => "audio/wav",
            "ogg" => "audio/ogg",
            "webm" => "audio/webm",
            "aac" => "audio/aac",
            "mp4" or "m4a" => "audio/mp4",
            _ => null
        };
    }

    private static string? ResolveZyphraOutputFormat(string? requestFormat, string? responseMimeType, string? requestedMimeType)
    {
        if (!string.IsNullOrWhiteSpace(requestFormat))
        {
            var trimmed = requestFormat.Trim().ToLowerInvariant();
            return trimmed.Contains('/') ? MapMimeToAudioFormat(trimmed) : trimmed;
        }

        if (!string.IsNullOrWhiteSpace(responseMimeType))
            return MapMimeToAudioFormat(responseMimeType.Trim().ToLowerInvariant());

        if (!string.IsNullOrWhiteSpace(requestedMimeType))
            return MapMimeToAudioFormat(requestedMimeType.Trim().ToLowerInvariant());

        return null;
    }

    private static string MapMimeToAudioFormat(string mimeType)
    {
        return mimeType switch
        {
            "audio/mpeg" or "audio/mp3" => "mp3",
            "audio/wav" or "audio/wave" or "audio/x-wav" => "wav",
            "audio/ogg" => "ogg",
            "audio/webm" => "webm",
            "audio/aac" => "aac",
            "audio/mp4" => "m4a",
            _ => "webm"
        };
    }

    private Dictionary<string, JsonElement>? BuildZyphraProviderMetadata(
        string model,
        JsonObject payload,
        string? responseMimeType)
    {
        var metadata = new Dictionary<string, JsonElement>
        {
            ["model"] = JsonSerializer.SerializeToElement(model, ZyphraSpeechJson),
            ["payload"] = JsonSerializer.SerializeToElement(payload, ZyphraSpeechJson)
        };

        if (!string.IsNullOrWhiteSpace(responseMimeType))
            metadata["response_mime_type"] = JsonSerializer.SerializeToElement(responseMimeType, ZyphraSpeechJson);

        return new Dictionary<string, JsonElement>
        {
            [GetIdentifier()] = JsonSerializer.SerializeToElement(metadata, ZyphraSpeechJson)
        };
    }

}
