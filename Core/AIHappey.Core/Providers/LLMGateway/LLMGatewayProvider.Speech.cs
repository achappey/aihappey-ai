using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.LLMGateway;

public partial class LLMGatewayProvider
{
    private static readonly JsonSerializerOptions LLMGatewaySpeechJsonOptions = new(JsonSerializerDefaults.Web)
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
        List<object> warnings = [];

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        var payload = BuildLLMGatewaySpeechPayload(request);
        var json = JsonSerializer.Serialize(payload, LLMGatewaySpeechJsonOptions);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.MediaType;

        if (!response.IsSuccessStatusCode)
        {
            var error = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"LLM Gateway speech request failed ({(int)response.StatusCode})."
                : $"LLM Gateway speech request failed ({(int)response.StatusCode}): {error}");
        }

        var format = ResolveLLMGatewaySpeechFormat(request, payload, contentType);
        var mimeType = ResolveLLMGatewaySpeechMimeType(format, contentType);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(bytes),
                MimeType = mimeType,
                Format = format
            },
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new ResponseData
            {
                Timestamp = now,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            },
            Request = new SpeechRequestItem
            {
                Body = payload
            }
        };
    }

    private static Dictionary<string, object?> BuildLLMGatewaySpeechPayload(SpeechRequest request)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = request.Model,
            ["input"] = request.Text
        };

        if (!string.IsNullOrWhiteSpace(request.Voice))
            payload["voice"] = request.Voice;

        if (!string.IsNullOrWhiteSpace(request.OutputFormat))
            payload["response_format"] = request.OutputFormat.Trim().ToLowerInvariant();

        if (request.Speed is not null)
            payload["speed"] = request.Speed.Value;

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            payload["instructions"] = request.Instructions;

        MergeLLMGatewaySpeechProviderOptions(payload, request);

        return payload;
    }

    private static void MergeLLMGatewaySpeechProviderOptions(Dictionary<string, object?> payload, SpeechRequest request)
    {
        if (request.ProviderOptions is null)
            return;

        if (!request.ProviderOptions.TryGetValue("llmgateway", out var providerOptions))
            return;

        if (providerOptions.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in providerOptions.EnumerateObject())
            payload[property.Name] = property.Value.Clone();
    }

    private static string ResolveLLMGatewaySpeechFormat(
        SpeechRequest request,
        IReadOnlyDictionary<string, object?> payload,
        string? contentType)
    {
        var requestedFormat = !string.IsNullOrWhiteSpace(request.OutputFormat)
            ? request.OutputFormat.Trim().ToLowerInvariant()
            : TryGetLLMGatewaySpeechString(payload, "response_format", "responseFormat", "outputFormat", "format")?.Trim().ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(requestedFormat))
            return requestedFormat;

        if (!string.IsNullOrWhiteSpace(contentType))
        {
            if (contentType.Contains("mpeg", StringComparison.OrdinalIgnoreCase) || contentType.Contains("mp3", StringComparison.OrdinalIgnoreCase))
                return "mp3";
            if (contentType.Contains("wav", StringComparison.OrdinalIgnoreCase) || contentType.Contains("wave", StringComparison.OrdinalIgnoreCase))
                return "wav";
            if (contentType.Contains("pcm", StringComparison.OrdinalIgnoreCase))
                return "pcm";
            if (contentType.Contains("opus", StringComparison.OrdinalIgnoreCase))
                return "opus";
            if (contentType.Contains("aac", StringComparison.OrdinalIgnoreCase))
                return "aac";
            if (contentType.Contains("flac", StringComparison.OrdinalIgnoreCase))
                return "flac";
        }

        return "wav";
    }

    private static string ResolveLLMGatewaySpeechMimeType(string format, string? contentType)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
            return contentType;

        return format.Trim().ToLowerInvariant() switch
        {
            "mp3" => "audio/mpeg",
            "opus" => "audio/opus",
            "aac" => "audio/aac",
            "flac" => "audio/flac",
            "wav" => "audio/wav",
            "pcm" => "audio/wav",
            _ => "application/octet-stream"
        };
    }

    private static string? TryGetLLMGatewaySpeechString(IReadOnlyDictionary<string, object?> payload, params string[] names)
    {
        foreach (var name in names)
        {
            var match = payload.FirstOrDefault(kvp => string.Equals(kvp.Key, name, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(match.Key))
                continue;

            return match.Value switch
            {
                null => null,
                string text => text,
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
                JsonElement element => element.GetRawText(),
                _ => match.Value.ToString()
            };
        }

        return null;
    }

}
