using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using System.Text.Json;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Text;
using System.Text.Json.Serialization;
using AIHappey.Core.Models;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.EdenAI;

public partial class EdenAIProvider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (audio, mimeType, headers, payload) = await RequestEdenAISpeechAsync(
            request.Model,
            request.Text,
            request.Voice,
            request.OutputFormat,
            request.Speed,
            request.Instructions,
            cancellationToken);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(audio),
                MimeType = mimeType,
                Format = ResolveEdenAISpeechFormat(request.OutputFormat, mimeType)
            },
            Warnings = [],
            ProviderMetadata = BuildEdenAIHeaderMetadata(headers),
            Request = new SpeechRequestItem
            {
                Body = payload
            },
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = headers,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }


    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var (audio, mimeType, _, _) = await RequestEdenAISpeechAsync(
            options.Model,
            options.Input,
            options.Voice,
            options.ResponseFormat,
            options.Speed,
            options.Instructions,
            cancellationToken);
        return (audio, mimeType);
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (audio, _) = await OpenAISpeechRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        yield return new AudioSpeechStreamDelta { Audio = Convert.ToBase64String(audio) };
        yield return new AudioSpeechStreamDone();
    }

    private async Task<(byte[] Audio, string MimeType,
        Dictionary<string, string> Headers, Dictionary<string, object?> Payload)> RequestEdenAISpeechAsync(
        string? model,
        string? input,
        string? voice,
        string? responseFormat,
        float? speed,
        string? instructions,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("'model' is a required field", nameof(model));
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("'input' is a required field", nameof(input));
        if (string.IsNullOrWhiteSpace(voice))
            throw new ArgumentException("'voice' is a required field", nameof(voice));

        ApplyAuthHeader();
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["input"] = input,
            ["voice"] = voice,
            ["response_format"] = responseFormat,
            ["speed"] = speed,
            ["instructions"] = instructions
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "v3/audio/speech")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            }), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"EdenAI speech request failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(audio)}");

        return (audio,
            response.Content.Headers.ContentType?.MediaType ?? ResolveEdenAISpeechMimeType(responseFormat),
            response.GetHeaders(),
            payload);
    }

    private Dictionary<string, JsonElement> BuildEdenAIHeaderMetadata(Dictionary<string, string> headers)
    {
        var metadata = new Dictionary<string, JsonElement>
        {
            [GetIdentifier()] = JsonSerializer.SerializeToElement(new
            {
                provider = headers.GetValueOrDefault("x-edenai-provider"),
                cost = headers.GetValueOrDefault("x-edenai-cost")
            })
        };

        if (headers.TryGetValue("x-edenai-cost", out var cost) && decimal.TryParse(cost, out var parsedCost))
            metadata["gateway"] = JsonSerializer.SerializeToElement(new { cost = parsedCost });

        return metadata;
    }

    private static string ResolveEdenAISpeechFormat(string? format, string mimeType)
        => string.IsNullOrWhiteSpace(format)
            ? mimeType switch { "audio/ogg" => "opus", "audio/aac" => "aac", "audio/flac" => "flac", "audio/wav" => "wav", "audio/pcm" => "pcm", _ => "mp3" }
            : format;

    private static string ResolveEdenAISpeechMimeType(string? format)
        => format?.Trim().ToLowerInvariant() switch
        {
            "opus" => "audio/ogg",
            "aac" => "audio/aac",
            "flac" => "audio/flac",
            "wav" => "audio/wav",
            "pcm" => "audio/pcm",
            _ => "audio/mpeg"
        };


}
