using System.Text.Json;
using System.Text;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Novita;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Core.Extensions;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.Novita;

public partial class NovitaProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(
           TranscriptionRequest request,
           CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var metadata = request.GetProviderMetadata<NovitaTranscriptionProviderMetadata>(GetIdentifier());

        // Novita expects:
        // - file: base64 string OR URL
        // - application/json (not multipart)

        var payload = new Dictionary<string, object?>
        {
            ["file"] = request.Audio?.ToString()
                ?? throw new InvalidOperationException("Audio is required"),
        };

        if (!string.IsNullOrWhiteSpace(metadata?.Prompt))
            payload["prompt"] = metadata.Prompt;

        if (metadata?.Hotwords?.Any() == true)
            payload["hotwords"] = metadata.Hotwords.ToArray();

        var requestBody = JsonSerializer.Serialize(payload);

        using var content = new StringContent(
            requestBody,
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

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        return ConvertNovitaResponse(root, requestBody,
            GetIdentifier(), request.Model, resp.GetHeaders());
    }

    private static TranscriptionResponse ConvertNovitaResponse(JsonElement root,
        string requestBody, string providerId, string model, IDictionary<string, string>? headers = null)
    {
        return new TranscriptionResponse
        {
            Text = root.TryGetProperty("text", out var t)
                ? t.GetString() ?? ""
                : "",

            // Novita returns no segments
            Segments = [],
            ProviderMetadata = providerId
                .CreatePrimitiveProviderMetadata(),
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                Headers = headers,
                ModelId = model.ToModelId(providerId),
                Body = root.Clone()
            },
            Request = new TranscriptionRequestItem
            {
                Body = requestBody
            }
        };
    }
}
