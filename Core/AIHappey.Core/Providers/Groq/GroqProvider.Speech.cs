using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.Groq;
using AIHappey.Core.AI;
using AIHappey.Core.ModelProviders;

namespace AIHappey.Core.Providers.Groq;

public partial class GroqProvider : IModelProvider
{
    public async Task<SpeechResponse> SpeechRequest(
          SpeechRequest request,
          CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var metadata =
            request.GetSpeechProviderMetadata<GroqSpeechProviderMetadata>(GetIdentifier());

        var voice =
            request.Voice
            ?? metadata?.Voice
            ?? "troy";

        var format =
            request.OutputFormat
            ?? metadata?.ResponseFormat
            ?? "wav";

        var payload = new
        {
            model = request.Model,
            input = request.Text,
            voice,
            response_format = format
        };

        using var content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json"
        );

        using var resp = await _client.PostAsync(
            "https://api.groq.com/openai/v1/audio/speech",
            content,
            cancellationToken
        );

        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException(
                $"Groq TTS failed ({(int)resp.StatusCode}): {err}"
            );
        }

        var mime = format switch
        {
            "wav" => "audio/wav",
            "mp3" => "audio/mpeg",
            _ => "application/octet-stream"
        };

        var base64 = Convert
            .ToBase64String(bytes);

        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = base64,
                MimeType = mime,
                Format = format ?? "wav"
            },
            Warnings = [],
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                ModelId = request.Model,
            }
        };
    }

}
