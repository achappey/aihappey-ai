using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.ApiAirforce;

public partial class ApiAirforceProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var (audio, mediaType) = DecodeApiAirforceAudio(request.Audio, request.MediaType);
        var providerOptions = TryGetProviderOptions(request.ProviderOptions, GetIdentifier());
        var fields = providerOptions is null
            ? new Dictionary<string, object?>()
            : providerOptions.Value.EnumerateObject().ToDictionary(
                property => property.Name,
                property => (object?)property.Value.Clone(),
                StringComparer.OrdinalIgnoreCase);

        var result = await SendApiAirforceTranscriptionAsync(
            NormalizeModelId(request.Model), audio, mediaType, fields, cancellationToken);

        var root = result.Root;
        var words = root.TryGetProperty("words", out var wordsElement) && wordsElement.ValueKind == JsonValueKind.Array
            ? wordsElement.EnumerateArray()
                .Where(word => string.Equals(TryGetString(word, "type"), "word", StringComparison.OrdinalIgnoreCase))
                .Select(word => new TranscriptionSegment
                {
                    Text = TryGetString(word, "text") ?? string.Empty,
                    StartSecond = TryGetSingle(word, "start"),
                    EndSecond = TryGetSingle(word, "end")
                }).ToArray()
            : [];

        return new TranscriptionResponse
        {
            Text = TryGetString(root, "text") ?? string.Empty,
            Language = TryGetString(root, "language_code"),
            DurationInSeconds = root.TryGetProperty("audio_duration_secs", out var duration) && duration.TryGetSingle(out var seconds)
                ? seconds
                : null,
            Segments = words,
            Warnings = [],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root.Clone()),
            Request = new TranscriptionRequestItem { Body = result.RequestBody },
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Headers = result.Headers,
                Body = root.Clone()
            }
        };
    }

    private async Task<ApiAirforceTranscriptionResult> SendApiAirforceTranscriptionAsync(
        string model,
        byte[] audio,
        string mediaType,
        IReadOnlyDictionary<string, object?> fields,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audio);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
        form.Add(file, "file", ResolveApiAirforceAudioFileName(mediaType));
        form.Add(new StringContent(model, Encoding.UTF8), "model");

        var requestFields = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["model"] = model };
        foreach (var field in fields)
        {
            if (field.Value is null || field.Key.Equals("file", StringComparison.OrdinalIgnoreCase) || field.Key.Equals("model", StringComparison.OrdinalIgnoreCase))
                continue;

            var name = field.Key.Equals("language", StringComparison.OrdinalIgnoreCase) ? "language_code" : field.Key;
            var value = field.Value is JsonElement json
                ? json.ValueKind == JsonValueKind.String ? json.GetString() : json.GetRawText()
                : Convert.ToString(field.Value, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(value))
                continue;

            form.Add(new StringContent(value, Encoding.UTF8), name);
            requestFields[name] = field.Value;
        }

        using var response = await _client.PostAsync("v1/audio/transcriptions", form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"ApiAirforce transcription failed ({(int)response.StatusCode} {response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        return new ApiAirforceTranscriptionResult(
            document.RootElement.Clone(),
            response.GetHeaders(),
            JsonSerializer.Serialize(requestFields, ApiAirforceMediaJsonOptions));
    }

    private static (byte[] Audio, string MediaType) DecodeApiAirforceAudio(object audio, string? mediaType)
    {
        ArgumentNullException.ThrowIfNull(audio);
        var value = audio is JsonElement json && json.ValueKind == JsonValueKind.String ? json.GetString() : audio.ToString();
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Audio is required.", nameof(audio));

        var resolvedMediaType = string.IsNullOrWhiteSpace(mediaType) ? "audio/mpeg" : mediaType;
        var comma = value.IndexOf(',');
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0)
        {
            var header = value[5..comma];
            resolvedMediaType = header.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? resolvedMediaType;
            value = value[(comma + 1)..];
        }

        return (Convert.FromBase64String(value), resolvedMediaType);
    }

    private static string ResolveApiAirforceAudioFileName(string mediaType) => mediaType.ToLowerInvariant() switch
    {
        "audio/wav" or "audio/x-wav" => "audio.wav",
        "audio/mp4" or "audio/m4a" or "audio/x-m4a" => "audio.m4a",
        "audio/flac" => "audio.flac",
        "audio/ogg" => "audio.ogg",
        "audio/webm" => "audio.webm",
        _ => "audio.mp3"
    };

    private static float TryGetSingle(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.TryGetSingle(out var result) ? result : 0;

    private sealed record ApiAirforceTranscriptionResult(
        JsonElement Root,
        Dictionary<string, string> Headers,
        string RequestBody);
}
