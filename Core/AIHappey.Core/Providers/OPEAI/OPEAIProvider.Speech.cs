using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.OPEAI;

public partial class OPEAIProvider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text)) throw new ArgumentException("Text is required.", nameof(request));

        var payload = BuildOPEAISpeechPayload(
            request.Model, request.Text, request.OutputFormat, request.Instructions,
            request.Speed, request.Language, GetOPEAIProviderOptions(request.ProviderOptions));
        var result = await SendOPEAISpeechAsync(payload, cancellationToken);
        var format = ReadOPEAIString(payload, "format") ?? request.OutputFormat;

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(result.Audio),
                MimeType = result.MimeType,
                Format = ResolveOPEAIAudioFormat(format, result.MimeType)
            },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Request = new SpeechRequestItem { Body = payload },
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = result.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Model)) throw new ArgumentException("'model' is a required field", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Input)) throw new ArgumentException("'input' is a required field", nameof(options));

        var payload = BuildOPEAISpeechPayload(
            options.Model, options.Input, options.ResponseFormat, options.Instructions,
            options.Speed, null, options.AdditionalProperties);
        var result = await SendOPEAISpeechAsync(payload, cancellationToken);
        return (result.Audio, result.MimeType);
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Model)) throw new ArgumentException("'model' is a required field", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Input)) throw new ArgumentException("'input' is a required field", nameof(options));

        var payload = BuildOPEAISpeechPayload(
            options.Model, options.Input, options.ResponseFormat, options.Instructions,
            options.Speed, null, options.AdditionalProperties);
        payload["stream"] = true;

        ApplyAuthHeader();
        using var request = CreateOPEAIJsonRequest("v1/audio/speech", payload);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"OPE AI speech synthesis failed ({(int)response.StatusCode}): {error}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            yield return new AudioSpeechStreamDelta { Audio = Convert.ToBase64String(buffer, 0, read) };
        }

        yield return new AudioSpeechStreamDone();
    }

    private static JsonObject BuildOPEAISpeechPayload(
        string model,
        string text,
        string? format,
        string? instructions,
        float? speed,
        string? language,
        JsonElement? rawOptions)
    {
        var payload = new JsonObject();
        AddOPEAISpeechMappedOptions(payload, format, instructions, speed, language);
        ApplyOPEAISpeechRawOptions(payload, CreateOPEAIPayload(rawOptions));
        payload["model"] = model;
        payload["text"] = text;
        return payload;
    }

    private static JsonObject BuildOPEAISpeechPayload(
        string model,
        string text,
        string? format,
        string? instructions,
        float? speed,
        string? language,
        Dictionary<string, JsonElement>? rawOptions)
    {
        var payload = new JsonObject();
        AddOPEAISpeechMappedOptions(payload, format, instructions, speed, language);
        ApplyOPEAISpeechRawOptions(payload, CreateOPEAIPayload(rawOptions));
        payload["model"] = model;
        payload["text"] = text;
        return payload;
    }

    private static void AddOPEAISpeechMappedOptions(JsonObject payload, string? format, string? instructions, float? speed, string? language)
    {
        if (!string.IsNullOrWhiteSpace(format)) payload["format"] = format;
        if (!string.IsNullOrWhiteSpace(instructions)) payload["instruct_text"] = instructions;
        if (speed.HasValue) payload["speed"] = speed.Value;
        if (!string.IsNullOrWhiteSpace(language)) payload["language"] = language;
    }

    private static void ApplyOPEAISpeechRawOptions(JsonObject payload, JsonObject rawOptions)
    {
        foreach (var property in rawOptions)
        {
            if (property.Key.Equals("model", StringComparison.OrdinalIgnoreCase)
                || property.Key.Equals("text", StringComparison.OrdinalIgnoreCase)
                || property.Key.Equals("input", StringComparison.OrdinalIgnoreCase)
                || property.Key.Equals("voice", StringComparison.OrdinalIgnoreCase))
                continue;

            payload[property.Key] = property.Value?.DeepClone();
        }
    }

    private async Task<(byte[] Audio, string MimeType, Dictionary<string, string> Headers)> SendOPEAISpeechAsync(
        JsonObject payload,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = CreateOPEAIJsonRequest("v1/audio/speech", payload);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"OPE AI speech synthesis failed ({(int)response.StatusCode}): {error}");
        }

        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (audio.Length == 0) throw new InvalidOperationException("OPE AI speech synthesis returned empty audio.");
        var format = ReadOPEAIString(payload, "format");
        return (audio, ResolveOPEAIAudioMimeType(format, response.Content.Headers.ContentType?.MediaType), response.GetHeaders());
    }

    private static string? ReadOPEAIString(JsonObject payload, string propertyName)
        => payload[propertyName] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

}
