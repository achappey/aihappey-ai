using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.NavyAI;

public partial class NavyAIProvider
{
    private static readonly JsonSerializerOptions NavySpeechJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Voice);

        var format = string.IsNullOrWhiteSpace(request.OutputFormat) ? "mp3" : request.OutputFormat;
        var payload = CreateNavySpeechPayload(request.Model, request.Text, request.Voice, format,
            request.Instructions, request.Speed, request.ProviderOptions);
        var result = await SendNavySpeechAsync(payload, format, cancellationToken);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(result.Audio),
                MimeType = result.MimeType,
                Format = format
            },
            Warnings = string.IsNullOrWhiteSpace(request.Language)
                ? []
                : [new { type = "unsupported", feature = "language" }],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Metadata),
            Request = new SpeechRequestItem { Body = payload },
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = result.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = result.Metadata
            }
        };
    }
        
    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Input);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Voice);
        var format = string.IsNullOrWhiteSpace(options.ResponseFormat) ? "mp3" : options.ResponseFormat;
        var result = await SendNavySpeechAsync(CreateNavySpeechPayload(options.Model, options.Input,
            options.Voice, format, options.Instructions, options.Speed, options.AdditionalProperties), format, cancellationToken);
        return (result.Audio, result.MimeType);
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (audio, _) = await OpenAISpeechRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        yield return new AudioSpeechStreamDelta { Audio = Convert.ToBase64String(audio) };
        yield return new AudioSpeechStreamDone();
    }

    private async Task<NavySpeechResult> SendNavySpeechAsync(Dictionary<string, object?> payload,
        string? format, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, NavySpeechJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"NavyAI speech request failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        JsonElement metadata = JsonSerializer.SerializeToElement(new
        {
            contentType = response.Content.Headers.ContentType?.MediaType,
            contentLength = bytes.LongLength
        });
        var mimeType = response.Content.Headers.ContentType?.MediaType ?? ResolveNavySpeechMimeType(format);

        // ElevenLabs with-timestamps returns JSON rather than a raw audio body. Preserve the
        // full response as metadata and decode its audio_base64/audio field for compatibility.
        if (mimeType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            using var document = JsonDocument.Parse(bytes);
            metadata = document.RootElement.Clone();
            var audio = NavyGetString(metadata, "audio_base64") ?? NavyGetString(metadata, "audio");
            if (string.IsNullOrWhiteSpace(audio))
                throw new InvalidOperationException("NavyAI timestamped speech response returned no audio payload.");
            bytes = Convert.FromBase64String(NavyRemoveDataUrlPrefix(audio));
            mimeType = "audio/mpeg";
        }

        return new NavySpeechResult(bytes, mimeType, response.GetHeaders(), metadata);
    }

    private static Dictionary<string, object?> CreateNavySpeechPayload(string model, string input,
        string voice, string? responseFormat, string? instructions, float? speed,
        Dictionary<string, JsonElement>? providerOptions)
    {
        var payload = NavyCopyOptions(providerOptions);
        payload["model"] = model;
        payload["input"] = input;
        payload["voice"] = voice;
        if (!string.IsNullOrWhiteSpace(responseFormat)) payload["response_format"] = responseFormat;
        if (!string.IsNullOrWhiteSpace(instructions)) payload["instructions"] = instructions;
        if (speed is not null) payload["speed"] = speed.Value;
        payload.Remove("stream");
        payload.Remove("stream_format");
        return payload;
    }

    private static string ResolveNavySpeechMimeType(string? format) => format?.Trim().ToLowerInvariant() switch
    {
        "mp3" => "audio/mpeg", "opus" => "audio/opus", "aac" => "audio/aac",
        "flac" => "audio/flac", "wav" => "audio/wav", _ => "application/octet-stream"
    };

    private sealed record NavySpeechResult(byte[] Audio, string MimeType,
        Dictionary<string, string> Headers, JsonElement Metadata);

}
