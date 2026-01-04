using AIHappey.Core.AI;
using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Common.Model;

namespace AIHappey.Core.Providers.Scaleway;

public partial class ScalewayProvider : IModelProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(
          TranscriptionRequest request,
          CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var bytes = Convert.FromBase64String(request.Audio.ToString()!);

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
    /*    if (!string.IsNullOrWhiteSpace(request.Language))
            form.Add(new StringContent(request.Language), "language");

        if (!string.IsNullOrWhiteSpace(request.Prompt))
            form.Add(new StringContent(request.Prompt), "prompt");*/

        // only supported format
    /*    form.Add(new StringContent("json"), "response_format");

        // temperature (0–2)
        form.Add(new StringContent(
            request.Temperature
                .ToString(System.Globalization.CultureInfo.InvariantCulture)),
            "temperature"
        );*/

        var url = "v1/audio/transcriptions";

        using var resp = await _client.PostAsync(url, form, cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Scaleway STT failed ({(int)resp.StatusCode}): {json}");

        return ConvertScalewayResponse(json, request.Model);
    }

    private static TranscriptionResponse ConvertScalewayResponse(
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

            // Scaleway does not return segments
            Segments = [],

            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                ModelId = model,
                Body = json
            }
        };
    }
}