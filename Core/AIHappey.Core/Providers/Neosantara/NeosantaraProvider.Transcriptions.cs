using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using System.Text.Json;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.Neosantara;

public partial class NeosantaraProvider
{
    private const int NeosantaraMaxAudioBytes = 25 * 1024 * 1024;
    private static readonly HashSet<string> NeosantaraTranscriptionReserved =
        new(["file", "model", "language", "prompt", "response_format", "temperature"], StringComparer.OrdinalIgnoreCase);

    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.MediaType))
            throw new ArgumentException("MediaType is required.", nameof(request));

        var audioValue = request.Audio is JsonElement { ValueKind: JsonValueKind.String } element
            ? element.GetString()
            : request.Audio?.ToString();
        if (string.IsNullOrWhiteSpace(audioValue))
            throw new ArgumentException("Audio is required.", nameof(request));
        var bytes = Convert.FromBase64String(audioValue.RemoveDataUrlPrefix());
        ValidateNeosantaraAudio(bytes.Length, request.MediaType);

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        using var form = new MultipartFormDataContent();
        AddNeosantaraMetadataFormFields(form, metadata, NeosantaraTranscriptionReserved);
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(request.MediaType);
        form.Add(file, "file", "audio" + request.MediaType.GetAudioExtension());
        form.Add(new StringContent(request.Model), "model");
        form.Add(new StringContent("verbose_json"), "response_format");

        ApplyAuthHeader();
        using var response = await _client.PostAsync("v1/audio/transcriptions", form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Neosantara transcription failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var segments = ReadNeosantaraSegments(root);
        return new TranscriptionResponse
        {
            Text = ReadNeosantaraString(root, "text") ?? string.Empty,
            Language = ReadNeosantaraString(root, "language"),
            DurationInSeconds = ReadNeosantaraFloat(root, "duration"),
            Segments = segments,
            Warnings = [],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = root
            }
        };
    }



    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return _client.OpenAICompatibleTranscriptionRequestAsync(options, "v1/audio/transcriptions", cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);
        if (!string.IsNullOrEmpty(response.Text))
            yield return new OpenAITranscriptionTextDelta { Delta = response.Text };
        yield return new OpenAITranscriptionTextDone { Text = response.Text };
    }

    private static void AddNeosantaraMetadataFormFields(MultipartFormDataContent form, JsonElement metadata, IReadOnlySet<string> reserved)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return;
        foreach (var property in metadata.EnumerateObject())
        {
            if (reserved.Contains(property.Name))
                continue;
            var value = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.GetRawText();
            if (value is not null)
                form.Add(new StringContent(value), property.Name);
        }
    }

    private static void ValidateNeosantaraAudio(int length, string mediaType)
    {
        if (length > NeosantaraMaxAudioBytes)
            throw new ArgumentOutOfRangeException(nameof(length), "Neosantara transcription files must not exceed 25 MB.");
        var normalized = mediaType.Split(';')[0].Trim().ToLowerInvariant();
        if (normalized is not ("audio/mpeg" or "audio/mp3" or "audio/mp4" or "video/mp4" or "audio/x-m4a" or "audio/m4a" or "audio/wav" or "audio/wave" or "audio/x-wav" or "audio/webm" or "video/webm" or "audio/mpga"))
            throw new NotSupportedException($"Neosantara transcription does not support media type '{mediaType}'.");
    }

    private static List<TranscriptionSegment> ReadNeosantaraSegments(JsonElement root)
    {
        var result = new List<TranscriptionSegment>();
        if (!root.TryGetProperty("segments", out var segments) || segments.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var segment in segments.EnumerateArray())
            result.Add(new TranscriptionSegment
            {
                Text = ReadNeosantaraString(segment, "text") ?? string.Empty,
                StartSecond = ReadNeosantaraFloat(segment, "start") ?? 0,
                EndSecond = ReadNeosantaraFloat(segment, "end") ?? 0
            });
        return result;
    }

    private static string? ReadNeosantaraString(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static float? ReadNeosantaraFloat(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? (float)value.GetDouble() : null;
}
