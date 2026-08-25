using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Router9;

public partial class Router9Provider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        var (audio, mediaType) = DecodeRouter9Data(request.Audio, request.MediaType);
        var language = GetAdditionalString(request.ProviderOptions, "language");
        var result = await TranscribeRouter9Async(audio, mediaType, request.Model, language, cancellationToken);
        var text = RequireRouter9TranscriptionText(result.Root);
        return new TranscriptionResponse
        {
            Text = text,
            Language = language,
            Segments = [],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root),
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = result.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = result.Root
            },
            Request = new TranscriptionRequestItem { Body = "multipart/form-data" }
        };
    }

    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAITranscriptionRequest();
        await using var stream = options.File.OpenReadStream();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);
        var result = await TranscribeRouter9Async(memory.ToArray(), options.File.ContentType ?? "audio/mpeg",
            options.Model, options.Language, cancellationToken);
        return new OpenAITranscriptionResponse { Text = RequireRouter9TranscriptionText(result.Root) };
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

    private async Task<Router9JsonResult> TranscribeRouter9Async(byte[] audio, string mediaType, string model,
        string? language, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audio);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
        form.Add(file, "file", ResolveRouter9AudioFileName(mediaType));
        if (!string.IsNullOrWhiteSpace(language)) form.Add(new StringContent(language), "language");
        if (!string.IsNullOrWhiteSpace(model)) form.Add(new StringContent(model), "model");
        using var response = await _client.PostAsync("v1/audio/transcribe", form, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Router9 transcription failed ({(int)response.StatusCode}): {json}");
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement.Clone();
            if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
                throw new InvalidOperationException($"Router9 transcription failed: {json}");
            return new Router9JsonResult(root, GetRouter9Headers(response));
        }
        catch (JsonException ex) { throw new InvalidOperationException("Router9 transcription returned invalid JSON.", ex); }
    }

    private static string RequireRouter9TranscriptionText(JsonElement root)
        => GetRouter9String(root, "result", "fullText")
           ?? throw new InvalidOperationException("Router9 transcription response did not contain result.fullText.");

    private static string? GetAdditionalString(Dictionary<string, JsonElement>? properties, string name)
        => properties is not null && properties.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static string ResolveRouter9AudioFileName(string mediaType)
        => mediaType.ToLowerInvariant() switch
        {
            var value when value.Contains("wav") => "audio.wav",
            var value when value.Contains("webm") => "audio.webm",
            var value when value.Contains("mp4") || value.Contains("m4a") => "audio.m4a",
            var value when value.Contains("mpeg") || value.Contains("mp3") => "audio.mp3",
            _ => "audio.bin"
        };
}
