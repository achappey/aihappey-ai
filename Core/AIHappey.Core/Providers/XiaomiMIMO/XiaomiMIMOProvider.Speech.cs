using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.XiaomiMIMO;

public partial class XiaomiMIMOProvider
{
    private static readonly JsonSerializerOptions XiaomiMimoSpeechJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly string[] XiaomiMimoTtsModels =
    [
        "mimo-v2.5-tts",
        "mimo-v2.5-tts-voicedesign",
        "mimo-v2.5-tts-voiceclone"
    ];

    private static readonly string[] XiaomiMimoBuiltInVoices =
    [
        "mimo_default", "冰糖", "茉莉", "苏打", "白桦", "Mia", "Chloe", "Milo", "Dean"
    ];

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var response = await SpeechRequest(options.ToSpeechRequest(), cancellationToken);
        return response.ToOpenAISpeechAudio();
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var request = options.ToSpeechRequest();
        ValidateXiaomiMimoSpeechRequest(request, streaming: true);

        var metadata = request.GetProviderMetadata<XiaomiMimoSpeechProviderMetadata>(GetIdentifier());
        var format = ResolveOutputFormat(request, metadata);
        var voice = ResolveVoice(request, metadata);
        var warnings = new List<object>();
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["messages"] = BuildSpeechMessages(request, metadata, warnings),
            ["audio"] = new Dictionary<string, object?>
            {
                ["format"] = format,
                ["voice"] = voice,
                ["optimize_text_preview"] = metadata?.OptimizeTextPreview
            },
            ["stream"] = true
        };
        var requestBody = JsonSerializer.Serialize(payload, XiaomiMimoSpeechJsonOptions);
        AudioSpeechUsage? usage = null;

        await foreach (var root in SendXiaomiMimoAudioStreamAsync(requestBody, "speech", cancellationToken))
        {
            usage = ExtractXiaomiMimoSpeechUsage(root) ?? usage;
            var audio = ExtractXiaomiMimoStreamAudio(root);
            if (!string.IsNullOrWhiteSpace(audio))
                yield return new AudioSpeechStreamDelta { Audio = audio };
        }

        yield return new AudioSpeechStreamDone { Usage = usage };
    }

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        ValidateXiaomiMimoSpeechRequest(request, streaming: false);

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
                ModelId = request.Model.ToModelId(GetIdentifier()),
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

    private static void ValidateXiaomiMimoSpeechRequest(SpeechRequest request, bool streaming)
    {
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));
        if (!XiaomiMimoTtsModels.Contains(request.Model.Trim(), StringComparer.Ordinal))
            throw new ArgumentException("Unsupported Xiaomi MiMo TTS model. Supported models: mimo-v2.5-tts, mimo-v2.5-tts-voicedesign, mimo-v2.5-tts-voiceclone.", nameof(request));

        var metadata = request.GetProviderMetadata<XiaomiMimoSpeechProviderMetadata>("xiaomimimo");
        var format = ResolveOutputFormat(request, metadata);
        var allowedFormats = streaming ? new[] { "pcm", "pcm16" } : new[] { "wav", "mp3", "pcm", "pcm16" };
        if (!allowedFormats.Contains(format, StringComparer.Ordinal))
            throw new ArgumentException($"Xiaomi MiMo {(streaming ? "streaming " : string.Empty)}speech output format must be one of: {string.Join(", ", allowedFormats)}.", nameof(request));

        var voice = ResolveVoice(request, metadata);
        if (IsVoiceDesignModel(request.Model))
        {
            if (!string.IsNullOrWhiteSpace(voice))
                throw new ArgumentException("mimo-v2.5-tts-voicedesign does not support audio.voice.", nameof(request));
            var description = request.Instructions ?? metadata?.StylePrompt ?? metadata?.VoiceDescription;
            if (string.IsNullOrWhiteSpace(description) && metadata?.OptimizeTextPreview is not true)
                throw new ArgumentException("mimo-v2.5-tts-voicedesign requires instructions/voice description unless optimize_text_preview is true.", nameof(request));
        }
        else if (IsVoiceCloneModel(request.Model))
        {
            if (string.IsNullOrWhiteSpace(voice))
                throw new ArgumentException("mimo-v2.5-tts-voiceclone requires a base64 MP3 or WAV audio sample in voice.", nameof(request));
            ValidateXiaomiMimoVoiceSample(voice);
        }
        else if (!XiaomiMimoBuiltInVoices.Contains(voice, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Unsupported Xiaomi MiMo built-in voice '{voice}'.", nameof(request));
        }
    }

    private static void ValidateXiaomiMimoVoiceSample(string voice)
    {
        var base64 = voice.Trim();
        if (base64.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = base64.IndexOf(',');
            if (comma < 0)
                throw new ArgumentException("Voice sample data URL is invalid.", nameof(voice));
            var header = base64[5..comma].ToLowerInvariant();
            if (!header.StartsWith("audio/mpeg;") && !header.StartsWith("audio/mp3;") && !header.StartsWith("audio/wav;"))
                throw new ArgumentException("Voice sample data URL must contain MP3 or WAV audio.", nameof(voice));
            base64 = base64[(comma + 1)..];
        }

        try
        {
            Convert.FromBase64String(base64);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Voice sample must be valid base64 MP3 or WAV audio.", nameof(voice), exception);
        }
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

    private static string? ExtractXiaomiMimoStreamAudio(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var choice in choices.EnumerateArray())
        {
            if (choice.TryGetProperty("delta", out var delta)
                && delta.ValueKind == JsonValueKind.Object
                && delta.TryGetProperty("audio", out var audio)
                && audio.ValueKind == JsonValueKind.Object
                && audio.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.String)
                return data.GetString();
        }

        return null;
    }

    private static AudioSpeechUsage? ExtractXiaomiMimoSpeechUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;

        return new AudioSpeechUsage
        {
            InputTokens = TryGetXiaomiMimoInt32(usage, "prompt_tokens"),
            OutputTokens = TryGetXiaomiMimoInt32(usage, "completion_tokens"),
            TotalTokens = TryGetXiaomiMimoInt32(usage, "total_tokens")
        };
    }

    private static int? TryGetXiaomiMimoInt32(JsonElement value, string propertyName)
        => value.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var result)
            ? result
            : null;

    private static string ResolveMimeType(string outputFormat)
        => outputFormat.Trim().ToLowerInvariant() switch
        {
            "wav" => "audio/wav",
            "pcm" => "audio/pcm",
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
