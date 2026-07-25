using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace AIHappey.Core.Providers.ReGraph;

public partial class ReGraphProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.MediaType))
            throw new ArgumentException("MediaType is required.", nameof(request));

        var audio = request.Audio is JsonElement { ValueKind: JsonValueKind.String } element
            ? element.GetString()
            : request.Audio?.ToString();
        if (string.IsNullOrWhiteSpace(audio))
            throw new ArgumentException("Audio is required.", nameof(request));

        if (audio.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var commaIndex = audio.IndexOf(',');
            if (commaIndex < 0)
                throw new ArgumentException("Audio data URL is invalid.", nameof(request));
            audio = audio[(commaIndex + 1)..];
        }

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var language = GetString(metadata, "language");
        var responseFormat = GetString(metadata, "response_format", "responseFormat") ?? "json";
        var prompt = GetString(metadata, "prompt");
        var temperature = GetString(metadata, "temperature");
        var audioBytes = Convert.FromBase64String(audio);

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audioBytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);
        form.Add(file, "file", "audio" + GetAudioExtension(request.MediaType));
        form.Add(new StringContent(request.Model), "model");
        if (!string.IsNullOrWhiteSpace(language)) form.Add(new StringContent(language), "language");
        if (!string.IsNullOrWhiteSpace(responseFormat)) form.Add(new StringContent(responseFormat), "response_format");
        if (!string.IsNullOrWhiteSpace(prompt)) form.Add(new StringContent(prompt), "prompt");
        if (!string.IsNullOrWhiteSpace(temperature)) form.Add(new StringContent(temperature), "temperature");

        ApplyAuthHeader();
        using var response = await _client.PostAsync("v1/audio/transcriptions", form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"ReGraph transcription failed ({(int)response.StatusCode}): {raw}");

        return ParseTranscription(raw, request.Model, language, responseFormat, response.GetHeaders());
    }


    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        return OpenAITranscriptionRequestCoreAsync(options, cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);
        if (!string.IsNullOrWhiteSpace(response.Text))
            yield return new OpenAITranscriptionTextDelta { Delta = response.Text };
        yield return new OpenAITranscriptionTextDone { Text = response.Text };
    }

    private async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestCoreAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ValidateOpenAITranscriptionRequest();
        var responseFormat = options.ResolveOpenAITranscriptionResponseFormat();
        var request = await options.ToTranscriptionRequest(options.Model, GetIdentifier(), cancellationToken);
        var response = await TranscriptionRequest(request, cancellationToken);
        return response.ToOpenAITranscriptionResponse(responseFormat);
    }

    private static TranscriptionResponse ParseTranscription(string raw, string model, string? requestedLanguage, string responseFormat, Dictionary<string, string> headers)
    {
        if (responseFormat is "text" or "srt" or "vtt")
        {
            return new TranscriptionResponse
            {
                Text = raw,
                Language = requestedLanguage,
                ProviderMetadata = "regraph".CreatePrimitiveProviderMetadata(),
                Response = new() { Timestamp = DateTime.UtcNow, Headers = headers, ModelId = model.ToModelId("regraph"), Body = raw }
            };
        }

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        return new TranscriptionResponse
        {
            Text = root.TryGetProperty("text", out var text) ? text.GetString() ?? string.Empty : string.Empty,
            Language = root.TryGetProperty("language", out var language) ? language.GetString() : requestedLanguage,
            DurationInSeconds = root.TryGetProperty("duration", out var duration) && duration.TryGetSingle(out var value) ? value : null,
            ProviderMetadata = "regraph".CreatePrimitiveProviderMetadata(root.Clone()),
            Response = new() { Timestamp = DateTime.UtcNow, Headers = headers, ModelId = model.ToModelId("regraph"), Body = root.Clone() }
        };
    }

    private static string? GetString(JsonElement? metadata, params string[] names)
    {
        if (metadata is not { ValueKind: JsonValueKind.Object } value)
            return null;
        foreach (var name in names)
            if (value.TryGetProperty(name, out var property))
                return property.ToString();
        return null;
    }

    private static string GetAudioExtension(string mediaType)
        => mediaType.ToLowerInvariant() switch
        {
            "audio/mpeg" => ".mp3",
            "audio/mp4" => ".m4a",
            "audio/wav" or "audio/x-wav" => ".wav",
            "audio/webm" => ".webm",
            _ => ".audio"
        };

}
