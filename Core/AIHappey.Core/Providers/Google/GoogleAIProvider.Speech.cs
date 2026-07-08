using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Google;

public partial class GoogleAIProvider
{
    private const string GoogleSpeechDefaultVoice = "Kore";

    private static readonly GoogleSpeechVoice[] GoogleSpeechVoices =
    [
        new("Zephyr", "Bright"),
        new("Puck", "Upbeat"),
        new("Charon", "Informative"),
        new("Kore", "Firm"),
        new("Fenrir", "Excitable"),
        new("Leda", "Youthful"),
        new("Orus", "Firm"),
        new("Aoede", "Breezy"),
        new("Callirrhoe", "Easy-going"),
        new("Autonoe", "Bright"),
        new("Enceladus", "Breathy"),
        new("Iapetus", "Clear"),
        new("Umbriel", "Easy-going"),
        new("Algieba", "Smooth"),
        new("Despina", "Smooth"),
        new("Erinome", "Clear"),
        new("Algenib", "Gravelly"),
        new("Rasalgethi", "Informative"),
        new("Laomedeia", "Upbeat"),
        new("Achernar", "Soft"),
        new("Alnilam", "Firm"),
        new("Schedar", "Even"),
        new("Gacrux", "Mature"),
        new("Pulcherrima", "Forward"),
        new("Achird", "Friendly"),
        new("Zubenelgenubi", "Casual"),
        new("Vindemiatrix", "Gentle"),
        new("Sadachbia", "Lively"),
        new("Sadaltager", "Knowledgeable"),
        new("Sulafat", "Warm")
    ];

