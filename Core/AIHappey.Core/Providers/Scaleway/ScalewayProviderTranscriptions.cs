using AIHappey.Core.AI;
using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Common.Model;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Scaleway;
using System.Globalization;

namespace AIHappey.Core.Providers.Scaleway;

public partial class ScalewayProvider : IModelProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(
          TranscriptionRequest request,
          CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var bytes = Convert.FromBase64String(request.Audio.ToString()!);
        var metadata = request.GetTranscriptionProviderMetadata<ScalewayTranscriptionProviderMetadata>(GetIdentifier());
        using var form = new MultipartFormDataContent();

        // file (required)
        var fileName = "audio" + request.MediaType.GetAudioExtension();
        var file = new ByteArrayContent(bytes);

        if (!string.IsNullOrWhiteSpace(request.MediaType))
            file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);

        form.Add(file, "file", fileName);

        // required
        form.Add(new StringContent(request.Model), "model");

        if (!string.IsNullOrWhiteSpace(metadata?.Language))
            form.Add(new StringContent(metadata.Language), "language");

        if (!string.IsNullOrWhiteSpace(metadata?.Prompt))
            form.Add(new StringContent(metadata.Prompt), "prompt");

        // temperature (optional)
        if (metadata?.Temperature is not null)
        {
            form.Add(
                new StringContent(
                    metadata.Temperature.Value.ToString(CultureInfo.InvariantCulture)
                ),
                "temperature"
            );
        }

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
