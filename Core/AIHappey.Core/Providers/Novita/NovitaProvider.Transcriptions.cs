using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Common.Model;
using System.Text;

namespace AIHappey.Core.Providers.Novita;

public partial class NovitaProvider : IModelProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(
           TranscriptionRequest request,
           CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        // Novita expects:
        // - file: base64 string OR URL
        // - application/json (not multipart)

        var payload = new Dictionary<string, object?>
        {
            ["file"] = request.Audio?.ToString()
                ?? throw new InvalidOperationException("Audio is required"),
        };

    /*    if (!string.IsNullOrWhiteSpace(request.Prompt))
            payload["prompt"] = request.Prompt;

        if (request.Hotwords?.Any() == true)
            payload["hotwords"] = request.Hotwords.Take(100).ToArray();*/

        using var content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json"
        );

        using var resp = await _client.PostAsync(
            "https://api.novita.ai/v3/glm-asr",
            content,
            cancellationToken
        );

        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Novita STT failed ({(int)resp.StatusCode}): {json}");

        return ConvertNovitaResponse(json);
    }

    private static TranscriptionResponse ConvertNovitaResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        return new TranscriptionResponse
        {
            Text = root.TryGetProperty("text", out var t)
                ? t.GetString() ?? ""
                : "",

            // Novita returns no segments
            Segments = [],

            Response = new ImageResponseData
            {
                Timestamp = DateTime.UtcNow,
                ModelId = "glm-asr-2512",
                Body = json
            }
        };
    }
}