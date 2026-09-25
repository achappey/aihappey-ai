using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Noiz;

public partial class NoizProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(NormalizeModelId(request.Model), TranscriptionModel, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Noiz transcription model '{request.Model}' is not supported.");

        var mediaType = request.MediaType?.Split(';', 2)[0].Trim().ToLowerInvariant() switch
        {
            "audio/mpeg" or "audio/mp3" => (Mime: "audio/mpeg", Extension: ".mp3"),
            "audio/wav" or "audio/x-wav" or "audio/wave" => (Mime: "audio/wav", Extension: ".wav"),
            "audio/mp4" or "audio/x-m4a" or "audio/m4a" => (Mime: "audio/mp4", Extension: ".m4a"),
            "audio/ogg" => (Mime: "audio/ogg", Extension: ".ogg"),
            "audio/flac" or "audio/x-flac" => (Mime: "audio/flac", Extension: ".flac"),
            "audio/aac" => (Mime: "audio/aac", Extension: ".aac"),
            "audio/webm" => (Mime: "audio/webm", Extension: ".webm"),
            _ => throw new NotSupportedException($"Noiz does not support transcription media type '{request.MediaType}'.")
        };

        var encoded = request.Audio switch
        {
            JsonElement { ValueKind: JsonValueKind.String } value => value.GetString(),
            _ => request.Audio?.ToString()
        };
        if (string.IsNullOrWhiteSpace(encoded))
            throw new ArgumentException("Audio is required.", nameof(request));
        if (encoded.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var marker = encoded.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
                throw new ArgumentException("Audio data URL must contain base64 data.", nameof(request));
            encoded = encoded[(marker + 8)..];
        }

        var audio = Convert.FromBase64String(encoded);
        if (audio.Length == 0 || audio.Length > 50 * 1024 * 1024)
            throw new ArgumentException("Noiz transcription audio must be nonempty and at most 50 MB.", nameof(request));

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audio);
        file.Headers.ContentType = new MediaTypeHeaderValue(mediaType.Mime);
        form.Add(file, "file", "audio" + mediaType.Extension);
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var language = ReadString(metadata, "language");
        AddIfNotNull(form, "language", language);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "speech-to-text") { Content = form };
        ApplyAuthHeader(httpRequest);
        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Noiz transcription failed ({(int)response.StatusCode}): {json}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (ReadInt(root, "code") is { } code && code != 0 && code != 200)
            throw new InvalidOperationException($"Noiz transcription failed: {json}");
        if (!TryGetPropertyIgnoreCase(root, "data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Noiz transcription response did not contain data.");

        var segments = new List<TranscriptionSegment>();
        if (TryGetPropertyIgnoreCase(data, "segments", out var entries) && entries.ValueKind == JsonValueKind.Array)
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(ReadString(entry, "text")))
                    continue;
                var start = ReadFloat(entry, "start") ?? 0;
                segments.Add(new TranscriptionSegment
                {
                    Text = ReadString(entry, "text")!,
                    StartSecond = start,
                    EndSecond = Math.Max(start, ReadFloat(entry, "end") ?? start)
                });
            }

        return new TranscriptionResponse
        {
            Text = ReadString(data, "transcript") ?? string.Empty,
            Language = ReadString(data, "language"),
            DurationInSeconds = ReadFloat(data, "duration"),
            Segments = segments,
            ProviderMetadata = new Dictionary<string, JsonElement> { [GetIdentifier()] = data.Clone() },
            Response = new ResponseData { Timestamp = DateTime.UtcNow, ModelId = request.Model.ToModelId(GetIdentifier()), Body = root.Clone() }
        };
    }

    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAITranscriptionRequest();
        var format = options.ResolveOpenAITranscriptionResponseFormat();
        var request = await options.ToTranscriptionRequest(options.Model, GetIdentifier(), cancellationToken);
        return (await TranscriptionRequest(request, cancellationToken)).ToOpenAITranscriptionResponse(format);
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var result = await OpenAITranscriptionRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrEmpty(result.Text))
            yield return new OpenAITranscriptionTextDelta { Delta = result.Text };
        yield return new OpenAITranscriptionTextDone { Text = result.Text };
    }

    private static float? ReadFloat(JsonElement element, string property)
        => TryGetPropertyIgnoreCase(element, property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out var number)
            ? number : null;

    private string NormalizeModelId(string? model)
    {
        var local = model?.Trim() ?? string.Empty;
        var prefix = GetIdentifier() + "/";
        return local.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? local[prefix.Length..] : local;
    }
}
