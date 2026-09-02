using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.AIHubMix;

public partial class AIHubMixProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MediaType);

        var audio = DecodeAIHubMixAudio(request.Audio);
        ApplyAuthHeader();
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audio);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(request.MediaType);
        form.Add(file, "file", ResolveAIHubMixAudioFileName(request.MediaType));
        form.Add(new StringContent(request.Model), "model");
        form.Add(new StringContent("verbose_json"), "response_format");
        AddAIHubMixProviderOptions(form, request.ProviderOptions, "file", "model", "response_format", "stream");

        using var response = await _client.PostAsync("v1/audio/transcriptions", form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"AIHubMix transcription request failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var segments = root.TryGetProperty("segments", out var segmentArray) && segmentArray.ValueKind == JsonValueKind.Array
            ? segmentArray.EnumerateArray().Select(segment => new TranscriptionSegment
            {
                Text = GetAIHubMixString(segment, "text") ?? string.Empty,
                StartSecond = GetAIHubMixSingle(segment, "start"),
                EndSecond = GetAIHubMixSingle(segment, "end")
            }).ToList()
            : [];

        return new TranscriptionResponse
        {
            Text = GetAIHubMixString(root, "text") ?? string.Join(" ", segments.Select(segment => segment.Text)),
            Language = GetAIHubMixString(root, "language"),
            DurationInSeconds = GetAIHubMixNullableSingle(root, "duration"),
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

    public IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(StreamingTranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return _client.OpenAICompatibleVercelTranscriptionStreamingAsync(
            request, GetIdentifier(), cancellationToken: cancellationToken);
    }

    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAITranscriptionRequest();
        ApplyAuthHeader();
        return _client.OpenAICompatibleTranscriptionRequestAsync(options, cancellationToken: cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAITranscriptionRequest();
        if (!options.Model.Equals("whisper-1", StringComparison.OrdinalIgnoreCase))
        {
            ApplyAuthHeader();
            await foreach (var streamEvent in _client.OpenAICompatibleTranscriptionStreamingAsync(
                options, cancellationToken: cancellationToken))
                yield return streamEvent;
            yield break;
        }

        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(response.Text))
            yield return new OpenAITranscriptionTextDelta { Delta = response.Text };
        yield return new OpenAITranscriptionTextDone { Text = response.Text };
    }

    private static byte[] DecodeAIHubMixAudio(object audio)
    {
        var value = audio is JsonElement element && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : audio?.ToString();
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Audio is required.", nameof(audio));
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = value.IndexOf(',');
            if (comma < 0) throw new ArgumentException("The audio data URL is invalid.", nameof(audio));
            value = value[(comma + 1)..];
        }
        try { return Convert.FromBase64String(value); }
        catch (FormatException exception) { throw new ArgumentException("Audio must be valid base64.", nameof(audio), exception); }
    }

    private static void AddAIHubMixProviderOptions(MultipartFormDataContent form,
        Dictionary<string, JsonElement>? options, params string[] reserved)
    {
        if (options is null) return;
        var reservedNames = reserved.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in options)
        {
            if (reservedNames.Contains(name) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;
            if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                    form.Add(new StringContent(item.ValueKind == JsonValueKind.String ? item.GetString()! : item.GetRawText()), name.EndsWith("[]", StringComparison.Ordinal) ? name : name + "[]");
            }
            else
                form.Add(new StringContent(value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText()), name);
        }
    }

    private static string? GetAIHubMixString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static float GetAIHubMixSingle(JsonElement element, string name) => GetAIHubMixNullableSingle(element, name) ?? 0;

    private static float? GetAIHubMixNullableSingle(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetSingle(out var number) ? number : null;

    private static string ResolveAIHubMixAudioFileName(string mediaType) => "audio" + mediaType.ToLowerInvariant() switch
    {
        "audio/flac" => ".flac", "audio/mpeg" => ".mp3", "audio/mp4" => ".mp4",
        "audio/mpga" => ".mpga", "audio/m4a" => ".m4a", "audio/ogg" => ".ogg",
        "audio/wav" or "audio/x-wav" => ".wav", "audio/webm" => ".webm", _ => ".audio"
    };

}
