using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.TheRouterAI;

public partial class TheRouterAIProvider
{
    private const string TheRouterAISpeechEndpoint = "v1/audio/speech";

    private static readonly JsonSerializerOptions TheRouterAIJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<SpeechResponse> TheRouterAISpeechRequest(
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
        var payload = TheRouterAIProviderOptionsToDictionary(request.ProviderOptions);

        payload["model"] = request.Model;
        payload["input"] = request.Text;

        if (!string.IsNullOrWhiteSpace(request.Voice))
            payload["voice"] = request.Voice.Trim();

        if (!string.IsNullOrWhiteSpace(request.OutputFormat))
            payload["response_format"] = request.OutputFormat.Trim();

        if (request.Speed is not null)
            payload["speed"] = request.Speed.Value;

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            payload["instructions"] = request.Instructions;

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, TheRouterAISpeechEndpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, TheRouterAIJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/*"));

        using var resp = await _client.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException($"TheRouterAI TTS failed ({(int)resp.StatusCode}): {err}");
        }

        var outputFormat = ResolveStringPayloadValue(payload, "response_format")
                           ?? resp.Content.Headers.ContentType?.MediaType
                           ?? "mp3";
        var mime = ResolveTheRouterAISpeechMimeType(outputFormat, resp.Content.Headers.ContentType?.MediaType);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(bytes),
                MimeType = mime,
                Format = outputFormat
            },
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new ResponseData
            {
                Timestamp = now,
                Headers = resp.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            },
            Request = new SpeechRequestItem
            {
                Body = payload
            }
        };
    }

    private Dictionary<string, object?> TheRouterAIProviderOptionsToDictionary(
        Dictionary<string, JsonElement>? providerOptions)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (providerOptions is null
            || !providerOptions.TryGetValue(GetIdentifier(), out var providerMetadata)
            || providerMetadata.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return payload;
        }

        if (providerMetadata.ValueKind != JsonValueKind.Object)
            throw new ArgumentException($"providerOptions.{GetIdentifier()} must be a JSON object.");

        foreach (var property in providerMetadata.EnumerateObject())
            payload[property.Name] = property.Value.Clone();

        return payload;
    }

    private static string? ResolveStringPayloadValue(
        IReadOnlyDictionary<string, object?> payload,
        string key)
    {
        if (!payload.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            string s when !string.IsNullOrWhiteSpace(s) => s,
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
            JsonElement { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } json => json.ToString(),
            _ => value.ToString()
        };
    }

    private static string ResolveTheRouterAISpeechMimeType(string? responseFormat, string? contentType)
    {
        var fmt = (responseFormat ?? string.Empty).Trim().ToLowerInvariant();
        return fmt switch
        {
            "mp3" => "audio/mpeg",
            "opus" => "audio/opus",
            "aac" => "audio/aac",
            "flac" => "audio/flac",
            "wav" => "audio/wav",
            "pcm" => "audio/pcm",
            _ => contentType ?? "application/octet-stream"
        };
    }
}
