using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.LMRouter;

public partial class LMRouterProvider
{
    private const string SpeechEndpoint = "openai/v1/audio/speech";

    private static readonly JsonSerializerOptions LMRouterSpeechJsonOptions = new(JsonSerializerDefaults.Web)
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
        if (string.IsNullOrWhiteSpace(request.Voice))
            throw new ArgumentException("Voice is required.", nameof(request));

        var payload = GetLMRouterSpeechProviderOptions(request.ProviderOptions);
        payload["model"] = request.Model;
        payload["input"] = request.Text;
        payload["voice"] = request.Voice;
        if (!string.IsNullOrWhiteSpace(request.OutputFormat)) payload["response_format"] = request.OutputFormat;
        if (!string.IsNullOrWhiteSpace(request.Instructions)) payload["instructions"] = request.Instructions;
        if (request.Speed.HasValue) payload["speed"] = request.Speed.Value;
        if (!string.IsNullOrWhiteSpace(request.Language)) payload["language"] = request.Language;

        var result = await SendLMRouterSpeechAsync(payload, request.OutputFormat, cancellationToken);
        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(result.Audio),
                MimeType = result.MimeType,
                Format = ResolveLMRouterSpeechFormat(request.OutputFormat, result.MimeType)
            },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new
            {
                content_type = result.MimeType,
                content_length = result.Audio.LongLength
            }),
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = result.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier())
            },
            Request = new SpeechRequestItem { Body = payload }
        };
    }

    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Model))
            throw new ArgumentException("Model is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Input))
            throw new ArgumentException("Input is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Voice))
            throw new ArgumentException("Voice is required.", nameof(options));

        ApplyAuthHeader();
        return _client.OpenAICompatibleSpeechRequestAsync(options, SpeechEndpoint, cancellationToken);
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

    private async Task<LMRouterSpeechResult> SendLMRouterSpeechAsync(
        JsonObject payload,
        string? requestedFormat,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Post, SpeechEndpoint)
        {
            Content = new StringContent(payload.ToJsonString(LMRouterSpeechJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"LMRouter speech request failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(audio)}");

        return new LMRouterSpeechResult(
            audio,
            response.Content.Headers.ContentType?.MediaType ?? ResolveLMRouterSpeechMimeType(requestedFormat),
            response.GetHeaders());
    }

    private JsonObject GetLMRouterSpeechProviderOptions(Dictionary<string, JsonElement>? providerOptions)
    {
        if (providerOptions?.TryGetValue(GetIdentifier(), out var options) == true && options.ValueKind == JsonValueKind.Object)
            return JsonNode.Parse(options.GetRawText())?.AsObject() ?? new JsonObject();
        return new JsonObject();
    }

    private static string ResolveLMRouterSpeechFormat(string? format, string mimeType)
        => string.IsNullOrWhiteSpace(format)
            ? mimeType switch
            {
                "audio/opus" => "opus",
                "audio/aac" => "aac",
                "audio/flac" => "flac",
                "audio/wav" => "wav",
                "audio/pcm" => "pcm",
                _ => "mp3"
            }
            : format;

    private static string ResolveLMRouterSpeechMimeType(string? format) => format?.Trim().ToLowerInvariant() switch
    {
        "opus" => "audio/opus",
        "aac" => "audio/aac",
        "flac" => "audio/flac",
        "wav" => "audio/wav",
        "pcm" => "audio/pcm",
        _ => "audio/mpeg"
    };

    private sealed record LMRouterSpeechResult(byte[] Audio, string MimeType, IDictionary<string, string> Headers);
}