    private static readonly JsonSerializerOptions GoogleSpeechJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(
        SpeechRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var payload = BuildSpeechPayload(request, warnings);

        if (!string.IsNullOrWhiteSpace(request.OutputFormat))
            warnings.Add(new { type = "unsupported", feature = "outputFormat" });
        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });
        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, InteractionsRelativeUrl);
        httpRequest.Headers.Accept.Clear();
        httpRequest.Headers.Accept.ParseAdd(MediaTypeNames.Application.Json);
        httpRequest.Content = new StringContent(payload.ToJsonString(GoogleSpeechJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json);

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{Google} speech failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();

        if (!TryExtractGoogleSpeechAudio(root, out var base64, out var mimeType))
            throw new InvalidOperationException("No audio data returned.");

        mimeType = string.IsNullOrWhiteSpace(mimeType) ? "audio/L16" : mimeType;

        var providerMetadata = BuildGoogleSpeechProviderMetadata(root, request.Model);

        return new SpeechResponse
        {
            ProviderMetadata = providerMetadata,
            Audio = new SpeechAudioResponse
            {
                Base64 = base64,
                MimeType = mimeType,
                Format = ResolveGoogleSpeechAudioFormat(mimeType)
            },
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = root
            },
            Request = new SpeechRequestItem
            {
                Body = payload
            }
        };
    }

    private Dictionary<string, JsonElement> BuildGoogleSpeechProviderMetadata(JsonElement root, string model)
    {
        var hasUsage = TryGetProperty(root, "usage", out var usage)
            && usage.ValueKind == JsonValueKind.Object;

        var providerMetadata = new Dictionary<string, JsonElement>
        {
            [GetIdentifier()] = hasUsage
                ? JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>
                {
                    ["usage"] = usage.Clone()
                }, JsonSerializerOptions.Web)
                : JsonSerializer.SerializeToElement(new { }, JsonSerializerOptions.Web)
        };

        if (!hasUsage)
            return providerMetadata;

        var normalizedUsage = NormalizeGoogleUsage(usage, null);
        var serviceTier = TryGetString(root, "service_tier") ?? TryGetString(root, "serviceTier");
        var pricing = GoogleTieredPricingResolver.Resolve(
            model,
            serviceTier,
            ModelCostMetadataEnricher.GetTotalTokens(normalizedUsage));

        var costMetadata = ModelCostMetadataEnricher.AddCostFromUsage(
            normalizedUsage,
            existingMetadata: null,
            pricing);

        if (TryGetGatewayMetadata(costMetadata, out var gateway))
            providerMetadata["gateway"] = JsonSerializer.SerializeToElement(gateway, JsonSerializerOptions.Web);

        return providerMetadata;
    }

    private static JsonObject BuildSpeechPayload(SpeechRequest request, ICollection<object> warnings)
    {
        var (modelId, modelVoice) = ParseGoogleSpeechModelAndVoice(request.Model);

        if (!string.IsNullOrWhiteSpace(modelVoice)
            && !string.IsNullOrWhiteSpace(request.Voice)
            && !string.Equals(request.Voice.Trim(), modelVoice, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });
        }

        var metadata = request.GetProviderMetadata<JsonElement>(GoogleExtensions.Identifier());
        var payload = metadata.ValueKind == JsonValueKind.Object
            ? CloneJsonObject(metadata)
            : [];

        if (!HasJsonProperty(payload, "model"))
            payload["model"] = modelId;

        if (!HasJsonProperty(payload, "input"))
            payload["input"] = request.Text;

        if (!HasJsonProperty(payload, "response_format"))
        {
            payload["response_format"] = new JsonObject
            {
                ["type"] = "audio"
            };
        }

        var speechConfigFromTopLevel = TryRemoveJsonProperty(payload, "speech_config", out var topLevelSpeechConfig)
            ? topLevelSpeechConfig
            : null;

        var hasGenerationConfig = TryGetJsonObjectProperty(payload, "generation_config", out var generationConfig);

        if (!HasJsonProperty(generationConfig, "speech_config"))
        {
            generationConfig["speech_config"] = speechConfigFromTopLevel ?? BuildDefaultGoogleSpeechConfig(request, modelVoice);
        }
        else if (speechConfigFromTopLevel is not null)
        {
            warnings.Add(new
            {
                type = "ignored",
                feature = "providerOptions.google.speech_config",
                reason = "providerOptions.google.generation_config.speech_config is already set"
            });
        }

        if (!hasGenerationConfig)
            payload["generation_config"] = generationConfig;

        return payload;
    }

    private static JsonArray BuildDefaultGoogleSpeechConfig(SpeechRequest request)
        => BuildDefaultGoogleSpeechConfig(request, modelVoice: null);

    private static JsonArray BuildDefaultGoogleSpeechConfig(SpeechRequest request, string? modelVoice)
    {
        var voice = !string.IsNullOrWhiteSpace(modelVoice)
            ? modelVoice
            : string.IsNullOrWhiteSpace(request.Voice)
            ? GoogleSpeechDefaultVoice
            : ResolveGoogleSpeechVoiceName(request.Voice.Trim()) ?? request.Voice.Trim();

        return
        [
            new JsonObject
            {
                ["voice"] = voice
            }
        ];
    }

    private static (string ModelId, string? Voice) ParseGoogleSpeechModelAndVoice(string model)
    {
        var localModel = model.Trim();
        var providerPrefix = GoogleExtensions.Identifier() + "/";
        if (localModel.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            localModel = localModel[providerPrefix.Length..];

        var slashIndex = localModel.LastIndexOf('/');
        if (slashIndex <= 0 || slashIndex >= localModel.Length - 1)
            return (localModel, null);

        var baseModel = localModel[..slashIndex].Trim();
        var voice = localModel[(slashIndex + 1)..].Trim();

        if (!baseModel.Contains("tts", StringComparison.OrdinalIgnoreCase))
            return (localModel, null);

        if (string.IsNullOrWhiteSpace(voice))
            throw new ArgumentException("Google speech model voice shortcut must be in the form '[tts-model]/{voice}'.", nameof(model));

        var canonicalVoice = ResolveGoogleSpeechVoiceName(voice)
            ?? throw new NotSupportedException($"Google speech voice '{voice}' is not supported.");

        return (baseModel, canonicalVoice);
    }

    private static string? ResolveGoogleSpeechVoiceName(string voice)
        => GoogleSpeechVoices.FirstOrDefault(v => string.Equals(v.Name, voice.Trim(), StringComparison.OrdinalIgnoreCase))?.Name;

    private static bool TryExtractGoogleSpeechAudio(JsonElement root, out string base64, out string? mimeType)
    {
        base64 = string.Empty;
        mimeType = null;

        if (TryGetProperty(root, "output_audio", out var outputAudio)
            && TryExtractOutputAudioObject(outputAudio, out base64, out mimeType))
        {
            return true;
        }

        return TryFindAudioObject(root, out base64, out mimeType);
    }

    private static bool TryFindAudioObject(JsonElement element, out string base64, out string? mimeType)
    {
        base64 = string.Empty;
        mimeType = null;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (TryExtractAudioFromObject(element, out base64, out mimeType))
                    return true;

                foreach (var property in element.EnumerateObject())
                {
                    if (TryFindAudioObject(property.Value, out base64, out mimeType))
                        return true;
                }

                return false;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (TryFindAudioObject(item, out base64, out mimeType))
                        return true;
                }

                return false;

            default:
                return false;
        }
    }

    private static bool TryExtractAudioFromObject(JsonElement element, out string base64, out string? mimeType)
    {
        base64 = string.Empty;
        mimeType = null;

        if (element.ValueKind != JsonValueKind.Object)
            return false;

        var type = TryGetString(element, "type");
        var data = TryGetString(element, "data");
        var mime = TryGetString(element, "mime_type") ?? TryGetString(element, "mimeType");

        if (string.IsNullOrWhiteSpace(data))
            return false;

        if (!string.Equals(type, "audio", StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(mime) || !mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        base64 = data;
        mimeType = mime;
        return true;
    }

    private static bool TryExtractOutputAudioObject(JsonElement element, out string base64, out string? mimeType)
    {
        base64 = string.Empty;
        mimeType = null;

        if (element.ValueKind != JsonValueKind.Object)
            return false;

        var data = TryGetString(element, "data");
        if (string.IsNullOrWhiteSpace(data))
            return false;

        base64 = data;
        mimeType = TryGetString(element, "mime_type") ?? TryGetString(element, "mimeType");
        return true;
    }

    private static string ResolveGoogleSpeechAudioFormat(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType))
            return "pcm";

        var normalized = mimeType.Trim().ToLowerInvariant();
        if (normalized.Contains("mpeg") || normalized.Contains("mp3")) return "mp3";
        if (normalized.Contains("wav") || normalized.Contains("wave")) return "wav";
        if (normalized.Contains("ogg")) return "ogg";
        if (normalized.Contains("opus")) return "opus";
        if (normalized.Contains("flac")) return "flac";
        if (normalized.Contains("aac")) return "aac";
        if (normalized.Contains("l16") || normalized.Contains("pcm")) return "pcm";

        return "pcm";
    }

    private static JsonObject CloneJsonObject(JsonElement element)
        => JsonNode.Parse(element.GetRawText()) as JsonObject ?? [];

    private static bool HasJsonProperty(JsonObject obj, string propertyName)
        => obj.Any(property => string.Equals(property.Key, propertyName, StringComparison.OrdinalIgnoreCase));

    private static bool TryGetJsonObjectProperty(JsonObject obj, string propertyName, out JsonObject value)
    {
        foreach (var property in obj)
        {
            if (!string.Equals(property.Key, propertyName, StringComparison.OrdinalIgnoreCase))
                continue;

            value = property.Value as JsonObject ?? [];
            return true;
        }

        value = [];
        return false;
    }

    private static bool TryRemoveJsonProperty(JsonObject obj, string propertyName, out JsonNode? value)
    {
        foreach (var property in obj.ToList())
        {
            if (!string.Equals(property.Key, propertyName, StringComparison.OrdinalIgnoreCase))
                continue;

            value = property.Value?.DeepClone();
            obj.Remove(property.Key);
            return true;
        }

        value = null;
        return false;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement property)
    {
        property = default;

        if (element.ValueKind != JsonValueKind.Object)
            return false;

        if (element.TryGetProperty(propertyName, out property))
            return true;

        foreach (var candidate in element.EnumerateObject())
        {
            if (string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                property = candidate.Value;
                return true;
            }
        }

        return false;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var property)
            || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.ToString();
    }

    private sealed record GoogleSpeechVoice(string Name, string Style);
}
