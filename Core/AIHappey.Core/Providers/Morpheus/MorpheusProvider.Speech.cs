using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Morpheus;

public partial class MorpheusProvider
{
    private static readonly JsonSerializerOptions MorpheusSpeechJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

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


    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());

        if (metadata.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object))
            throw new JsonException("Morpheus speech provider options must be a JSON object.");

        var providerOptions = metadata.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(metadata.GetRawText()) as JsonObject
                ?? throw new JsonException("Morpheus speech provider options must be a JSON object.")
            : new JsonObject();

        var voice = !string.IsNullOrWhiteSpace(request.Voice)
            ? request.Voice.Trim()
            : !string.IsNullOrWhiteSpace(ReadStringOption(metadata, "voice"))
                ? ReadStringOption(metadata, "voice")!.Trim()
                : "af_alloy";

        var outputFormat = !string.IsNullOrWhiteSpace(request.OutputFormat)
            ? request.OutputFormat.Trim().ToLowerInvariant()
            : !string.IsNullOrWhiteSpace(ReadStringOption(metadata, "response_format"))
                ? ReadStringOption(metadata, "response_format")!.Trim().ToLowerInvariant()
                : "mp3";

        var speed = request.Speed
            ?? ReadFloatOption(metadata, "speed")
            ?? 1f;

        var rawSessionId = ReadStringOption(metadata, "session_id");
        var sessionId = !string.IsNullOrWhiteSpace(rawSessionId)
            ? rawSessionId.Trim()
            : null;

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        providerOptions["model"] = request.Model;
        providerOptions["input"] = request.Text;
        providerOptions["voice"] = voice;
        providerOptions["response_format"] = outputFormat;
        providerOptions["speed"] = speed;
        providerOptions["session_id"] = sessionId;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(providerOptions, MorpheusSpeechJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/*"));

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException($"Morpheus speech request failed ({(int)resp.StatusCode}): {err}");
        }

        var contentType = resp.Content.Headers.ContentType?.MediaType;

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(bytes),
                MimeType = contentType ?? MapToAudioMimeType(outputFormat),
                Format = outputFormat
            },
            Warnings = warnings,
            Request = new()
            {
                Body = providerOptions.ToObjectDictionary()
            },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private static string? ReadStringOption(JsonElement metadata, string name)
        => TryGetOption(metadata, name, out var value)
            ? value.Deserialize<string>(MorpheusSpeechJson)
            : null;

    private static float? ReadFloatOption(JsonElement metadata, string name)
        => TryGetOption(metadata, name, out var value)
            ? value.Deserialize<float?>(MorpheusSpeechJson)
            : null;

    private static bool TryGetOption(JsonElement metadata, string name, out JsonElement value)
    {
        value = default;
        if (metadata.ValueKind != JsonValueKind.Object)
            return false;

        var found = false;
        foreach (var property in metadata.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                continue;

            value = property.Value;
            found = true;
        }

        return found;
    }

    private static string MapToAudioMimeType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "application/octet-stream";

        return value.Trim().ToLowerInvariant() switch
        {
            "mp3" => "audio/mpeg",
            "opus" => "audio/opus",
            "aac" => "audio/aac",
            "flac" => "audio/flac",
            "wav" => "audio/wav",
            "pcm" => "audio/pcm",
            _ => "application/octet-stream"
        };
    }
}
