using AIHappey.Core.AI;
using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.SambaNova;

public partial class SambaNovaProvider
{

    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return _client.OpenAICompatibleTranscriptionRequestAsync(options, cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return _client.OpenAICompatibleTranscriptionStreamingAsync(options, cancellationToken: cancellationToken);
    }

    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        EnsureMetadataObject(metadata);

        var bytes = Convert.FromBase64String(request.Audio.ToString()!);

        using var form = new MultipartFormDataContent();
        AddAdditionalMetadataFields(form, metadata);

        // file (required)
        var fileName = "audio" + request.MediaType.GetAudioExtension();
        var file = new ByteArrayContent(bytes);

        if (!string.IsNullOrWhiteSpace(request.MediaType))
            file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);

        form.Add(file, "file", fileName);

        // required
        form.Add(new StringContent(request.Model), "model");

        // optional
        var language = ReadMetadataString(metadata, "language");
        if (!string.IsNullOrWhiteSpace(language))
            form.Add(new StringContent(language), "language");

        var prompt = ReadMetadataString(metadata, "prompt");
        if (!string.IsNullOrWhiteSpace(prompt))
            form.Add(new StringContent(prompt), "prompt");

        // default: json (matches your pipeline)
        if (!HasMetadataProperty(metadata, "response_format"))
            form.Add(new StringContent("json"), "response_format");

        using var resp = await _client.PostAsync(
            "https://api.sambanova.ai/v1/audio/"
             + (request.Model.EndsWith("/translate") ? "translations" : "transcriptions"),
            form,
            cancellationToken
        );

        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"SambaNova STT failed ({(int)resp.StatusCode}): {json}");

        return ConvertTranscriptionResponse(json, request.Model);
    }

    private static void AddAdditionalMetadataFields(MultipartFormDataContent form, JsonElement metadata)
    {
        if (metadata.ValueKind == JsonValueKind.Undefined)
            return;

        foreach (var property in metadata.EnumerateObject())
        {
            if (property.Name.Equals("file", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("model", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("language", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("prompt", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.GetRawText();
            form.Add(new StringContent(value), property.Name);
        }
    }

    private static bool HasMetadataProperty(JsonElement metadata, string name)
        => metadata.ValueKind == JsonValueKind.Object
            && metadata.EnumerateObject()
                .Any(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string? ReadMetadataString(JsonElement metadata, string name)
    {
        JsonElement? value = null;
        if (metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in metadata.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    value = property.Value;
            }
        }

        return value is null || value.Value.ValueKind == JsonValueKind.Null
            ? null
            : value.Value.Deserialize<string>(JsonSerializerOptions.Web);
    }

    private static void EnsureMetadataObject(JsonElement metadata)
    {
        if (metadata.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Object)
            throw new JsonException("SambaNova provider metadata must be a JSON object.");
    }

    private static TranscriptionResponse ConvertTranscriptionResponse(
        string json,
        string model)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        return new TranscriptionResponse
        {
            Text = root.TryGetProperty("text", out var t)
                ? t.GetString() ?? ""
                : "",

            // SambaNova does not return segments unless streaming/verbose is used
            Segments = [],

            Language = root.TryGetProperty("language", out var lang)
                ? lang.GetString()
                : null,

            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                ModelId = model,
                Body = json
            }
        };
    }

}
