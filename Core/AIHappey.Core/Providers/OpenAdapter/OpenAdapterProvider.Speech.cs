using System.Runtime.CompilerServices;
using System.Text;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using System.Text.Json;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.OpenAdapter;

public partial class OpenAdapterProvider
{
    private static readonly JsonSerializerOptions OpenAdapterSpeechJson = new(JsonSerializerDefaults.Web);

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Voice))
            throw new ArgumentException("Voice is required.", nameof(request));

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["input"] = request.Text,
            ["voice"] = request.Voice,
            ["response_format"] = NormalizeSpeechFormat(request.OutputFormat),
            ["speed"] = request.Speed
        };
        MergeProviderOptions(payload, request.ProviderOptions);

        var (audio, mimeType, headers) = await SendSpeechRequestAsync(payload, cancellationToken);
        var format = ResolveSpeechFormat(payload["response_format"]?.ToString(), mimeType);
        var warnings = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(audio),
                MimeType = mimeType,
                Format = format
            },
            Warnings = warnings,
            ProviderMetadata = new()
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(new { responseFormat = format }, OpenAdapterSpeechJson)
            },
            Request = new SpeechRequestItem { Body = payload },
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = headers,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var response = await SpeechRequest(options.ToSpeechRequest(), cancellationToken);
        return response.ToOpenAISpeechAudio();
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var response = await SpeechRequest(options.ToSpeechRequest(), cancellationToken);
        foreach (var streamEvent in response.ToOpenAISpeechStreamEvents())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return streamEvent;
        }
    }

    private async Task<(byte[] Audio, string MimeType, Dictionary<string, string> Headers)> SendSpeechRequestAsync(
        Dictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        var json = JsonSerializer.Serialize(payload, OpenAdapterSpeechJson);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenAdapter speech failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(audio)}");

        var requestedFormat = payload["response_format"]?.ToString();
        var mimeType = response.Content.Headers.ContentType?.MediaType ?? ResolveSpeechMimeType(requestedFormat);
        return (audio, mimeType, response.GetHeaders());
    }

    private void MergeProviderOptions(Dictionary<string, object?> payload, Dictionary<string, JsonElement>? providerOptions)
    {
        if (providerOptions is null ||
            !providerOptions.TryGetValue(GetIdentifier(), out var options) ||
            options.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in options.EnumerateObject())
        {
            if (property.NameEquals("stream"))
                continue;
            payload[property.Name] = property.Value.Clone();
        }
    }

    private static string NormalizeSpeechFormat(string? format)
        => string.IsNullOrWhiteSpace(format) ? "mp3" : format.Trim().ToLowerInvariant();

    private static string ResolveSpeechFormat(string? requestedFormat, string mimeType)
    {
        if (!string.IsNullOrWhiteSpace(requestedFormat))
            return requestedFormat.Trim().ToLowerInvariant();
        return mimeType.ToLowerInvariant() switch
        {
            "audio/wav" or "audio/x-wav" => "wav",
            "audio/ogg" or "audio/opus" => "opus",
            "audio/flac" => "flac",
            "audio/pcm" or "audio/l16" => "pcm",
            _ => "mp3"
        };
    }

    private static string ResolveSpeechMimeType(string? format)
        => NormalizeSpeechFormat(format) switch
        {
            "wav" => "audio/wav",
            "opus" => "audio/ogg",
            "flac" => "audio/flac",
            "pcm" => "audio/pcm",
            _ => "audio/mpeg"
        };
}
