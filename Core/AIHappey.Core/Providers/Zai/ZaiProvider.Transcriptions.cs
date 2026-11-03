using AIHappey.Core.AI;
using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Zai;

public partial class ZaiProvider 
{
    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var bytes = Convert.FromBase64String(request.Audio.ToString()!);
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        if (metadata.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Object))
            throw new JsonException("Z.AI transcription provider metadata must be an object.");

        using var form = new MultipartFormDataContent();

        // file (required)
        var fileName = "audio" + request.MediaType.GetAudioExtension();
        var file = new ByteArrayContent(bytes);

        if (!string.IsNullOrWhiteSpace(request.MediaType))
            file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);

        form.Add(file, "file", fileName);

        // required
        form.Add(new StringContent(request.Model), "model");

        if (TryGetMetadataProperty(metadata, "prompt", out var prompt))
        {
            var promptText = prompt.Deserialize<string>(JsonSerializerOptions.Web);
            if (!string.IsNullOrWhiteSpace(promptText))
                form.Add(new StringContent(promptText), "prompt");
        }

        if (TryGetMetadataProperty(metadata, "hotwords", out var hotwords))
        {
            foreach (var hotword in hotwords.Deserialize<string[]?>(JsonSerializerOptions.Web) ?? [])
                form.Add(new StringContent(hotword), "hotwords");
        }

        if (metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in metadata.EnumerateObject())
            {
                if (!IsMappedMetadataProperty(property.Name))
                    form.Add(new StringContent(GetMultipartValue(property.Value)), property.Name);
            }
        }

        // sync mode (default, but explicit)
        form.Add(new StringContent("false"), "stream");

        using var resp = await _client.PostAsync(
            "https://api.z.ai/api/paas/v4/audio/transcriptions",
            form,
            cancellationToken
        );

        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Z.AI STT failed ({(int)resp.StatusCode}): {json}");

        return ConvertZaiResponse(json, request.Model);
    }

    private static TranscriptionResponse ConvertZaiResponse(
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

            // Z.AI sync response has no segments
            Segments = [],

            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                ModelId = root.TryGetProperty("model", out var m)
                    ? m.GetString() ?? model
                    : model,
                Body = json
            }
        };
    }

    private static bool TryGetMetadataProperty(JsonElement metadata, string name, out JsonElement value)
    {
        value = default;
        var found = false;
        if (metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in metadata.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    found = true;
                }
            }
        }

        return found;
    }

    private static bool IsMappedMetadataProperty(string name)
        => name.Equals("prompt", StringComparison.OrdinalIgnoreCase)
            || name.Equals("hotwords", StringComparison.OrdinalIgnoreCase)
            || name.Equals("file", StringComparison.OrdinalIgnoreCase)
            || name.Equals("model", StringComparison.OrdinalIgnoreCase)
            || name.Equals("stream", StringComparison.OrdinalIgnoreCase);

    private static string GetMultipartValue(JsonElement value)
        => value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.GetRawText();

}
