using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.LLMAPI;

public partial class LLMAPIProvider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var options = new AudioSpeechRequest
        {
            Model = request.Model,
            Input = request.Text,
            Voice = request.Voice,
            ResponseFormat = request.OutputFormat,
            Speed = request.Speed,
            AdditionalProperties = GetLLMAPIProviderOptions(request.ProviderOptions)
        };

        var result = await SynthesizeLLMAPISpeechAsync(options, cancellationToken);
        var warnings = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.Instructions)) warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (!string.IsNullOrWhiteSpace(request.Language)) warnings.Add(new { type = "unsupported", feature = "language" });

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(result.Audio),
                MimeType = result.MimeType,
                Format = ResolveLLMAPISpeechFormat(request.OutputFormat, result.MimeType)
            },
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Request = new SpeechRequestItem { Body = result.Payload },
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = result.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        var result = await SynthesizeLLMAPISpeechAsync(options, cancellationToken);
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

    private async Task<LLMAPISpeechResult> SynthesizeLLMAPISpeechAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Model)) throw new ArgumentException("'model' is a required field", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Input)) throw new ArgumentException("'input' is a required field", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Voice)) throw new ArgumentException("'voice' is a required field", nameof(options));

        var payload = options.AdditionalProperties is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : options.AdditionalProperties.ToDictionary(x => x.Key, x => (object?)x.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        payload["model"] = options.Model;
        payload["input"] = options.Input;
        payload["voice"] = options.Voice;
        if (!string.IsNullOrWhiteSpace(options.ResponseFormat)) payload["response_format"] = options.ResponseFormat;
        if (options.Speed is not null) payload["speed"] = options.Speed;
        payload.Remove("stream");

        ApplyAuthHeader();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"LLMAPI speech failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(audio)}");

        return new LLMAPISpeechResult(
            audio,
            response.Content.Headers.ContentType?.MediaType ?? ResolveLLMAPISpeechMimeType(options.ResponseFormat),
            response.GetHeaders(),
            payload);
    }

    private Dictionary<string, JsonElement>? GetLLMAPIProviderOptions(Dictionary<string, JsonElement>? providerOptions)
        => providerOptions is not null
            && providerOptions.TryGetValue(GetIdentifier(), out var options)
            && options.ValueKind == JsonValueKind.Object
                ? options.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone(), StringComparer.OrdinalIgnoreCase)
                : null;

    private static string ResolveLLMAPISpeechFormat(string? format, string mimeType)
        => string.IsNullOrWhiteSpace(format)
            ? mimeType switch
            {
                "audio/wav" or "audio/x-wav" => "wav",
                "audio/pcm" => "pcm",
                "audio/ogg" => "opus",
                "audio/aac" => "aac",
                "audio/flac" => "flac",
                _ => "mp3"
            }
            : format;

    private static string ResolveLLMAPISpeechMimeType(string? format)
        => format?.Trim().ToLowerInvariant() switch
        {
            "wav" => "audio/wav",
            "pcm" => "audio/pcm",
            "opus" or "ogg" => "audio/ogg",
            "aac" => "audio/aac",
            "flac" => "audio/flac",
            _ => "audio/mpeg"
        };

    private sealed record LLMAPISpeechResult(
        byte[] Audio,
        string MimeType,
        Dictionary<string, string> Headers,
        Dictionary<string, object?> Payload);
}
