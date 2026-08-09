using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Providers.OpenAI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.DeepInfra;

public sealed partial class DeepInfraProvider
{
    private static readonly JsonSerializerOptions SpeechJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(
        SpeechRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        ApplyAuthHeader();

        var started = DateTime.UtcNow;
        var outputFormat = string.IsNullOrWhiteSpace(request.OutputFormat)
            ? "wav"
            : request.OutputFormat.Trim().ToLowerInvariant();
        var payload = CreateDeepInfraSpeechPassthrough(request.GetProviderMetadata<JsonElement>(GetIdentifier()));

        payload["model"] = request.Model;
        payload["input"] = request.Text;
        SetDeepInfraSpeechValue(payload, "voice", request.Voice);
        SetDeepInfraSpeechValue(payload, "response_format", outputFormat);
        SetDeepInfraSpeechValue(payload, "speed", request.Speed);
        SetDeepInfraSpeechValue(payload, "instructions", request.Instructions);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, SpeechJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"DeepInfra speech request failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(audio)}");

        var mimeType = response.Content.Headers.ContentType?.MediaType
            ?? OpenAIProvider.MapToAudioMimeType(outputFormat);

        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = Convert.ToBase64String(audio),
                MimeType = mimeType,
                Format = outputFormat
            },
            Warnings = [],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Request = new() { Body = payload },
            Response = new()
            {
                Timestamp = started,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private static Dictionary<string, object?> CreateDeepInfraSpeechPassthrough(JsonElement metadata)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (metadata.ValueKind != JsonValueKind.Object)
            return payload;

        foreach (var property in metadata.EnumerateObject())
            payload[property.Name] = property.Value.Clone();

        return payload;
    }

    private static void SetDeepInfraSpeechValue(Dictionary<string, object?> payload, string name, object? value)
    {
        if (value is not null && (value is not string text || !string.IsNullOrWhiteSpace(text)))
            payload[name] = value;
    }
}
