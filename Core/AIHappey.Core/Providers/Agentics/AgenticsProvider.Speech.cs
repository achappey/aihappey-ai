using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Agentics;

public partial class AgenticsProvider
{
    private const string AgenticsSpeechBaseModel = "tts";

    private static readonly JsonSerializerOptions AgenticsSpeechJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<SpeechResponse> AgenticsSpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var (_, modelVoice) = ParseAgenticsSpeechModelAndVoice(request.Model);

        var voice = modelVoice ?? request.Voice?.Trim() ?? TryGetAgenticsString(metadata, "voice") ?? "female2";
        if (modelVoice is not null
            && !string.IsNullOrWhiteSpace(request.Voice)
            && !string.Equals(request.Voice.Trim(), modelVoice, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });
        }

        var quality = TryGetInt(metadata, "quality");
        var speed = request.Speed ?? TryGetFloat(metadata, "speed");
        var outputBase64 = TryGetBoolean(metadata, "outputBase64", "output_base64") ?? true;

        var payload = new Dictionary<string, object?>
        {
            ["text"] = request.Text,
            ["voice"] = voice,
            ["speed"] = speed,
            ["quality"] = quality,
            ["outputBase64"] = outputBase64
        };

        MergeAgenticsAudioProviderOptions(payload, metadata, warnings, ["text", "voice", "speed", "quality", "outputBase64", "output_base64"]);

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/audio/tts")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, AgenticsSpeechJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Agentics speech failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        var contentType = response.Content.Headers.ContentType?.MediaType;
        var format = NormalizeAgenticsAudioFormat(request.OutputFormat ?? TryGetAgenticsString(metadata, "format") ?? TryGetAgenticsString(metadata, "outputFormat") ?? TryGetAgenticsString(metadata, "output_format"));
        string base64;
        JsonElement? providerMetadata = null;
        object? body;

        if (IsJsonContentType(contentType))
        {
            var raw = Encoding.UTF8.GetString(bytes);
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            providerMetadata = root.Clone();
            body = root.Clone();

            base64 = TryGetAgenticsString(root, "base64")
                ?? TryGetAgenticsString(root, "audio")
                ?? TryGetAgenticsString(root, "audioBase64")
                ?? TryGetAgenticsString(root, "audio_base64")
                ?? throw new InvalidOperationException("Agentics speech JSON response did not contain base64 audio.");
        }
        else
        {
            base64 = Convert.ToBase64String(bytes);
            body = new { contentType, length = bytes.Length };
        }

        format ??= ResolveAgenticsAudioFormat(contentType, "wav");
        var mimeType = ResolveAgenticsAudioMimeType(format, contentType);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = base64,
                MimeType = mimeType,
                Format = format
            },
            Warnings = warnings,
            ProviderMetadata = providerMetadata is { } metadataRoot
                ? new Dictionary<string, JsonElement> { [GetIdentifier()] = metadataRoot }
                : null,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = body
            }
        };
    }

    private static (string Model, string? Voice) ParseAgenticsSpeechModelAndVoice(string? model)
    {
        var localModel = string.IsNullOrWhiteSpace(model) ? AgenticsSpeechBaseModel : model.Trim();

        const string providerPrefix = "agentics/";
        if (localModel.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            localModel = localModel[providerPrefix.Length..];

        if (string.Equals(localModel, AgenticsSpeechBaseModel, StringComparison.OrdinalIgnoreCase))
            return (AgenticsSpeechBaseModel, null);

        var prefix = AgenticsSpeechBaseModel + "/";
        if (localModel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var voice = localModel[prefix.Length..].Trim();
            if (string.IsNullOrWhiteSpace(voice))
                throw new ArgumentException("Agentics speech shortcut model must be in the form 'tts/{voice}'.", nameof(model));

            return (AgenticsSpeechBaseModel, voice);
        }

        throw new NotSupportedException($"Agentics speech model '{model}' is not supported. Use 'tts' or 'tts/{{voice}}'.");
    }

    private static bool IsJsonContentType(string? contentType)
        => contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;

    private static string? NormalizeAgenticsAudioFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return null;

        return format.Trim().ToLowerInvariant();
    }

    private static string ResolveAgenticsAudioFormat(string? contentType, string fallback)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return fallback;

        if (contentType.Contains("mpeg", StringComparison.OrdinalIgnoreCase)) return "mp3";
        if (contentType.Contains("wav", StringComparison.OrdinalIgnoreCase) || contentType.Contains("wave", StringComparison.OrdinalIgnoreCase)) return "wav";
        if (contentType.Contains("ogg", StringComparison.OrdinalIgnoreCase)) return "ogg";
        if (contentType.Contains("opus", StringComparison.OrdinalIgnoreCase)) return "opus";
        if (contentType.Contains("flac", StringComparison.OrdinalIgnoreCase)) return "flac";
        if (contentType.Contains("aac", StringComparison.OrdinalIgnoreCase)) return "aac";

        return fallback;
    }

    private static string ResolveAgenticsAudioMimeType(string format, string? contentType)
        => format.Trim().ToLowerInvariant() switch
        {
            "mp3" => "audio/mpeg",
            "wav" => "audio/wav",
            "ogg" => "audio/ogg",
            "opus" => "audio/opus",
            "flac" => "audio/flac",
            "aac" => "audio/aac",
            _ => contentType ?? MediaTypeNames.Application.Octet
        };

}
