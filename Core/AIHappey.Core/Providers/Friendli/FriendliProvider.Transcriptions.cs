using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Friendli;

public partial class FriendliProvider
{
    private const string TranscriptionsEndpoint = "v1/audio/transcriptions";

    private static readonly HashSet<string> ProtectedTranscriptionFields =
        new(StringComparer.OrdinalIgnoreCase) { "file", "model", "stream" };

    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MediaType);

        var encodedAudio = request.Audio switch
        {
            JsonElement { ValueKind: JsonValueKind.String } value => value.GetString(),
            _ => request.Audio?.ToString()
        };

        if (string.IsNullOrWhiteSpace(encodedAudio))
            throw new ArgumentException("Audio is required.", nameof(request));

        encodedAudio = StripDataUrlPrefix(encodedAudio);

        byte[] audio;
        try
        {
            audio = Convert.FromBase64String(encodedAudio);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Audio must contain valid base64 data.", nameof(request), exception);
        }

        if (audio.Length == 0)
            throw new ArgumentException("Audio is required.", nameof(request));

        var providerOptions = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var requestBody = BuildRequestTelemetry(request.Model, request.MediaType, audio.Length, providerOptions);

        using var form = new MultipartFormDataContent();
        using var file = new ByteArrayContent(audio);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(request.MediaType);
        form.Add(file, "file", "audio" + GetAudioExtension(request.MediaType));
        form.Add(new StringContent(request.Model, Encoding.UTF8), "model");
        AddRawProviderOptions(form, providerOptions);

        ApplyAuthHeader();
        var timestamp = DateTime.UtcNow;
        using var response = await _client.PostAsync(TranscriptionsEndpoint, form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Friendli transcription failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        if (!root.TryGetProperty("text", out var textElement) || textElement.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"Friendli transcription response did not include text: {raw}");

        var language = ReadProviderOptionString(providerOptions, "language");
        var duration = ReadAudioDurationSeconds(root);

        return new TranscriptionResponse
        {
            Text = textElement.GetString()!,
            Language = language,
            DurationInSeconds = duration,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = root.Clone()
            },
            Request = new TranscriptionRequestItem { Body = requestBody },
            Response = new ResponseData
            {
                Timestamp = timestamp,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = root.Clone()
            }
        };
    }

    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return _client.OpenAICompatibleTranscriptionRequestAsync(
            options,
            TranscriptionsEndpoint,
            cancellationToken);
    }

    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return _client.OpenAICompatibleTranscriptionStreamingAsync(
            options,
            TranscriptionsEndpoint,
            cancellationToken);
    }

    private static void AddRawProviderOptions(MultipartFormDataContent form, JsonElement options)
    {
        if (options.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in options.EnumerateObject())
        {
            if (ProtectedTranscriptionFields.Contains(property.Name))
                continue;

            AddMultipartValue(form, property.Name, property.Value);
        }
    }

    private static void AddMultipartValue(MultipartFormDataContent form, string name, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                    AddMultipartValue(form, $"{name}[{property.Name}]", property.Value);
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                    AddMultipartValue(form, $"{name}[]", item);
                break;
            case JsonValueKind.String:
                AddMultipartString(form, name, value.GetString());
                break;
            case JsonValueKind.Number:
                AddMultipartString(form, name, value.GetRawText());
                break;
            case JsonValueKind.True:
                AddMultipartString(form, name, "true");
                break;
            case JsonValueKind.False:
                AddMultipartString(form, name, "false");
                break;
        }
    }

    private static void AddMultipartString(MultipartFormDataContent form, string name, string? value)
    {
        if (value is not null)
            form.Add(new StringContent(value, Encoding.UTF8), name);
    }

    private static string StripDataUrlPrefix(string audio)
    {
        audio = audio.Trim();
        if (!audio.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return audio;

        var comma = audio.IndexOf(',');
        if (comma < 0)
            throw new ArgumentException("Audio data URL is invalid.", nameof(audio));

        return audio[(comma + 1)..];
    }

    private static string BuildRequestTelemetry(
        string model,
        string mediaType,
        int audioByteLength,
        JsonElement providerOptions)
        => JsonSerializer.Serialize(new
        {
            model,
            file = new { mediaType, byteLength = audioByteLength },
            providerOptions = providerOptions.ValueKind == JsonValueKind.Object ? providerOptions : (JsonElement?)null
        }, JsonSerializerOptions.Web);

    private static string? ReadProviderOptionString(JsonElement options, string name)
        => options.ValueKind == JsonValueKind.Object
           && options.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static float? ReadAudioDurationSeconds(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in new[] { "input_audio_length_ms", "processed_audio_length_ms" })
        {
            if (usage.TryGetProperty(name, out var value) && value.TryGetDouble(out var milliseconds))
                return (float)(milliseconds / 1000d);
        }

        return null;
    }

    private static string GetAudioExtension(string mediaType)
        => mediaType.Split(';', 2)[0].Trim().ToLowerInvariant() switch
        {
            "audio/mpeg" => ".mp3",
            "audio/mp4" or "audio/x-m4a" => ".m4a",
            "audio/wav" or "audio/x-wav" => ".wav",
            "audio/flac" => ".flac",
            "audio/ogg" => ".ogg",
            "audio/webm" => ".webm",
            _ => ".audio"
        };
}
