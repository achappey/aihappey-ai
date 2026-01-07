using AIHappey.Core.AI;
using System.Net.Http.Headers;
using AIHappey.Common.Model;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers;

namespace AIHappey.Core.Providers.Zai;

public partial class ZaiProvider : IModelProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var bytes = Convert.FromBase64String(request.Audio.ToString()!);
        var metadata = request.GetTranscriptionProviderMetadata<ZaiTranscriptionProviderMetadata>(GetIdentifier());

        using var form = new MultipartFormDataContent();

        // file (required)
        var fileName = "audio" + request.MediaType.GetAudioExtension();
        var file = new ByteArrayContent(bytes);

        if (!string.IsNullOrWhiteSpace(request.MediaType))
            file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);

        form.Add(file, "file", fileName);

        // required
        form.Add(new StringContent(request.Model), "model");

        // optional
        if (!string.IsNullOrWhiteSpace(metadata?.Prompt))
            form.Add(new StringContent(metadata.Prompt), "prompt");

        // hotwords → repeated field
        if (metadata?.Hotwords?.Any() == true)
        {
            foreach (var hw in metadata.Hotwords)
                form.Add(new StringContent(hw), "hotwords");
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

}