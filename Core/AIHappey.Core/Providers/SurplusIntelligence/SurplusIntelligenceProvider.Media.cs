using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.MCP.Media;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.SurplusIntelligence;

public sealed partial class SurplusIntelligenceProvider
{
    private async Task<ImageResponse> CreateImageAsync(
        ImageRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);
        if (request.ProviderOptions?.Count > 0)
            throw new NotSupportedException("Surplus Intelligence image generation does not document provider options.");
        if (request.Files?.Any() == true || request.Mask is not null)
            throw new NotSupportedException("Surplus Intelligence image generation does not document image edits.");

        var response = await OpenAIImageGenerationRequestAsync(new OpenAIImageGenerationRequest
        {
            Model = GetNativeModel(request.Model),
            Prompt = request.Prompt,
            N = request.N ?? 1,
            Size = request.Size
        }, cancellationToken);

        var images = response.Data?
            .Select(image => !string.IsNullOrWhiteSpace(image.Url)
                ? image.Url
                : !string.IsNullOrWhiteSpace(image.B64Json)
                    ? $"data:image/png;base64,{image.B64Json}"
                    : null)
            .Where(static image => image is not null)
            .Cast<string>()
            .ToArray() ?? [];
        if (images.Length == 0)
            throw new InvalidOperationException("Surplus Intelligence image response contained no image data.");

        var warnings = new List<object>();
        if (request.AspectRatio is not null)
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });
        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new()
            {
                Timestamp = response.Created > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(response.Created).UtcDateTime
                    : DateTime.UtcNow,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private async Task<SpeechResponse> CreateSpeechAsync(
        SpeechRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
        if (request.ProviderOptions?.Count > 0)
            throw new NotSupportedException("Surplus Intelligence speech does not document provider options.");

        var format = string.IsNullOrWhiteSpace(request.OutputFormat) ? "mp3" : request.OutputFormat;
        var (audio, mimeType) = await OpenAISpeechRequestAsync(new AudioSpeechRequest
        {
            Model = GetNativeModel(request.Model),
            Input = request.Text,
            Voice = request.Voice ?? "alloy",
            ResponseFormat = format,
            Instructions = request.Instructions,
            Speed = request.Speed
        }, cancellationToken);

        var warnings = new List<object>();
        if (request.Language is not null)
            warnings.Add(new { type = "unsupported", feature = "language" });

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(audio),
                MimeType = mimeType,
                Format = format
            },
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private async Task<TranscriptionResponse> CreateTranscriptionAsync(
        TranscriptionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MediaType);
        if (request.ProviderOptions?.Count > 0)
            throw new NotSupportedException("Surplus Intelligence transcription does not document provider options.");

        var audio = request.Audio switch
        {
            JsonElement value when value.ValueKind == JsonValueKind.String => value.GetString(),
            string value => value,
            _ => request.Audio?.ToString()
        };
        if (string.IsNullOrWhiteSpace(audio))
            throw new ArgumentException("Audio is required.", nameof(request));
        if (MediaContentHelpers.TryParseDataUrl(audio, out var dataUrlMediaType, out var parsedBase64))
        {
            if (!string.Equals(dataUrlMediaType, request.MediaType, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Audio data URL media type must match mediaType.", nameof(request));
            audio = parsedBase64;
        }

        byte[] audioBytes;
        try
        {
            audioBytes = Convert.FromBase64String(audio);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Audio must be raw base64 or a base64 data URL.", nameof(request), exception);
        }

        ApplyAuthHeader();
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audioBytes);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(request.MediaType);
        form.Add(file, "file", "audio" + request.MediaType.GetAudioExtension());
        form.Add(new StringContent(GetNativeModel(request.Model)), "model");
        form.Add(new StringContent("verbose_json"), "response_format");

        using var response = await _client.PostAsync("v1/audio/transcriptions", form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Surplus Intelligence transcription failed ({(int)response.StatusCode}): {raw}",
                null,
                response.StatusCode);

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var segments = new List<TranscriptionSegment>();
        if (root.TryGetProperty("segments", out var segmentArray) && segmentArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var segment in segmentArray.EnumerateArray())
            {
                segments.Add(new()
                {
                    Text = ReadMediaString(segment, "text") ?? string.Empty,
                    StartSecond = ReadMediaNumber(segment, "start"),
                    EndSecond = ReadMediaNumber(segment, "end")
                });
            }
        }

        return new TranscriptionResponse
        {
            Text = ReadMediaString(root, "text") ?? string.Join(" ", segments.Select(static segment => segment.Text)),
            Language = ReadMediaString(root, "language"),
            DurationInSeconds = ReadMediaOptionalNumber(root, "duration"),
            Segments = segments,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Request = new TranscriptionRequestItem { Body = "multipart/form-data" },
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = root
            }
        };
    }

    private static string? ReadMediaString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private static float ReadMediaNumber(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetSingle(out var number)
                ? number
                : 0f;

    private static float? ReadMediaOptionalNumber(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetSingle(out var number)
                ? number
                : null;
}
