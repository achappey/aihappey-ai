using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.MCP.Media;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.RegoloAI;

public partial class RegoloAIProvider
{


    public IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(StreamingTranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }


    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.MediaType))
            throw new ArgumentException("MediaType is required.", nameof(request));

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
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        if (metadata.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Object)
            throw new JsonException("RegoloAI transcription provider options must be a JSON object.");

        var now = DateTime.UtcNow;

        using var form = new MultipartFormDataContent();

        var fileName = "audio" + request.MediaType.GetAudioExtension();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);

        form.Add(file, "file", fileName);
        form.Add(new StringContent(request.Model), "model");

        if (metadata.ValueKind == JsonValueKind.Object)
        {
            var language = GetStringOption(metadata, "language");
            if (!string.IsNullOrWhiteSpace(language))
                form.Add(new StringContent(language), "language");

            foreach (var property in metadata.EnumerateObject())
            {
                if (string.Equals(property.Name, "language", StringComparison.OrdinalIgnoreCase))
                    continue;

                form.Add(new StringContent(ToMultipartValue(property.Value)), property.Name);
            }
        }

        using var resp = await _client.PostAsync("v1/audio/transcriptions", form, cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Regolo STT failed ({(int)resp.StatusCode}): {json}");

        return ConvertRegoloTranscriptionResponse(json, request.Model, now);
    }

    private static TranscriptionResponse ConvertRegoloTranscriptionResponse(string json, string model, DateTime timestamp)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var text = root.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String
            ? textEl.GetString() ?? string.Empty
            : string.Empty;

        var language = root.TryGetProperty("language", out var languageEl) && languageEl.ValueKind == JsonValueKind.String
            ? languageEl.GetString()
            : null;

        return new TranscriptionResponse
        {
            Text = text,
            Language = language,
            Segments = [],
            Response = new ResponseData
            {
                Timestamp = timestamp,
                ModelId = root.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String
                    ? modelEl.GetString() ?? model
                    : model,
                Body = json
            }
        };
    }

    private static string? GetStringOption(JsonElement metadata, string name)
    {
        JsonElement value = default;
        var found = false;
        foreach (var property in metadata.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                continue;

            value = property.Value;
            found = true;
        }

        return found ? value.Deserialize<string>(JsonSerializerOptions.Web) : null;
    }

    private static string ToMultipartValue(JsonElement value)
        => value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.GetRawText();
}
