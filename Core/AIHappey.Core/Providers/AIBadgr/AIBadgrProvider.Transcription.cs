using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.MCP.Media;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.AIBadgr;

public partial class AIBadgrProvider
{
    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return _client.OpenAICompatibleTranscriptionRequestAsync(
            options,
            cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return _client.OpenAICompatibleTranscriptionStreamingAsync(
            options,
            cancellationToken: cancellationToken);
    }

    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.MediaType))
            throw new ArgumentException("MediaType is required.", nameof(request));

        var audio = request.Audio switch
        {
            JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString(),
            _ => request.Audio?.ToString()
        };
        if (string.IsNullOrWhiteSpace(audio))
            throw new ArgumentException("Audio is required.", nameof(request));
        if (MediaContentHelpers.TryParseDataUrl(audio, out _, out var parsedBase64))
            audio = parsedBase64;

        ApplyAuthHeader();

        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(Convert.FromBase64String(audio));
        content.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);
        form.Add(content, "file", "audio" + request.MediaType.GetAudioExtension());
        form.Add(new StringContent(NormalizeProviderModelId(request.Model)), "model");
        form.Add(new StringContent("verbose_json"), "response_format");

        using var response = await _client.PostAsync("v1/audio/transcriptions", form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"AI Badgr transcription request failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        var segments = new List<TranscriptionSegment>();
        if (root.TryGetProperty("segments", out var segmentsElement) && segmentsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var segment in segmentsElement.EnumerateArray())
            {
                segments.Add(new()
                {
                    Text = segment.TryGetProperty("text", out var text1) ? text1.GetString() ?? string.Empty : string.Empty,
                    StartSecond = ReadNumber(segment, "start"),
                    EndSecond = ReadNumber(segment, "end")
                });
            }
        }

        return new TranscriptionResponse
        {
            Text = root.TryGetProperty("text", out var text) ? text.GetString() ?? string.Empty : string.Join(" ", segments.Select(segment => segment.Text)),
            Language = root.TryGetProperty("language", out var language) ? language.GetString() : null,
            DurationInSeconds = root.TryGetProperty("duration", out var duration) && duration.ValueKind == JsonValueKind.Number ? (float)duration.GetDouble() : null,
            Segments = segments,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = root.Clone()
            }
        };
    }

    private static float ReadNumber(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
            ? (float)value.GetDouble()
            : 0f;
}
