using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using System.Globalization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using System.Text.Json.Nodes;

namespace AIHappey.Core.Providers.ResembleAI;

public partial class ResembleAIProvider
{
    private static readonly JsonSerializerOptions SpeechJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(
        SpeechRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        // Unified knobs not supported by Resemble's synchronous synth API.
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });
        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        JsonElement? providerOptions = request.ProviderOptions is not null
            && request.ProviderOptions.TryGetValue(GetIdentifier(), out var options)
                ? options
                : null;
        var rawMetadata = providerOptions is { ValueKind: JsonValueKind.Object }
            ? providerOptions
            : null;
        var voiceUuidMetadata = ReadProviderOption<string>(providerOptions, "voice_uuid");
        var projectUuid = ReadProviderOption<string>(providerOptions, "project_uuid");
        var title = ReadProviderOption<string>(providerOptions, "title");
        var precision = ReadProviderOption<string>(providerOptions, "precision");
        var outputFormatMetadata = ReadProviderOption<string>(providerOptions, "output_format");
        var sampleRate = ReadProviderOption<int?>(providerOptions, "sample_rate");
        var useHd = ReadProviderOption<bool?>(providerOptions, "use_hd");
        var applyCustomPronunciations = ReadProviderOption<bool?>(providerOptions, "apply_custom_pronunciations");

        var (baseModelId, modelVoiceUuid) = ParseSpeechModelAndVoice(request.Model);

        // Required: voice_uuid (from model-id or explicit request/provider metadata)
        var voiceUuid = (modelVoiceUuid ?? request.Voice ?? voiceUuidMetadata)?.Trim();
        if (string.IsNullOrWhiteSpace(voiceUuid))
            throw new ArgumentException("ResembleAI requires a voice UUID. Provide SpeechRequest.voice or providerOptions.resembleai.voice_uuid.", nameof(request));

        if (!string.IsNullOrWhiteSpace(modelVoiceUuid))
        {
            if (!string.IsNullOrWhiteSpace(request.Voice)
                && !string.Equals(request.Voice.Trim(), modelVoiceUuid, StringComparison.OrdinalIgnoreCase))
                warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });

            if (!string.IsNullOrWhiteSpace(voiceUuidMetadata)
                && !string.Equals(voiceUuidMetadata.Trim(), modelVoiceUuid, StringComparison.OrdinalIgnoreCase))
                warnings.Add(new { type = "ignored", feature = "providerOptions.resembleai.voice_uuid", reason = "voice is derived from model id" });
        }

        var outputFormat = NormalizeResembleOutputFormat(
            request.OutputFormat
            ?? outputFormatMetadata);

        // Build request body.
        var payload = rawMetadata is { } raw
            ? raw.EnumerateObject().ToDictionary(
                property => property.Name,
                property => (object?)property.Value.Clone(),
                StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        // Provider options are trusted passthrough data, but canonical request fields
        // always win so callers cannot replace the selected voice or input text.
        payload["voice_uuid"] = voiceUuid;
        payload["data"] = request.Text;
        payload["model"] = baseModelId;

        if (!string.IsNullOrWhiteSpace(projectUuid))
            payload["project_uuid"] = projectUuid;
        if (!string.IsNullOrWhiteSpace(title))
            payload["title"] = title;

        if (!string.IsNullOrWhiteSpace(precision))
            payload["precision"] = precision;
        if (!string.IsNullOrWhiteSpace(outputFormat))
            payload["output_format"] = outputFormat;
        if (sampleRate is not null)
            payload["sample_rate"] = sampleRate.Value;
        if (useHd is not null)
            payload["use_hd"] = useHd.Value;
        if (applyCustomPronunciations is not null)
            payload["apply_custom_pronunciations"] = applyCustomPronunciations.Value;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, new Uri("https://f.cluster.resemble.ai/synthesize"))
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, SpeechJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"ResembleAI TTS failed ({(int)resp.StatusCode}): {body}");

        // Response shape:
        // { success: true, audio_content: "<base64>", output_format: "wav"|"mp3", sample_rate: 48000, ... }
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var audioBase64 = root.TryGetProperty("audio_content", out var ac) && ac.ValueKind == JsonValueKind.String
            ? (ac.GetString() ?? string.Empty)
            : string.Empty;

        if (string.IsNullOrWhiteSpace(audioBase64))
            throw new InvalidOperationException($"ResembleAI TTS returned no audio_content. Body: {body}");

        var returnedFormat = root.TryGetProperty("output_format", out var of) && of.ValueKind == JsonValueKind.String
            ? of.GetString()
            : null;

        var effectiveFormat = NormalizeResembleOutputFormat(returnedFormat ?? outputFormat) ?? "wav";
        var mime = effectiveFormat.Equals("mp3", StringComparison.OrdinalIgnoreCase) ? "audio/mpeg" : "audio/wav";
        var modelItem = await this.GetModel(request.Model, cancellationToken);
        var duration = TryGetDecimal(root, "duration")
            ?? TryGetDecimal(root, "synth_duration");

        var costs = duration is > 0 && modelItem.Pricing?.Output != null
            ? duration.Value * modelItem.Pricing.Output
            : (decimal?)null;

        var metadataNode = JsonNode.Parse(body)?.AsObject()
            ?? throw new InvalidOperationException("ResembleAI TTS response was not a JSON object.");

        metadataNode.Remove("audio_content");

        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = audioBase64,
                MimeType = mime,
                Format = effectiveFormat
            },
            Warnings = warnings,
            Request = new()
            {
                Body = payload
            },
            ProviderMetadata = GetIdentifier()
                .CreatePrimitiveProviderMetadata(metadataNode, costs),
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = doc.RootElement.Clone()
            }
        };
    }

    private static T? ReadProviderOption<T>(JsonElement? providerOptions, string name)
    {
        if (providerOptions is not { } options
            || options.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return default;

        if (options.ValueKind != JsonValueKind.Object)
        {
            _ = options.Deserialize<Dictionary<string, JsonElement>>(JsonSerializerOptions.Web);
            return default;
        }

        JsonElement? value = null;
        foreach (var property in options.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                value = property.Value;
        }

        return value is { } propertyValue
            ? propertyValue.Deserialize<T>(JsonSerializerOptions.Web)
            : default;
    }

    private static decimal? TryGetDecimal(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var el))
            return null;

        return el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetDecimal(out var value) => value,
            JsonValueKind.String when decimal.TryParse(
                el.GetString(),
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out var value) => value,
            _ => null
        };
    }

    private (string BaseModelId, string? VoiceUuid) ParseSpeechModelAndVoice(string model)
    {
        var localModel = ExtractProviderLocalModelId(model);
        if (string.IsNullOrWhiteSpace(localModel))
            throw new ArgumentException("Model is required.", nameof(model));

        var slashIndex = localModel.IndexOf('/');
        if (slashIndex < 0)
        {
            if (!IsSupportedSpeechBaseModel(localModel))
                throw new NotSupportedException($"ResembleAI speech model '{model}' is not supported.");

            return (localModel, null);
        }

        if (slashIndex == 0 || slashIndex >= localModel.Length - 1)
            throw new ArgumentException("ResembleAI speech model must include both base model id and voice UUID in the form '[baseModel]/[voiceUuid]'.", nameof(model));

        var baseModelId = localModel[..slashIndex].Trim();
        var voiceUuid = localModel[(slashIndex + 1)..].Trim();

        if (string.IsNullOrWhiteSpace(baseModelId) || string.IsNullOrWhiteSpace(voiceUuid))
            throw new ArgumentException("ResembleAI speech model must include both base model id and voice UUID in the form '[baseModel]/[voiceUuid]'.", nameof(model));

        if (!IsSupportedSpeechBaseModel(baseModelId))
            throw new NotSupportedException($"ResembleAI speech base model '{baseModelId}' is not supported.");

        return (baseModelId, voiceUuid);
    }

    private static string? NormalizeResembleOutputFormat(string? outputFormat)
    {
        if (string.IsNullOrWhiteSpace(outputFormat))
            return null;

        var fmt = outputFormat.Trim().ToLowerInvariant();

        // Accept common aliases.
        if (fmt is "mpeg")
            fmt = "mp3";
        if (fmt is "wave")
            fmt = "wav";

        return fmt switch
        {
            "wav" => "wav",
            "mp3" => "mp3",
            _ => fmt // pass-through unknowns so Resemble can validate and return a helpful error
        };
    }
}
