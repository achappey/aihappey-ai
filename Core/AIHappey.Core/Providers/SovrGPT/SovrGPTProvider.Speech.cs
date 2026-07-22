using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using System.Text.Json;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.SovrGPT;

public partial class SovrGPTProvider
{
    private static readonly JsonSerializerOptions SovrGPTSpeechJsonOptions = new(JsonSerializerDefaults.Web)
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
        var warnings = new List<object>();
        var payload = CopyProviderOptions(request.GetProviderMetadata<JsonElement>(GetIdentifier()), warnings);
        var (model, modelVoice) = ParseSpeechModelAndVoice(request.Model);

        if (!string.IsNullOrWhiteSpace(request.Voice)
            && !string.IsNullOrWhiteSpace(modelVoice)
            && !string.Equals(request.Voice.Trim(), modelVoice, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });
        }

        // Apply the contract fields last so callers cannot override the selected model or input.
        payload["model"] = model;
        payload["input"] = request.Text;

        var voice = modelVoice ?? request.Voice?.Trim();
        if (!string.IsNullOrWhiteSpace(voice))
            payload["voice"] = voice;

        if (!string.IsNullOrWhiteSpace(request.OutputFormat))
            payload["response_format"] = NormalizeSpeechFormat(request.OutputFormat);

        if (!string.IsNullOrWhiteSpace(request.Language))
            payload["language"] = request.Language.Trim();

        if (request.Speed is not null)
            payload["speed"] = request.Speed.Value;

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            payload["instruction"] = request.Instructions;

        var payloadJson = JsonSerializer.Serialize(payload, SovrGPTSpeechJsonOptions);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(payloadJson, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = Encoding.UTF8.GetString(audio);
            throw new InvalidOperationException(
                $"SovrGPT speech request failed ({(int)response.StatusCode}): {error}");
        }

        var contentType = response.Content.Headers.ContentType?.MediaType;
        var format = ResolveSpeechFormat(payload, contentType);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(audio),
                MimeType = contentType ?? ResolveSpeechMimeType(format),
                Format = format
            },
            Warnings = warnings,
            Request = new SpeechRequestItem { Body = payload },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new ResponseData
            {
                Timestamp = now,
                Headers = response.GetHeaders(),
                ModelId = model.ToModelId(GetIdentifier())
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
        // SovrGPT documents a completed raw-audio response only. Use the shared completed-response adapter.
        => this.SpeechStreamingAsync(options, cancellationToken);

    private static Dictionary<string, object?> CopyProviderOptions(JsonElement options, List<object> warnings)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (options.ValueKind != JsonValueKind.Object)
            return payload;

        foreach (var property in options.EnumerateObject())
        {
            if (property.NameEquals("model") || property.NameEquals("input"))
            {
                warnings.Add(new { type = "ignored", feature = $"providerOptions.{property.Name}" });
                continue;
            }

            payload[property.Name] = property.Value.Clone();
        }

        return payload;
    }

    private static (string Model, string? Voice) ParseSpeechModelAndVoice(string model)
    {
        var value = model.Trim();
        const string providerPrefix = "sovrgpt/";

        if (value.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            value = value[providerPrefix.Length..];

        const string voiceSegment = "/voice/";
        var voiceIndex = value.LastIndexOf(voiceSegment, StringComparison.OrdinalIgnoreCase);

        if (voiceIndex < 1)
            return (value, null);

        var voice = value[(voiceIndex + voiceSegment.Length)..].Trim();
        if (string.IsNullOrWhiteSpace(voice))
            throw new ArgumentException("Speech voice model ids must use '[model]/voice/[voice]'.", nameof(model));

        return (value[..voiceIndex].Trim(), voice);
    }

    private static string ResolveSpeechFormat(
        IReadOnlyDictionary<string, object?> payload,
        string? contentType)
    {
        if (payload.TryGetValue("response_format", out var responseFormat)
            && responseFormat is string value
            && !string.IsNullOrWhiteSpace(value))
        {
            return NormalizeSpeechFormat(value);
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
