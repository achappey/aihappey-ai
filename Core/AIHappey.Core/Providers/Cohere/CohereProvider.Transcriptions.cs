using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.MCP.Media;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Cohere;

public partial class CohereProvider
{
    private const string CohereTranscriptionEndpoint = "v2/audio/transcriptions";


    public IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(StreamingTranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    
    private async Task<TranscriptionResponse> TranscriptionRequestInternal(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.MediaType))
            throw new ArgumentException("MediaType is required.", nameof(request));

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        if (metadata.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined))
            throw new JsonException();

        string? language = null;
        float? temperature = null;
        if (metadata.ValueKind == JsonValueKind.Object)
        {
            if (metadata.TryGetProperty("language", out var languageElement))
                language = languageElement.Deserialize<string>(JsonSerializerOptions.Web);

            if (metadata.TryGetProperty("temperature", out var temperatureElement))
                temperature = temperatureElement.Deserialize<float?>(JsonSerializerOptions.Web);
        }

        if (string.IsNullOrWhiteSpace(language))
            throw new ArgumentException("providerOptions.cohere.language is required for Cohere transcription.", nameof(request));

        var audioString = request.Audio switch
        {
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
            _ => request.Audio?.ToString()
        };

        if (string.IsNullOrWhiteSpace(audioString))
            throw new ArgumentException("Audio is required.", nameof(request));

        if (MediaContentHelpers.TryParseDataUrl(audioString, out _, out var parsedBase64))
            audioString = parsedBase64;

        var bytes = Convert.FromBase64String(audioString);
        var fileName = "audio" + request.MediaType.GetAudioExtension();
        var now = DateTime.UtcNow;

        using var form = new MultipartFormDataContent();

        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);

        form.Add(new StringContent(request.Model), "model");
        form.Add(new StringContent(language), "language");

        if (temperature is not null)
            form.Add(new StringContent(temperature.Value.ToString(CultureInfo.InvariantCulture)), "temperature");

        if (metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in metadata.EnumerateObject())
            {
                if (property.Name is "language" or "temperature" or "model" or "file")
                    continue;

                form.Add(new StringContent(GetMultipartValue(property.Value)), property.Name);
            }
        }

        form.Add(file, "file", fileName);

        using var resp = await _client.PostAsync(CohereTranscriptionEndpoint, form, cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Cohere transcription failed ({(int)resp.StatusCode}): {json}");

        return ConvertCohereTranscriptionResponse(json, request.Model, language, now);
    }

    private static string GetMultipartValue(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Number or JsonValueKind.True
                or JsonValueKind.False or JsonValueKind.Null => value.GetRawText(),
            _ => string.Empty
        };

    private static TranscriptionResponse ConvertCohereTranscriptionResponse(
        string json,
        string model,
        string language,
        DateTime timestamp)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var text = root.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String
            ? textEl.GetString() ?? string.Empty
            : string.Empty;

        return new TranscriptionResponse
        {
            Text = text,
            Language = language,
            Segments = [],
            Response = new()
            {
                Timestamp = timestamp,
                ModelId = model,
                Body = json
            }
        };
    }
}
