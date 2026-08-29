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

namespace AIHappey.Core.Providers.AICredits;

public partial class AICreditsProvider
{
    private static readonly JsonSerializerOptions AICreditsSpeechJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateAICreditsSpeech(request.Model, request.Text, request.Voice);

        var payload = GetAICreditsOptions(request.ProviderOptions);
        payload["model"] = request.Model;
        payload["input"] = request.Text;
        payload["voice"] = request.Voice;
        if (!string.IsNullOrWhiteSpace(request.OutputFormat)) payload["response_format"] = request.OutputFormat;

        var result = await SendAICreditsSpeechAsync(payload, request.OutputFormat, cancellationToken);
        var warnings = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.Instructions)) warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (request.Speed.HasValue) warnings.Add(new { type = "unsupported", feature = "speed" });
        if (!string.IsNullOrWhiteSpace(request.Language)) warnings.Add(new { type = "unsupported", feature = "language" });

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(result.Audio),
                MimeType = result.MimeType,
                Format = ResolveAICreditsAudioFormat(request.OutputFormat, result.MimeType)
            },
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Metadata),
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = result.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = result.Metadata
            },
            Request = new SpeechRequestItem { Body = payload }
        };
    }

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateAICreditsSpeech(options.Model, options.Input, options.Voice);
        var payload = JsonSerializer.SerializeToNode(options, AICreditsSpeechJsonOptions)?.AsObject()
            ?? throw new InvalidOperationException("Could not serialize the AICredits speech request.");
        var result = await SendAICreditsSpeechAsync(payload, options.ResponseFormat, cancellationToken);
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

    private async Task<AICreditsSpeechResult> SendAICreditsSpeechAsync(
        JsonObject payload,
        string? requestedFormat,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(payload.ToJsonString(AICreditsSpeechJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"AICredits speech request failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(audio)}");
        if (audio.Length == 0)
            throw new InvalidOperationException("AICredits speech request returned empty audio.");

        var mimeType = response.Content.Headers.ContentType?.MediaType ?? ResolveAICreditsAudioMimeType(requestedFormat);
        var headers = response.GetHeaders();
        var metadata = JsonSerializer.SerializeToElement(new
        {
            status_code = (int)response.StatusCode,
            content_type = mimeType,
            content_length = audio.LongLength,
            headers
        });
        return new AICreditsSpeechResult(audio, mimeType, headers, metadata);
    }

    private static void ValidateAICreditsSpeech(string model, string input, string? voice)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("Model is required.", nameof(model));
        if (string.IsNullOrWhiteSpace(input)) throw new ArgumentException("Input is required.", nameof(input));
        if (input.Length > 4096) throw new ArgumentException("AICredits speech input cannot exceed 4,096 characters.", nameof(input));
        if (string.IsNullOrWhiteSpace(voice)) throw new ArgumentException("Voice is required.", nameof(voice));
    }

    private static string ResolveAICreditsAudioMimeType(string? format) => format?.ToLowerInvariant() switch
    {
        "opus" => "audio/opus",
        "aac" => "audio/aac",
        "flac" => "audio/flac",
        "wav" => "audio/wav",
        _ => "audio/mpeg"
    };

    private static string ResolveAICreditsAudioFormat(string? requestedFormat, string mimeType)
        => !string.IsNullOrWhiteSpace(requestedFormat) ? requestedFormat : mimeType.ToLowerInvariant() switch
        {
            "audio/mpeg" => "mp3",
            "audio/opus" => "opus",
            "audio/aac" => "aac",
            "audio/flac" => "flac",
            "audio/wav" or "audio/x-wav" => "wav",
            _ => mimeType.Split('/').Last()
        };

    private sealed record AICreditsSpeechResult(byte[] Audio, string MimeType, IDictionary<string, string> Headers, JsonElement Metadata);
}
