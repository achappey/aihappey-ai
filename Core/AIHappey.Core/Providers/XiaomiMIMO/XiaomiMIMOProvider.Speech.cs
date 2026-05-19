using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.XiaomiMIMO;

public partial class XiaomiMIMOProvider
{
    private static readonly JsonSerializerOptions XiaomiMimoSpeechJsonOptions = new(JsonSerializerDefaults.Web)
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

        var metadata = request.GetProviderMetadata<XiaomiMimoSpeechProviderMetadata>(GetIdentifier());
        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });
        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed", detail = "Use Xiaomi style tags in text or providerOptions.xiaomimimo.stylePrompt/instructions." });

        var outputFormat = ResolveOutputFormat(request, metadata);
        var voice = ResolveVoice(request, metadata);

        var messages = BuildSpeechMessages(request, metadata, warnings);
        var audio = new Dictionary<string, object?>
        {
            ["format"] = outputFormat,
            ["voice"] = voice,
            ["optimize_text_preview"] = metadata?.OptimizeTextPreview
        };

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["messages"] = messages,
            ["audio"] = audio,
            ["stream"] = false
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, XiaomiMimoSpeechJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Xiaomi MiMo speech request failed ({(int)resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement.Clone();
        var audioData = ExtractAudioData(root);

        if (string.IsNullOrWhiteSpace(audioData))
            throw new InvalidOperationException($"Xiaomi MiMo speech response did not include choices[0].message.audio.data. Body: {body}");

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = audioData,
                MimeType = ResolveMimeType(outputFormat),
                Format = outputFormat
            },
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(new
                {
                    model = request.Model,
                    audio = new
                    {
                        format = outputFormat,
                        voice,
                        optimize_text_preview = metadata?.OptimizeTextPreview
                    },
                    response = root
                }, JsonSerializerOptions.Web)
            },
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = root
            }
        };
    }

    private static List<object> BuildSpeechMessages(
        SpeechRequest request,
        XiaomiMimoSpeechProviderMetadata? metadata,
        List<object> warnings)
    {
        var messages = new List<object>();
        var userPrompt = request.Instructions;

        if (string.IsNullOrWhiteSpace(userPrompt))
            userPrompt = metadata?.StylePrompt;

        if (string.IsNullOrWhiteSpace(userPrompt))
            userPrompt = metadata?.VoiceDescription;

        if (!string.IsNullOrWhiteSpace(userPrompt))
        {
            messages.Add(new
            {
                role = "user",
                content = userPrompt
            });
        }
        else if (IsVoiceDesignModel(request.Model))
        {
            warnings.Add(new { type = "missing", feature = "voice_description", detail = "mimo-v2.5-tts-voicedesign works best with instructions or providerOptions.xiaomimimo.voiceDescription/stylePrompt." });
            messages.Add(new
            {
                role = "user",
                content = string.Empty
            });
        }
        else if (IsVoiceCloneModel(request.Model))
        {
            messages.Add(new
            {
                role = "user",
                content = string.Empty
            });
        }

        messages.Add(new
        {
            role = "assistant",
            content = request.Text
        });

        return messages;
    }

    private static string ResolveOutputFormat(SpeechRequest request, XiaomiMimoSpeechProviderMetadata? metadata)
    {
        var outputFormat = request.OutputFormat?.Trim();
        if (string.IsNullOrWhiteSpace(outputFormat))
            outputFormat = metadata?.ResponseFormat?.Trim();
        if (string.IsNullOrWhiteSpace(outputFormat))
            outputFormat = "wav";

        return outputFormat.ToLowerInvariant();
    }

    private static string? ResolveVoice(SpeechRequest request, XiaomiMimoSpeechProviderMetadata? metadata)
    {
        var voice = request.Voice?.Trim();
        if (string.IsNullOrWhiteSpace(voice))
            voice = metadata?.Voice?.Trim();
        if (string.IsNullOrWhiteSpace(voice))
            voice = metadata?.VoiceSample?.Trim();

        if (string.IsNullOrWhiteSpace(voice) && !IsVoiceDesignModel(request.Model))
            voice = "mimo_default";

        return string.IsNullOrWhiteSpace(voice) ? null : voice;
    }

    private static string? ExtractAudioData(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var choice in choices.EnumerateArray())
        {
            if (choice.TryGetProperty("message", out var message)
                && message.TryGetProperty("audio", out var audio)
                && audio.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.String)
            {
                return data.GetString();
            }
        }

        return null;
    }

    private static string ResolveMimeType(string outputFormat)
        => outputFormat.Trim().ToLowerInvariant() switch
        {
            "wav" => "audio/wav",
            "pcm16" => "audio/pcm",
            "mp3" => "audio/mpeg",
            "mpeg" => "audio/mpeg",
            _ => "application/octet-stream"
        };

    private static bool IsVoiceDesignModel(string model)
        => model.Contains("voicedesign", StringComparison.OrdinalIgnoreCase);

    private static bool IsVoiceCloneModel(string model)
        => model.Contains("voiceclone", StringComparison.OrdinalIgnoreCase);

    private sealed class XiaomiMimoSpeechProviderMetadata
    {
        [JsonPropertyName("voice")]
        public string? Voice { get; set; }

        [JsonPropertyName("voiceSample")]
        public string? VoiceSample { get; set; }

        [JsonPropertyName("voice_sample")]
        public string? VoiceSampleSnakeCase
        {
            get => VoiceSample;
            set => VoiceSample = value;
        }

        [JsonPropertyName("responseFormat")]
        public string? ResponseFormat { get; set; }

        [JsonPropertyName("response_format")]
        public string? ResponseFormatSnakeCase
        {
            get => ResponseFormat;
            set => ResponseFormat = value;
        }

        [JsonPropertyName("stylePrompt")]
        public string? StylePrompt { get; set; }

        [JsonPropertyName("style_prompt")]
        public string? StylePromptSnakeCase
        {
            get => StylePrompt;
            set => StylePrompt = value;
        }

        [JsonPropertyName("voiceDescription")]
        public string? VoiceDescription { get; set; }

        [JsonPropertyName("voice_description")]
        public string? VoiceDescriptionSnakeCase
        {
            get => VoiceDescription;
            set => VoiceDescription = value;
        }

        [JsonPropertyName("optimizeTextPreview")]
        public bool? OptimizeTextPreview { get; set; }

        [JsonPropertyName("optimize_text_preview")]
        public bool? OptimizeTextPreviewSnakeCase
        {
            get => OptimizeTextPreview;
            set => OptimizeTextPreview = value;
        }
    }
}
