using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.MCP.Media;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Async;

public partial class AsyncProvider
{
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

        var modelId = NormalizeAsyncTranscriptionModel(request.Model);
        var now = DateTime.UtcNow;

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
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);

        form.Add(file, "file", fileName);
        form.Add(new StringContent(modelId, Encoding.UTF8), "model_id");

        var language = TryReadAsyncString(metadata, "language");
        if (!string.IsNullOrWhiteSpace(language))
            form.Add(new StringContent(language.Trim(), Encoding.UTF8), "language");

        AddRawAsyncTranscriptionPassthrough(form, metadata);

        using var resp = await _client.PostAsync("speech_to_text", form, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"asyncAI STT failed ({(int)resp.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var text = root.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String
            ? textEl.GetString() ?? string.Empty
            : string.Empty;

        return new TranscriptionResponse
        {
            Text = text,
            Language = language,
            Warnings = [],
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(new
                {
                    model = modelId,
                    language
                }, JsonSerializerOptions.Web)
            },
            Request = new()
            {
                Body = $"multipart/form-data; file={fileName}; model_id={modelId}"
            },
            Response = new()
            {
                Timestamp = now,
                ModelId = modelId.ToModelId(GetIdentifier()),
                Body = root.Clone()
            }
        };
    }

    private string NormalizeAsyncTranscriptionModel(string model)
    {
        var normalized = NormalizeAsyncModelId(model);
        if (string.Equals(normalized, AsyncTranscriptionModelId, StringComparison.OrdinalIgnoreCase))
            return AsyncTranscriptionModelId;

        throw new NotSupportedException($"Async transcription model '{model}' is not supported. Use '{AsyncTranscriptionModelId}'.");
    }

    private static void AddRawAsyncTranscriptionPassthrough(MultipartFormDataContent form, JsonElement options)
    {
        if (options.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in options.EnumerateObject())
        {
            if (property.NameEquals("file")
                || property.NameEquals("model")
                || property.NameEquals("model_id")
                || property.NameEquals("language"))
                continue;

            var value = ToAsyncMultipartValue(property.Value);
            if (value is null)
                continue;

            form.Add(new StringContent(value, Encoding.UTF8), property.Name);
        }
    }

    private static string? TryReadAsyncString(JsonElement obj, string propertyName)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var prop in obj.EnumerateObject())
        {
            if (!string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                continue;

            return prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Number => prop.Value.GetRawText(),
                _ => null
            };
        }

        return null;
    }

    private static string? ToAsyncMultipartValue(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.Undefined => null,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Array => value.GetRawText(),
            JsonValueKind.Object => value.GetRawText(),
            _ => value.GetRawText()
        };
}
