using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using System.Text.Json;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Text;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.Featherless;

public partial class FeatherlessProvider
{
    private static readonly JsonSerializerOptions FeatherlessSpeechJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        ApplyAuthHeader();

        var started = DateTime.UtcNow;
        var payload = CreateFeatherlessSpeechPayload(request);
        var responseFormat = ReadFeatherlessSpeechString(payload, "response_format") ?? "mp3";
        var delivery = ReadFeatherlessSpeechString(payload, "delivery") ?? "bulk";
        var encoding = ReadFeatherlessSpeechString(payload, "encoding") ?? "binary";

        if (delivery.Equals("stream", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Featherless SSE delivery is not supported by the non-streaming speech API.", nameof(request));

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, FeatherlessSpeechJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = Encoding.UTF8.GetString(responseBytes);
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? $"Featherless speech request failed ({(int)response.StatusCode} {response.ReasonPhrase})."
                    : $"Featherless speech request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {error}");
        }

        var audio = DecodeFeatherlessSpeechAudio(responseBytes, delivery, encoding, out var envelopeFormat);
        var actualFormat = envelopeFormat ?? responseFormat;
        var mimeType = response.Content.Headers.ContentType?.MediaType;

        if (string.Equals(mimeType, MediaTypeNames.Application.Json, StringComparison.OrdinalIgnoreCase))
            mimeType = null;

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(audio),
                MimeType = mimeType ?? OpenAI.OpenAIProvider.MapToAudioMimeType(actualFormat),
                Format = actualFormat
            },
            Warnings = string.IsNullOrWhiteSpace(request.Language)
                ? []
                : [new { type = "unsupported", feature = "language" }],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Request = new SpeechRequestItem { Body = payload },
            Response = new ResponseData
            {
                Timestamp = started,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = new
                {
                    statusCode = (int)response.StatusCode,
                    contentType = response.Content.Headers.ContentType?.MediaType,
                    contentLength = responseBytes.LongLength
                }
            }
        };
    }

    private Dictionary<string, object?> CreateFeatherlessSpeechPayload(SpeechRequest request)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        var providerOptions = request.GetProviderMetadata<JsonElement>(GetIdentifier());

        if (providerOptions.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in providerOptions.EnumerateObject())
                payload[property.Name] = property.Value.Clone();
        }

        payload["model"] = request.Model;
        payload["input"] = request.Text;

        SetFeatherlessSpeechValue(payload, "voice", request.Voice);
        SetFeatherlessSpeechValue(payload, "response_format", request.OutputFormat);
        SetFeatherlessSpeechValue(payload, "speed", request.Speed);
        SetFeatherlessSpeechValue(payload, "instructions", request.Instructions);

        payload.TryAdd("response_format", "mp3");
        payload.TryAdd("delivery", "bulk");
        payload.TryAdd("encoding", "binary");

        return payload;
    }

    private static void SetFeatherlessSpeechValue(Dictionary<string, object?> payload, string name, object? value)
    {
        if (value is not null && (value is not string text || !string.IsNullOrWhiteSpace(text)))
            payload[name] = value;
    }

    private static string? ReadFeatherlessSpeechString(Dictionary<string, object?> payload, string name)
    {
        if (!payload.TryGetValue(name, out var value) || value is null)
            return null;

        return value switch
        {
            string text => text.Trim().ToLowerInvariant(),
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString()?.Trim().ToLowerInvariant(),
            _ => null
        };
    }

    private static byte[] DecodeFeatherlessSpeechAudio(
        byte[] responseBytes,
        string delivery,
        string encoding,
        out string? format)
    {
        format = null;

        if (delivery.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            using var document = JsonDocument.Parse(responseBytes);
            var root = document.RootElement;
            if (!root.TryGetProperty("audio", out var audioElement)
                || audioElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(audioElement.GetString()))
                throw new InvalidOperationException("Featherless speech response did not contain base64 audio.");

            if (root.TryGetProperty("format", out var formatElement) && formatElement.ValueKind == JsonValueKind.String)
                format = formatElement.GetString()?.Trim().ToLowerInvariant();

            return DecodeFeatherlessBase64(audioElement.GetString()!);
        }

        if (encoding.Equals("base64", StringComparison.OrdinalIgnoreCase))
            return DecodeFeatherlessBase64(Encoding.UTF8.GetString(responseBytes).Trim());

        return responseBytes;
    }

    private static byte[] DecodeFeatherlessBase64(string base64)
    {
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("Featherless speech response contained invalid base64 audio.", exception);
        }
    }



    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return _client.OpenAICompatibleSpeechRequestAsync(options, cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return _client.OpenAICompatibleStreamingSpeechAsync(options, cancellationToken: cancellationToken);
    }
}
