using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.EUrouter;

public partial class EUrouterProvider
{
    private const string EUrouterSpeechEndpoint = "v1/audio/speech";

    private static readonly JsonSerializerOptions EUrouterAudioJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateEUrouterSpeech(request.Model, request.Text, request.Speed);

        var payload = GetEUrouterProviderOptions(request.ProviderOptions);
        payload["model"] = request.Model;
        payload["input"] = request.Text;
        if (!string.IsNullOrWhiteSpace(request.Voice)) payload["voice"] = request.Voice;
        if (!string.IsNullOrWhiteSpace(request.OutputFormat)) payload["response_format"] = request.OutputFormat;
        if (!string.IsNullOrWhiteSpace(request.Instructions)) payload["instructions"] = request.Instructions;
        if (request.Speed.HasValue) payload["speed"] = request.Speed.Value;
        payload["stream_format"] = "audio";

        var result = await SendEUrouterSpeechAsync(payload, request.OutputFormat, cancellationToken);
        var format = ResolveEUrouterAudioFormat(request.OutputFormat, result.MimeType);
        var metadata = JsonSerializer.SerializeToElement(new
        {
            status_code = result.StatusCode,
            content_type = result.MimeType,
            content_length = result.Audio.LongLength,
            headers = result.Headers
        });

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
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(metadata),
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = result.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = metadata
            },
            Request = new SpeechRequestItem { Body = payload }
        };
    }

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateEUrouterSpeech(options.Model, options.Input, options.Speed);
        var payload = CreateEUrouterOpenAISpeechPayload(options, "audio");
        var result = await SendEUrouterSpeechAsync(payload, options.ResponseFormat, cancellationToken);
        return (result.Audio, result.MimeType);
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateEUrouterSpeech(options.Model, options.Input, options.Speed);
        var payload = CreateEUrouterOpenAISpeechPayload(options, "sse");

        ApplyAuthHeader();
        using var request = CreateEUrouterJsonRequest(EUrouterSpeechEndpoint, payload, acceptSse: true);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"EUrouter streaming speech request failed ({(int)response.StatusCode}): {error}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var dataLines = new List<string>();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                foreach (var item in ParseEUrouterSpeechEvent(dataLines)) yield return item;
                dataLines.Clear();
            }
            else if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                dataLines.Add(line[5..].TrimStart());
        }

        foreach (var item in ParseEUrouterSpeechEvent(dataLines)) yield return item;
    }

    private async Task<EUrouterSpeechResult> SendEUrouterSpeechAsync(
        JsonObject payload,
        string? requestedFormat,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = CreateEUrouterJsonRequest(EUrouterSpeechEndpoint, payload);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"EUrouter speech request failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(audio)}");
        if (audio.Length == 0)
            throw new InvalidOperationException("EUrouter speech request returned empty audio.");

        return new EUrouterSpeechResult(
            audio,
            response.Content.Headers.ContentType?.MediaType ?? ResolveEUrouterAudioMimeType(requestedFormat),
            response.GetHeaders(),
            (int)response.StatusCode);
    }

    private JsonObject CreateEUrouterOpenAISpeechPayload(AudioSpeechRequest options, string streamFormat)
    {
        var payload = CopyEUrouterProperties(options.AdditionalProperties);
        payload["model"] = options.Model;
        payload["input"] = options.Input;
        if (!string.IsNullOrWhiteSpace(options.Voice)) payload["voice"] = options.Voice;
        if (!string.IsNullOrWhiteSpace(options.ResponseFormat)) payload["response_format"] = options.ResponseFormat;
        if (!string.IsNullOrWhiteSpace(options.Instructions)) payload["instructions"] = options.Instructions;
        if (options.Speed.HasValue) payload["speed"] = options.Speed.Value;
        payload["stream_format"] = streamFormat;
        return payload;
    }

    private static IEnumerable<IAudioSpeechStreamEvent> ParseEUrouterSpeechEvent(List<string> dataLines)
    {
        if (dataLines.Count == 0) yield break;
        var data = string.Join("\n", dataLines).Trim();
        if (string.IsNullOrWhiteSpace(data) || data == "[DONE]") yield break;

        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;
        var type = GetEUrouterString(root, "type");
        if (type == "error")
            throw new InvalidOperationException($"EUrouter streaming speech returned an error: {data}");
        if (type == "speech.audio.delta" && GetEUrouterString(root, "audio") is { Length: > 0 } audio)
            yield return new AudioSpeechStreamDelta { Audio = audio };
        else if (type == "speech.audio.done")
            yield return JsonSerializer.Deserialize<AudioSpeechStreamDone>(data, EUrouterAudioJsonOptions) ?? new AudioSpeechStreamDone();
    }

    private JsonObject GetEUrouterProviderOptions(Dictionary<string, JsonElement>? providerOptions)
    {
        if (providerOptions is null ||
            !providerOptions.TryGetValue(GetIdentifier(), out var options) ||
            options.ValueKind != JsonValueKind.Object)
            return [];
        return JsonNode.Parse(options.GetRawText())?.AsObject() ?? [];
    }

    private static JsonObject CopyEUrouterProperties(Dictionary<string, JsonElement>? properties)
    {
        var payload = new JsonObject();
        if (properties is null) return payload;
        foreach (var (name, value) in properties)
            if (value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                payload[name] = JsonNode.Parse(value.GetRawText());
        return payload;
    }

    private static HttpRequestMessage CreateEUrouterJsonRequest(string endpoint, JsonObject payload, bool acceptSse = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload.ToJsonString(EUrouterAudioJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        if (acceptSse) request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return request;
    }

    private static void ValidateEUrouterSpeech(string model, string input, float? speed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        if (input.Length > 4096) throw new ArgumentException("EUrouter speech input cannot exceed 4,096 characters.", nameof(input));
        if (speed is < 0.25f or > 4f) throw new ArgumentOutOfRangeException(nameof(speed), "EUrouter speech speed must be between 0.25 and 4.0.");
    }

    private static string ResolveEUrouterAudioMimeType(string? format) => format?.Trim().ToLowerInvariant() switch
    {
        "mp3" => "audio/mpeg", "opus" => "audio/opus", "aac" => "audio/aac",
        "flac" => "audio/flac", "wav" => "audio/wav", "pcm" => "audio/pcm",
        _ => "audio/mpeg"
    };

    private static string ResolveEUrouterAudioFormat(string? format, string mimeType)
        => !string.IsNullOrWhiteSpace(format) ? format.Trim().ToLowerInvariant() : mimeType.ToLowerInvariant() switch
        {
            "audio/mpeg" => "mp3", "audio/opus" => "opus", "audio/aac" => "aac",
            "audio/flac" => "flac", "audio/wav" or "audio/x-wav" => "wav", "audio/pcm" => "pcm",
            _ => mimeType.Split('/').Last()
        };

    private sealed record EUrouterSpeechResult(byte[] Audio, string MimeType, IDictionary<string, string> Headers, int StatusCode);
}
