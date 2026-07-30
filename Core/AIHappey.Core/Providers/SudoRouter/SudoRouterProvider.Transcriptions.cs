using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.SudoRouter;

public partial class SudoRouterProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.MediaType))
            throw new ArgumentException("MediaType is required.", nameof(request));
        if (request.Audio is null)
            throw new ArgumentException("Audio is required.", nameof(request));

        var metadata = GetSudoRouterProviderOptions(request.ProviderOptions);
        var response = await SendSudoRouterTranscriptionAsync(
            request.Model,
            GetSudoRouterBase64Bytes(request.Audio.ToString()),
            request.MediaType,
            "audio" + GetSudoRouterAudioExtension(request.MediaType),
            metadata,
            cancellationToken);

        return CreateSudoRouterTranscriptionResponse(response.Root, request.Model, response.Headers, metadata);
    }

    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAITranscriptionRequest();
        var responseFormat = options.ResolveOpenAITranscriptionResponseFormat();
        var request = await options.ToTranscriptionRequest(options.Model, GetIdentifier(), cancellationToken);
        var response = await TranscriptionRequest(request, cancellationToken);
        return response.ToOpenAITranscriptionResponse(responseFormat);
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // SudoRouter documents a completed transcription response rather than SSE events.
        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(response.Text))
            yield return new OpenAITranscriptionTextDelta { Delta = response.Text };
        yield return new OpenAITranscriptionTextDone { Text = response.Text };
    }

    private async Task<SudoRouterJsonResult> SendSudoRouterTranscriptionAsync(
        string model,
        byte[] audio,
        string mediaType,
        string fileName,
        JsonObject metadata,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audio);
        file.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        form.Add(file, "file", fileName);
        form.Add(new StringContent(model, Encoding.UTF8), "model");

        foreach (var property in metadata)
        {
            if (property.Value is null || property.Key.Equals("model", StringComparison.OrdinalIgnoreCase))
                continue;

            form.Add(new StringContent(property.Value.ToJsonString().Trim('"')), property.Key);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/audio/transcriptions") { Content = form };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"SudoRouter transcription request failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        return new SudoRouterJsonResult(document.RootElement.Clone(), response.GetHeaders());
    }

    private TranscriptionResponse CreateSudoRouterTranscriptionResponse(
        JsonElement root,
        string model,
        IDictionary<string, string> headers,
        JsonObject requestMetadata)
    {
        var segments = new List<TranscriptionSegment>();
        if (root.TryGetProperty("segments", out var rawSegments) && rawSegments.ValueKind == JsonValueKind.Array)
        {
            foreach (var segment in rawSegments.EnumerateArray())
            {
                var segmentText = segment.TryGetProperty("text", out var textValue) ? textValue.GetString() : null;
                if (string.IsNullOrWhiteSpace(segmentText))
                    continue;

                segments.Add(new TranscriptionSegment
                {
                    Text = segmentText,
                    StartSecond = segment.TryGetProperty("start", out var start) && start.TryGetSingle(out var startSecond) ? startSecond : 0,
                    EndSecond = segment.TryGetProperty("end", out var end) && end.TryGetSingle(out var endSecond) ? endSecond : 0
                });
            }
        }

        var text = root.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String
            ? textElement.GetString() ?? string.Empty
            : string.Join(" ", segments.Select(static segment => segment.Text));

        return new TranscriptionResponse
        {
            Text = text,
            Language = root.TryGetProperty("language", out var language) && language.ValueKind == JsonValueKind.String ? language.GetString() : null,
            DurationInSeconds = root.TryGetProperty("duration", out var duration) && duration.TryGetSingle(out var seconds) ? seconds : null,
            Segments = segments,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = headers,
                ModelId = model.ToModelId(GetIdentifier()),
                Body = root
            },
            Request = new TranscriptionRequestItem { Body = requestMetadata.ToJsonString() }
        };
    }

    private static byte[] GetSudoRouterBase64Bytes(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Audio is required.", nameof(value));

        return Convert.FromBase64String(NormalizeSudoRouterBase64(value));
    }

    private static string GetSudoRouterAudioExtension(string mediaType)
        => mediaType.ToLowerInvariant() switch
        {
            "audio/mpeg" => ".mp3",
            "audio/wav" => ".wav",
            "audio/ogg" => ".ogg",
            "audio/flac" => ".flac",
            "audio/mp4" => ".m4a",
            _ => ".bin"
        };
}
