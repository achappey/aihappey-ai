using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Providers.OpenAI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Radient;

public partial class RadientProvider
{
    private static readonly JsonSerializerOptions RadientJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);

        var payload = CopyMetadata(request.ProviderOptions);
        payload["model"] = StripProviderPrefix(request.Model);
        payload["input"] = request.Text;
        Set(payload, "voice", request.Voice);
        Set(payload, "response_format", request.OutputFormat);
        Set(payload, "speed", request.Speed);

        var result = await SendSpeechAsync(payload, request.OutputFormat, cancellationToken);
        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(result.Audio),
                MimeType = result.MimeType,
                Format = request.OutputFormat ?? FormatFromMimeType(result.MimeType)
            },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Request = new SpeechRequestItem { Body = payload },
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = result.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private async Task<(byte[] Audio, string MimeType, Dictionary<string, string> Headers)> SendSpeechAsync(
        Dictionary<string, object?> payload,
        string? format,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/speech")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, RadientJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Radient speech request failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        return (bytes, response.Content.Headers.ContentType?.MediaType
            ?? OpenAIProvider.MapToAudioMimeType(format ?? "mp3"), response.GetHeaders());
    }

    private static Dictionary<string, object?> CopyMetadata(Dictionary<string, JsonElement>? options)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (options is null) return result;
        if (options.TryGetValue("radient", out var nested) && nested.ValueKind == JsonValueKind.Object)
            foreach (var property in nested.EnumerateObject()) result[property.Name] = property.Value.Clone();
        return result;
    }

    private static void Set(Dictionary<string, object?> payload, string key, object? value)
    {
        if (value is not null && (value is not string text || !string.IsNullOrWhiteSpace(text))) payload[key] = value;
    }

    private string StripProviderPrefix(string model)
    {
        var prefix = GetIdentifier() + "/";
        return model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? model[prefix.Length..] : model;
    }

    private static string FormatFromMimeType(string mimeType) => mimeType.ToLowerInvariant() switch
    {
        "audio/mpeg" => "mp3", "audio/ogg" => "opus", "audio/aac" => "aac",
        "audio/flac" => "flac", "audio/wav" or "audio/x-wav" => "wav", _ => "pcm"
    };
}
