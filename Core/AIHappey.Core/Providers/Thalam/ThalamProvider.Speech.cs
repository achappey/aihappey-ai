using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Thalam;

public partial class ThalamProvider
{
    private async Task<SpeechResponse> ThalamSpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var now = DateTime.UtcNow;
        var providerOptions = GetThalamProviderOptions(request.ProviderOptions);
        var payload = BuildThalamSpeechPayload(request, providerOptions);
        var json = JsonSerializer.Serialize(payload, ThalamJsonOptions);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var httpResponse = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var audioBytes = await httpResponse.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!httpResponse.IsSuccessStatusCode)
        {
            var error = Encoding.UTF8.GetString(audioBytes);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"Thalam speech generation failed ({(int)httpResponse.StatusCode})."
                : $"Thalam speech generation failed ({(int)httpResponse.StatusCode}): {error}");
        }

        var mediaType = httpResponse.Content.Headers.ContentType?.MediaType ?? "audio/mpeg";
        var format = NormalizeThalamAudioFormat(request.OutputFormat, mediaType);

        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = Convert.ToBase64String(audioBytes),
                MimeType = mediaType,
                Format = format
            },
            Warnings = [],
            ProviderMetadata = CreateThalamProviderMetadata(new
            {
                endpoint = "v1/audio/speech",
                payload,
                bytes = audioBytes.Length
            }),
            Response = new()
            {
                Timestamp = now,
                Headers = httpResponse.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            },
            Request = new()
            {
                Body = payload
            }
        };
    }

    private static Dictionary<string, object?> BuildThalamSpeechPayload(SpeechRequest request, JsonElement providerOptions)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["input"] = request.Text,
            ["voice"] = request.Voice,
            ["response_format"] = request.OutputFormat,
            ["speed"] = request.Speed,
            ["instructions"] = request.Instructions,
            ["language"] = request.Language
        };

        MergeThalamProviderOptions(payload, providerOptions);
        return payload;
    }
}
