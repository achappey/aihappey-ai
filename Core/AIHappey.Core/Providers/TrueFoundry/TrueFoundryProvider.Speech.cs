using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Text.Json;
using System.Net.Mime;
using System.Text;

namespace AIHappey.Core.Providers.TrueFoundry;

public partial class TrueFoundryProvider
{
    private async Task<SpeechResponse> TrueFoundrySpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var payload = TrueFoundryJsonObjectToDictionary(metadata);

        payload["model"] = request.Model.Trim();
        payload["input"] = request.Text;

        if (!string.IsNullOrWhiteSpace(request.Voice))
            payload["voice"] = request.Voice.Trim();
        if (!string.IsNullOrWhiteSpace(request.OutputFormat))
            payload["response_format"] = request.OutputFormat.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            payload["instructions"] = request.Instructions;
        if (request.Speed.HasValue)
            payload["speed"] = request.Speed.Value;
        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language", details = "TrueFoundry speech endpoint does not document a language field. Use providerOptions.truefoundry.voice for provider-specific language voices." });

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "audio/speech")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, TrueFoundryJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.MediaType;

        if (!response.IsSuccessStatusCode)
        {
            var error = Encoding.UTF8.GetString(responseBytes);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"TrueFoundry speech request failed ({(int)response.StatusCode})."
                : $"TrueFoundry speech request failed ({(int)response.StatusCode}): {error}");
        }

        var requestedFormat = ResolveTrueFoundrySpeechFormat(request, metadata, contentType);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(responseBytes),
                MimeType = contentType ?? OpenAI.OpenAIProvider.MapToAudioMimeType(requestedFormat),
                Format = requestedFormat
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

    private string ResolveTrueFoundrySpeechFormat(SpeechRequest request, JsonElement metadata, string? contentType)
    {
        var requestedFormat = !string.IsNullOrWhiteSpace(request.OutputFormat)
            ? request.OutputFormat.Trim().ToLowerInvariant()
            : TrueFoundryTryGetString(metadata, "response_format", "responseFormat", "outputFormat", "format")?.Trim().ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(requestedFormat))
            return requestedFormat;

        if (!string.IsNullOrWhiteSpace(contentType))
        {
            if (contentType.Contains("mpeg", StringComparison.OrdinalIgnoreCase)) return "mp3";
            if (contentType.Contains("wav", StringComparison.OrdinalIgnoreCase) || contentType.Contains("wave", StringComparison.OrdinalIgnoreCase)) return "wav";
            if (contentType.Contains("opus", StringComparison.OrdinalIgnoreCase)) return "opus";
            if (contentType.Contains("aac", StringComparison.OrdinalIgnoreCase)) return "aac";
            if (contentType.Contains("flac", StringComparison.OrdinalIgnoreCase)) return "flac";
            if (contentType.Contains("pcm", StringComparison.OrdinalIgnoreCase)) return "pcm";
        }

        return "mp3";
    }
}
