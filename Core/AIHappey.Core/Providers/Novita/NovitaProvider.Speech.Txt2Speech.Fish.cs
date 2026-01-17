using AIHappey.Core.AI;
using AIHappey.Common.Model;
using System.Text.Json;
using System.Text;

namespace AIHappey.Core.Providers.Novita;

public partial class NovitaProvider : IModelProvider
{
    private const string V4BaseUrl = "https://api.novita.ai/v4beta/txt2speech";

    private async Task<SpeechResponse> SpeechRequestAsyncTxt2SpeechFish(
        SpeechRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        // ---- output audio type ----
        var audioType =
            request.OutputFormat
            //  ?? metadata?.Text2Speech?.
            ?? "mp3";

        _client.DefaultRequestHeaders.Remove("model");
        _client.DefaultRequestHeaders.Add("model", request.Model);

        var submitPayload = new Dictionary<string, object?>
        {
            ["text"] = request.Text,
            ["format"] = audioType
        };

        using var submitContent = new StringContent(
            JsonSerializer.Serialize(submitPayload),
            Encoding.UTF8,
            "application/json"
        );

        using var submitResp = await _client.PostAsync(
            V4BaseUrl,
            submitContent,
            cancellationToken
        );

        var bytes = await submitResp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!submitResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Speechmatics TTS failed ({(int)submitResp.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        var mime = audioType.StartsWith("pcm") ? "application/octet-stream"
        : audioType == "mp3" ? "audio/mpeg"
        : audioType == "wav" ? "audio/wav"
        : audioType == "opus" ? "audio/opus"
        : "application/octet-stream";

        var base64 = Convert.ToBase64String(bytes);

        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = base64,
                MimeType = mime,
                Format = audioType ?? "wav"
            },
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model
            }
        };
    }
}
