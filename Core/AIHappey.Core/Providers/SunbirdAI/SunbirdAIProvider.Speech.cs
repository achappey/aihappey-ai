using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.SunbirdAI;

public partial class SunbirdAIProvider
{
    private const string SpeechEndpoint = "tasks/audio/speech";

    public async Task<SpeechResponse> SpeechRequest(
        SpeechRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);

        var payload = GetSunbirdObject(request.ProviderOptions);
        payload["text"] = request.Text;
        payload["model"] = GetSunbirdLeafModel(request.Model);
        SetSunbirdValue(payload, "voice", request.Voice);
        SetSunbirdValue(payload, "language", request.Language);
        SetSunbirdValue(payload, "response_format", request.OutputFormat);
        if (request.Speed is not null)
            payload["speed"] = request.Speed.Value;
        SetSunbirdValue(payload, "instructions", request.Instructions);

        var result = await SendSunbirdSpeechAsync(payload, request.OutputFormat, cancellationToken);
        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(result.Audio),
                MimeType = result.MimeType,
                Format = GetSunbirdAudioFormat(request.OutputFormat, result.MimeType)
            },
            Warnings = [],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.ProviderResponse),
            Request = new SpeechRequestItem { Body = payload },
            Response = new ResponseData
            {
                Timestamp = result.Timestamp,
                Headers = result.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = result.ProviderResponse
            }
        };
    }

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Input);

        var payload = SunbirdJsonObject(options.AdditionalProperties);
        payload["text"] = options.Input;
        payload["model"] = GetSunbirdLeafModel(options.Model);
        SetSunbirdValue(payload, "voice", options.Voice);
        SetSunbirdValue(payload, "response_format", options.ResponseFormat);
        SetSunbirdValue(payload, "instructions", options.Instructions);
        if (options.Speed is not null)
            payload["speed"] = options.Speed.Value;

        var result = await SendSunbirdSpeechAsync(payload, options.ResponseFormat, cancellationToken);
        return (result.Audio, result.MimeType);
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (audio, _) = await OpenAISpeechRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        yield return new AudioSpeechStreamDelta { Audio = Convert.ToBase64String(audio) };
        yield return new AudioSpeechStreamDone();
    }

    private async Task<(byte[] Audio, string MimeType, JsonElement ProviderResponse,
        Dictionary<string, string> Headers, DateTime Timestamp)> SendSunbirdSpeechAsync(
        Dictionary<string, object?> payload,
        string? requestedFormat,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        var timestamp = DateTime.UtcNow;
        using var request = new HttpRequestMessage(HttpMethod.Post, SpeechEndpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"SunbirdAI speech failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(responseBytes)}");

        var headers = response.GetHeaders();
        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true)
        {
            var responseInfo = JsonSerializer.SerializeToElement(new
            {
                statusCode = (int)response.StatusCode,
                contentType,
                contentLength = responseBytes.LongLength
            });
            return (responseBytes, contentType ?? GetSunbirdAudioMimeType(requestedFormat), responseInfo, headers, timestamp);
        }

        using var document = JsonDocument.Parse(responseBytes);
        var root = document.RootElement.Clone();
        var audioUrl = root.TryGetProperty("audio_url", out var urlElement) && urlElement.ValueKind == JsonValueKind.String
            ? urlElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(audioUrl))
            throw new InvalidOperationException($"SunbirdAI speech response did not include audio_url: {Encoding.UTF8.GetString(responseBytes)}");

        using var audioResponse = await _client.GetAsync(audioUrl, cancellationToken);
        var audio = await audioResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!audioResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"SunbirdAI speech audio download failed ({(int)audioResponse.StatusCode}).");

        var mimeType = audioResponse.Content.Headers.ContentType?.MediaType
            ?? GetSunbirdAudioMimeType(requestedFormat);
        return (audio, mimeType, root, headers, timestamp);
    }

    private Dictionary<string, object?> GetSunbirdObject(Dictionary<string, JsonElement>? providerOptions)
        => providerOptions is not null && providerOptions.TryGetValue(GetIdentifier(), out var options)
            ? SunbirdJsonObject(options)
            : [];

    private static Dictionary<string, object?> SunbirdJsonObject(Dictionary<string, JsonElement>? properties)
        => properties?.ToDictionary(property => property.Key, property => (object?)property.Value.Clone(), StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, object?> SunbirdJsonObject(JsonElement element)
        => element.ValueKind == JsonValueKind.Object
            ? element.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone(), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

    private static void SetSunbirdValue(Dictionary<string, object?> payload, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            payload[name] = value;
    }

    private static string GetSunbirdLeafModel(string model)
        => model.StartsWith("sunbirdai/", StringComparison.OrdinalIgnoreCase) ? model["sunbirdai/".Length..] : model;

    private static string GetSunbirdAudioFormat(string? requestedFormat, string mimeType)
        => !string.IsNullOrWhiteSpace(requestedFormat) ? requestedFormat : mimeType.ToLowerInvariant() switch
        {
            "audio/wav" or "audio/x-wav" => "wav",
            "audio/ogg" => "ogg",
            "audio/opus" => "opus",
            "audio/flac" => "flac",
            "audio/aac" => "aac",
            _ => "mp3"
        };

    private static string GetSunbirdAudioMimeType(string? format)
        => format?.Trim().ToLowerInvariant() switch
        {
            "wav" => "audio/wav",
            "ogg" => "audio/ogg",
            "opus" => "audio/opus",
            "flac" => "audio/flac",
            "aac" => "audio/aac",
            _ => "audio/mpeg"
        };
}
