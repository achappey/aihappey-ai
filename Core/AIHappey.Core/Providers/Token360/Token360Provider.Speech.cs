using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Token360;

public partial class Token360Provider
{
    private static readonly JsonSerializerOptions Token360SpeechJson = new(JsonSerializerDefaults.Web)
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

        var providerOptions = request.ProviderOptions?.TryGetValue(GetIdentifier(), out var options) == true
            ? options
            : default(JsonElement?);
        var payload = BuildToken360SpeechPayload(
            request.Model,
            request.Text,
            request.OutputFormat,
            request.Speed,
            request.Language,
            providerOptions);
        var now = DateTime.UtcNow;
        var result = await SendToken360SpeechAsync(payload, cancellationToken);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(result.Audio),
                MimeType = result.MimeType,
                Format = result.Format
            },
            Request = new SpeechRequestItem { Body = payload },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Response),
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = result.Model.ToModelId(GetIdentifier()),
                Headers = result.Headers,
                Body = result.Response
            }
        };
    }

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Model))
            throw new ArgumentException("Model is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Input))
            throw new ArgumentException("Input is required.", nameof(options));

        var payload = BuildToken360SpeechPayload(
            options.Model,
            options.Input,
            options.ResponseFormat,
            options.Speed,
            null,
            options.AdditionalProperties is null
                ? null
                : JsonSerializer.SerializeToElement(options.AdditionalProperties, Token360SpeechJson));
        var result = await SendToken360SpeechAsync(payload, cancellationToken);
        return (result.Audio, result.MimeType);
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (audio, _) = await OpenAISpeechRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (audio.Length > 0)
            yield return new AudioSpeechStreamDelta { Audio = Convert.ToBase64String(audio) };
        yield return new AudioSpeechStreamDone();
    }

    private static Dictionary<string, object?> BuildToken360SpeechPayload(
        string model,
        string input,
        string? format,
        float? speed,
        string? language,
        JsonElement? providerOptions)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        MergeToken360JsonOptions(payload, providerOptions);

        payload["model"] = NormalizeToken360Model(model);
        payload["input"] = input;
        payload["response_format"] = ReadToken360String(payload, "response_format") ?? "url";

        if (!string.IsNullOrWhiteSpace(format))
            payload["audio_format"] = format;
        if (speed is not null)
            payload["speed"] = speed.Value;
        if (!string.IsNullOrWhiteSpace(language))
            payload["language_type"] = language;

        return payload;
    }

    private async Task<Token360SpeechResult> SendToken360SpeechAsync(
        Dictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        var json = JsonSerializer.Serialize(payload, Token360SpeechJson);
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Token360 speech failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var audioValue = TryGetToken360String(root, "audio")
            ?? throw new InvalidOperationException("Token360 speech response returned no audio.");
        var format = TryGetToken360String(root, "format")
            ?? ReadToken360String(payload, "audio_format")
            ?? "mp3";

        byte[] audio;
        string mimeType;
        if (Uri.TryCreate(audioValue, UriKind.Absolute, out var audioUri)
            && audioUri.Scheme is "http" or "https")
        {
            using var download = await _client.GetAsync(audioUri, cancellationToken);
            audio = await download.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!download.IsSuccessStatusCode)
                throw new InvalidOperationException($"Token360 speech download failed ({(int)download.StatusCode}): {Encoding.UTF8.GetString(audio)}");
            mimeType = download.Content.Headers.ContentType?.MediaType ?? ResolveToken360AudioMimeType(format);
        }
        else
        {
            var base64 = audioValue;
            mimeType = ResolveToken360AudioMimeType(format);
            if (audioValue.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = audioValue.IndexOf(',');
                if (comma < 0)
                    throw new InvalidOperationException("Token360 speech returned an invalid audio data URL.");
                var semicolon = audioValue.IndexOf(';');
                if (semicolon > 5)
                    mimeType = audioValue[5..semicolon];
                base64 = audioValue[(comma + 1)..];
            }

            try
            {
                audio = Convert.FromBase64String(base64);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException("Token360 speech returned audio that is neither a URL nor valid Base64.", ex);
            }
        }

        return new Token360SpeechResult(
            audio,
            mimeType,
            format,
            TryGetToken360String(root, "model") ?? ReadToken360String(payload, "model") ?? GetIdentifier(),
            root,
            response.GetHeaders());
    }

    private static void MergeToken360JsonOptions(Dictionary<string, object?> target, JsonElement? options)
    {
        if (options is not { ValueKind: JsonValueKind.Object } objectOptions)
            return;

        foreach (var property in objectOptions.EnumerateObject())
            target[property.Name] = property.Value.Clone();
    }

    private static string NormalizeToken360Model(string model)
    {
        const string prefix = "token360/";
        return model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? model[prefix.Length..] : model;
    }

    private static string? ReadToken360String(IReadOnlyDictionary<string, object?> values, string name)
    {
        if (!values.TryGetValue(name, out var value) || value is null)
            return null;
        return value is JsonElement { ValueKind: JsonValueKind.String } element ? element.GetString() : value.ToString();
    }

    private static string? TryGetToken360String(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string ResolveToken360AudioMimeType(string? format)
        => format?.Trim().ToLowerInvariant() switch
        {
            "mp3" => "audio/mpeg",
            "wav" => "audio/wav",
            "ogg" or "ogg_opus" => "audio/ogg",
            "opus" => "audio/opus",
            "aac" => "audio/aac",
            "flac" => "audio/flac",
            "pcm" => "audio/pcm",
            var mime when mime?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true => mime,
            _ => MediaTypeNames.Application.Octet
        };

    private sealed record Token360SpeechResult(
        byte[] Audio,
        string MimeType,
        string Format,
        string Model,
        JsonElement Response,
        Dictionary<string, string> Headers);
}
