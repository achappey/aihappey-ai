using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.Depaza;

public partial class DepazaProvider
{
    private static readonly JsonSerializerOptions DepazaTranscriptionJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var now = DateTime.UtcNow;
        var payload = BuildDepazaTranscriptionPayload(request);
        var requestBody = JsonSerializer.Serialize(payload, DepazaTranscriptionJsonOptions);

        using var content = new StringContent(
            requestBody,
            Encoding.UTF8,
            MediaTypeNames.Application.Json);

        using var response = await _client.PostAsync("v1/transcribe", content, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"Depaza transcription request failed ({(int)response.StatusCode})."
                : $"Depaza transcription request failed ({(int)response.StatusCode}): {raw}");

        return ConvertDepazaTranscriptionResponse(
            raw,
            request.Model,
            now,
            requestBody,
            response.GetHeaders());
    }

    private static Dictionary<string, object?> BuildDepazaTranscriptionPayload(TranscriptionRequest request)
    {
        var payload = new Dictionary<string, object?>
        {
            ["audio"] = ReadDepazaAudioString(request),
            ["filename"] = ResolveDepazaFilename(request)
        };

        MergeDepazaProviderOptions(payload, request);

        return payload;
    }

    private static void MergeDepazaProviderOptions(
        Dictionary<string, object?> payload,
        TranscriptionRequest request)
    {
        if (request.ProviderOptions is null)
            return;

        if (!request.ProviderOptions.TryGetValue("depaza", out var providerOptions))
            return;

        if (providerOptions.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in providerOptions.EnumerateObject())
        {
            if (property.NameEquals("audio"))
                continue;

            payload[property.Name] = property.Value.Clone();
        }
    }

    private static string ReadDepazaAudioString(TranscriptionRequest request)
    {
        var audioString = request.Audio switch
        {
            JsonElement { ValueKind: JsonValueKind.String } el => el.GetString(),
            _ => request.Audio?.ToString()
        };

        if (string.IsNullOrWhiteSpace(audioString))
            throw new ArgumentException("Audio is required.", nameof(request));

        return audioString;
    }

    private static string ResolveDepazaFilename(TranscriptionRequest request)
    {
        var metadata = request.GetProviderMetadata<JsonElement>(nameof(Depaza).ToLowerInvariant());

        return "audio" + ResolveDepazaAudioExtension(request.MediaType);
    }

    private static string ResolveDepazaAudioExtension(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
            return ".webm";

        var normalized = mediaType.Split(';', 2)[0].Trim().ToLowerInvariant();

        return normalized switch
        {
            "audio/mpeg" => ".mp3",
            "audio/mp3" => ".mp3",
            "audio/wav" => ".wav",
            "audio/x-wav" => ".wav",
            "audio/wave" => ".wav",
            "audio/flac" => ".flac",
            "audio/x-flac" => ".flac",
            "audio/mp4" => ".m4a",
            "audio/x-m4a" => ".m4a",
            "audio/ogg" => ".ogg",
            "audio/opus" => ".opus",
            "audio/webm" => ".webm",
            "audio/aac" => ".aac",
            "audio/3gpp" => ".3gp",
            "audio/3gpp2" => ".3g2",
            _ => ".webm"
        };
    }

    private static TranscriptionResponse ConvertDepazaTranscriptionResponse(
        string raw,
        string model,
        DateTime timestamp,
        string requestBody,
        IDictionary<string, string> headers)
    {
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();

        var text = TryReadDepazaString(root, "text") ?? string.Empty;

        return new TranscriptionResponse
        {
            Text = text,
            Segments = [],
            Warnings = [],
            ProviderMetadata = nameof(Depaza).ToLowerInvariant()
                .CreatePrimitiveProviderMetadata(root),
            Response = new ResponseData
            {
                Timestamp = timestamp,
                Headers = headers,
                ModelId = model.ToModelId(nameof(Depaza).ToLowerInvariant()),
                Body = root
            },
            Request = new TranscriptionRequestItem
            {
                Body = requestBody
            }
        };
    }

    private static string? TryReadDepazaString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                continue;

            return property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.True => bool.TrueString.ToLowerInvariant(),
                JsonValueKind.False => bool.FalseString.ToLowerInvariant(),
                _ => null
            };
        }

        return null;
    }

}
