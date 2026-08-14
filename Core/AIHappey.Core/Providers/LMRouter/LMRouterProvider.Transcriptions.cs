using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.LMRouter;

public partial class LMRouterProvider
{
    private const string TranscriptionEndpoint = "openai/v1/audio/transcriptions";

    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.MediaType))
            throw new ArgumentException("MediaType is required.", nameof(request));
        if (request.Audio is null)
            throw new ArgumentException("Audio is required.", nameof(request));

        var providerOptions = GetLMRouterTranscriptionProviderOptions(request.ProviderOptions);
        var result = await SendLMRouterTranscriptionAsync(
            request.Model,
            DecodeLMRouterAudio(request.Audio.ToString()),
            request.MediaType,
            providerOptions,
            cancellationToken);

        return CreateLMRouterTranscriptionResponse(result.Root, request.Model, result.Headers, providerOptions);
    }

    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAITranscriptionRequest();
        ApplyAuthHeader();
        return _client.OpenAICompatibleTranscriptionRequestAsync(options, TranscriptionEndpoint, cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(response.Text))
            yield return new OpenAITranscriptionTextDelta { Delta = response.Text };
        yield return new OpenAITranscriptionTextDone { Text = response.Text };
    }

    private async Task<LMRouterTranscriptionResult> SendLMRouterTranscriptionAsync(
        string model,
        byte[] audio,
        string mediaType,
        Dictionary<string, JsonElement> providerOptions,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audio);
        file.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        form.Add(file, "file", "audio" + GetLMRouterAudioExtension(mediaType));
        form.Add(new StringContent(model, Encoding.UTF8), "model");
        form.Add(new StringContent("verbose_json", Encoding.UTF8), "response_format");

        foreach (var (name, value) in providerOptions)
        {
            if (name is "model" or "file" or "response_format" or "stream")
                continue;
            form.Add(new StringContent(LMRouterTranscriptionMultipartValue(value), Encoding.UTF8), name);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, TranscriptionEndpoint) { Content = form };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"LMRouter transcription request failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        return new LMRouterTranscriptionResult(document.RootElement.Clone(), response.GetHeaders());
    }

    private TranscriptionResponse CreateLMRouterTranscriptionResponse(
        JsonElement root,
        string model,
        IDictionary<string, string> headers,
        Dictionary<string, JsonElement> requestOptions)
    {
        var segments = new List<TranscriptionSegment>();
        if (root.TryGetProperty("segments", out var rawSegments) && rawSegments.ValueKind == JsonValueKind.Array)
        {
            foreach (var segment in rawSegments.EnumerateArray())
            {
                var text = ReadLMRouterString(segment, "text");
                if (string.IsNullOrWhiteSpace(text))
                    continue;
                segments.Add(new TranscriptionSegment
                {
                    Text = text,
                    StartSecond = ReadLMRouterSingle(segment, "start") ?? 0,
                    EndSecond = ReadLMRouterSingle(segment, "end") ?? 0
                });
            }
        }

        var textValue = ReadLMRouterString(root, "text") ?? string.Join(" ", segments.Select(segment => segment.Text));
        return new TranscriptionResponse
        {
            Text = textValue,
            Language = ReadLMRouterString(root, "language"),
            DurationInSeconds = ReadLMRouterSingle(root, "duration"),
            Segments = segments,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = headers,
                ModelId = model.ToModelId(GetIdentifier()),
                Body = root
            },
            Request = new TranscriptionRequestItem
            {
                Body = JsonSerializer.Serialize(new { model, response_format = "verbose_json", provider_options = requestOptions })
            }
        };
    }

    private Dictionary<string, JsonElement> GetLMRouterTranscriptionProviderOptions(Dictionary<string, JsonElement>? providerOptions)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (providerOptions?.TryGetValue(GetIdentifier(), out var options) != true || options.ValueKind != JsonValueKind.Object)
            return result;
        foreach (var property in options.EnumerateObject())
            result[property.Name] = property.Value.Clone();
        return result;
    }

    private static byte[] DecodeLMRouterAudio(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Audio is required.", nameof(value));
        var comma = value.IndexOf(',');
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0)
            value = value[(comma + 1)..];
        return Convert.FromBase64String(value.Trim());
    }

    private static string? ReadLMRouterString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static float? ReadLMRouterSingle(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetSingle(out var result) ? result : null;

    private static string LMRouterTranscriptionMultipartValue(JsonElement value)
        => value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();

    private static string GetLMRouterAudioExtension(string mediaType) => mediaType.ToLowerInvariant() switch
    {
        "audio/mpeg" => ".mp3",
        "audio/wav" or "audio/x-wav" => ".wav",
        "audio/ogg" => ".ogg",
        "audio/flac" => ".flac",
        "audio/mp4" or "audio/x-m4a" => ".m4a",
        "audio/webm" => ".webm",
        _ => ".bin"
    };

    private sealed record LMRouterTranscriptionResult(JsonElement Root, IDictionary<string, string> Headers);
}
