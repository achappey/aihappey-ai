using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.MoleAPI;

public partial class MoleAPIProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.MediaType)) throw new ArgumentException("MediaType is required.", nameof(request));

        var base64 = request.Audio is JsonElement element && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : request.Audio?.ToString();
        if (string.IsNullOrWhiteSpace(base64)) throw new ArgumentException("Audio is required.", nameof(request));
        if (base64.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            base64 = base64[(base64.IndexOf(',') + 1)..];

        ApplyAuthHeader();
        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(Convert.FromBase64String(base64));
        content.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);
        form.Add(content, "file", $"audio{GetAudioExtension(request.MediaType)}");
        form.Add(new StringContent(NormalizeProviderModelId(request.Model)), "model");
        form.Add(new StringContent("verbose_json"), "response_format");

        using var response = await _client.PostAsync("v1/audio/transcriptions", form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"MoleAPI transcription request failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        var segments = root.TryGetProperty("segments", out var segmentsElement) && segmentsElement.ValueKind == JsonValueKind.Array
            ? segmentsElement.EnumerateArray().Select(segment => new TranscriptionSegment
            {
                Text = GetString(segment, "text") ?? string.Empty,
                StartSecond = GetSingle(segment, "start"),
                EndSecond = GetSingle(segment, "end")
            }).ToList()
            : [];

        return new TranscriptionResponse
        {
            Text = GetString(root, "text") ?? string.Join(" ", segments.Select(segment => segment.Text)),
            Language = GetString(root, "language"),
            DurationInSeconds = GetNullableSingle(root, "duration"),
            Segments = segments,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root.Clone()),
            Request = new() { Body = "multipart/form-data" },
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = root.Clone()
            }
        };
    }

    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return _client.OpenAICompatibleTranscriptionRequestAsync(options, cancellationToken: cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(response.Text)) yield return new OpenAITranscriptionTextDelta { Delta = response.Text };
        yield return new OpenAITranscriptionTextDone { Text = response.Text };
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static float GetSingle(JsonElement element, string name)
        => GetNullableSingle(element, name) ?? 0f;

    private static float? GetNullableSingle(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out var number) ? number : null;

    private static string GetAudioExtension(string mediaType) => mediaType.ToLowerInvariant() switch
    {
        "audio/mpeg" => ".mp3", "audio/wav" => ".wav", "audio/mp4" => ".mp4", "audio/webm" => ".webm",
        "audio/ogg" => ".ogg", "audio/flac" => ".flac", _ => ".audio"
    };
}
