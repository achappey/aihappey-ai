using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Http.Headers;
using System.Net.Mime;

namespace AIHappey.Core.Providers.MegaNova;

public partial class MegaNovaProvider
{
    private static readonly JsonSerializerOptions MegaNovaSpeechJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<SpeechResponse> SpeechRequestMegaNova(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var metadata = GetMegaNovaProviderMetadata(request, GetIdentifier());
        var payload = BuildMegaNovaSpeechPayload(request, metadata);

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, MegaNovaSpeechJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/*"));

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.MediaType;

        if (!response.IsSuccessStatusCode)
        {
            var error = Encoding.UTF8.GetString(responseBytes);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"MegaNova speech request failed ({(int)response.StatusCode})."
                : $"MegaNova speech request failed ({(int)response.StatusCode}): {error}");
        }

        var format = ResolveMegaNovaAudioFormat(request.OutputFormat, contentType);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(responseBytes),
                MimeType = ResolveMegaNovaAudioMimeType(format, contentType),
                Format = format
            },
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new ResponseData
            {
                Timestamp = now,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            },
            Request = new SpeechRequestItem
            {
                Body = payload
            }
        };
    }

    private static Dictionary<string, object?> BuildMegaNovaSpeechPayload(SpeechRequest request, JsonElement metadata)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);

        MergeMegaNovaProviderMetadata(payload, metadata);

        payload["model"] = request.Model.Trim();
        payload["input"] = request.Text;

        if (!string.IsNullOrWhiteSpace(request.Voice))
            payload["voice"] = request.Voice.Trim();

        if (!string.IsNullOrWhiteSpace(request.OutputFormat))
            payload["response_format"] = request.OutputFormat.Trim().ToLowerInvariant();

        if (request.Speed is not null)
            payload["speed"] = request.Speed.Value;

        return payload;
    }
}
