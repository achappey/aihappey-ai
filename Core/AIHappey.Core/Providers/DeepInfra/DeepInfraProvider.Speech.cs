using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.Providers.OpenAI;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.DeepInfra;

public sealed partial class DeepInfraProvider
{
    private static readonly JsonSerializerOptions SpeechJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        ApplyAuthHeader();

        if (request.Model.StartsWith("hexgrad/"))
        {
            return await HexgradSpeechRequest(request, cancellationToken);
        }

        if (request.Model.StartsWith("ResembleAI/"))
        {
            return await ResembleAISpeechRequest(request, cancellationToken);
        }

        if (request.Model.StartsWith("sesame/"))
        {
            return await SesameSpeechRequest(request, cancellationToken);
        }

        if (request.Model.StartsWith("canopylabs/"))
        {
            return await CanopyLabsSpeechRequest(request, cancellationToken);
        }

        if (request.Model.StartsWith("Zyphra/"))
        {
            return await ZyphraSpeechRequest(request, cancellationToken);
        }

        throw new NotImplementedException(request.Model);
    }

    private async Task<SpeechResponse> DeepInfraSpeechRequest(string model,
        Dictionary<string, object?> payload,
        List<object> warnings,
        DateTime started,
        string outputFormat,
        CancellationToken cancellationToken = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"v1/inference/{model}")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, SpeechJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"DeepInfra TTS failed ({(int)resp.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        if (!root.TryGetProperty("audio", out var audioEl) || audioEl.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("DeepInfra TTS response did not contain audio data.");

        var audioBase64 = audioEl.GetString();
        if (string.IsNullOrWhiteSpace(audioBase64))
            throw new InvalidOperationException("DeepInfra TTS returned empty audio data.");

        var mime = OpenAIProvider.MapToAudioMimeType(outputFormat);

        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = audioBase64.RemoveDataUrlPrefix(),
                MimeType = mime,
                Format = outputFormat
            },
            Warnings = warnings,
            Response = new()
            {
                Timestamp = started,
                ModelId = model,
                Body = root.Clone()
            }
        };
    }

}
