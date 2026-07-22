using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Cortecs;

public partial class CortecsProvider
{
    private static readonly JsonSerializerOptions CortecsSpeechJsonOptions = new(JsonSerializerDefaults.Web)
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

        var now = DateTime.UtcNow;
        var providerOptions = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var payload = CopyProviderOptions(providerOptions);
        var voice = request.Voice?.Trim() ?? GetStringOption(providerOptions, "voice");

        if (string.IsNullOrWhiteSpace(voice))
        {
            throw new ArgumentException(
                "Voice is required. Provide SpeechRequest.Voice or providerOptions.cortecs.voice.",
                nameof(request));
        }

        // Contract fields always win over raw provider metadata.
        payload["model"] = request.Model;
        payload["input"] = request.Text;
        payload["voice"] = voice;

        if (!string.IsNullOrWhiteSpace(request.OutputFormat))
            payload["response_format"] = request.OutputFormat.Trim();

        if (request.Speed is not null)
            payload["speed"] = request.Speed.Value;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, CortecsSpeechJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Cortecs speech request failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(audio)}");
        }

        var format = ResolveSpeechFormat(payload, response.Content.Headers.ContentType?.MediaType);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(audio),
                MimeType = response.Content.Headers.ContentType?.MediaType ?? ResolveSpeechMimeType(format),
                Format = format
            },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Request = new SpeechRequestItem { Body = payload },
            Response = new ResponseData
            {
                Timestamp = now,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return _client.OpenAICompatibleSpeechRequestAsync(
            options,
            cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return this.SpeechStreamingAsync(options, cancellationToken);
    }

    private static Dictionary<string, object?> CopyProviderOptions(JsonElement providerOptions)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (providerOptions.ValueKind != JsonValueKind.Object)
            return payload;

        foreach (var property in providerOptions.EnumerateObject())
            payload[property.Name] = property.Value.Clone();

        return payload;
    }

    private static string? GetStringOption(JsonElement options, string name)
        => options.ValueKind == JsonValueKind.Object
           && options.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    private static string ResolveSpeechFormat(
        IReadOnlyDictionary<string, object?> payload,
        string? contentType)
    {
        if (payload.TryGetValue("response_format", out var value))
        {
            var responseFormat = value switch
            {
                string text => text,
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(responseFormat))
                return NormalizeSpeechFormat(responseFormat);
        }

        return contentType?.ToLowerInvariant() switch
        {
            "audio/mpeg" => "mp3",
            "audio/wav" or "audio/x-wav" => "wav",
            "audio/flac" => "flac",
            "audio/ogg" => "ogg",
            "audio/opus" => "opus",
            "audio/aac" => "aac",
            _ => "audio"
        };
    }

    private static string NormalizeSpeechFormat(string format)
        => format.Trim().ToLowerInvariant() switch
        {
            "mpeg" => "mp3",
            "wave" => "wav",
            var value => value
        };

    private static string ResolveSpeechMimeType(string format)
        => format switch
        {
            "mp3" => "audio/mpeg",
            "wav" => "audio/wav",
            "flac" => "audio/flac",
            "ogg" => "audio/ogg",
            "opus" => "audio/opus",
            "aac" => "audio/aac",
            _ => "application/octet-stream"
        };
}

