using AIHappey.Core.AI;
using AIHappey.Common.Model;
using System.Text.Json;
using System.Text;
using System.Net.Mime;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using AIHappey.Common.Model.Providers.Together;
using AIHappey.Common.Extensions;

namespace AIHappey.Core.Providers.Together;

public partial class TogetherProvider : IModelProvider
{
    private static readonly JsonSerializerOptions SpeechSettings = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        // Together does not expose these (currently) on /audio/speech.
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });

        var metadata = request.GetSpeechProviderMetadata<TogetherSpeechProviderMetadata>(GetIdentifier());

        // Accept both: "together/<model>" and "<model>".
        var modelName = NormalizeTogetherModelName(request.Model);

        var voice = ResolveVoice(modelName, request, metadata);
        if (string.IsNullOrWhiteSpace(voice))
            throw new ArgumentException("'voice' is required for Together TTS.", nameof(request));

        var responseFormat =
            request.OutputFormat
            ?? metadata?.ResponseFormat
            ?? "wav";

        var language = request.Language ?? metadata?.Language;

        var payload = new
        {
            model = modelName,
            input = request.Text,
            voice,
            response_format = responseFormat,
            language,
            response_encoding = metadata?.ResponseEncoding,
            sample_rate = metadata?.SampleRate,
            stream = false
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, SpeechSettings),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        // Avoid mutating shared HttpClient headers.
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/*"));

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException(
                $"Together TTS failed ({(int)resp.StatusCode}): {err}"
            );
        }

        var mime = ResolveSpeechMimeType(responseFormat, resp.Content.Headers.ContentType?.MediaType);
        var audio = Convert.ToBase64String(bytes);

        return new SpeechResponse
        {
            Audio = new()
            {
                MimeType = mime,
                Base64 = audio,
                Format = responseFormat
            },
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
            }
        };
    }

    private string NormalizeTogetherModelName(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(model));

        // Only strip prefix if the provider is explicitly present.
        // NOTE: Together model IDs themselves contain '/', e.g. "canopylabs/orpheus-3b-0.1-ft".
        var prefix = GetIdentifier() + "/";
        return model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? model.SplitModelId().Model
            : model.Trim();
    }

    private static string? ResolveVoice(
        string modelName,
        SpeechRequest request,
        TogetherSpeechProviderMetadata? metadata)
    {
        if (!string.IsNullOrWhiteSpace(request.Voice))
            return request.Voice;

        // Route to per-model-family voice settings.
        if (modelName.StartsWith("cartesia/", StringComparison.OrdinalIgnoreCase))
            return metadata?.Cartesia?.Voice;

        if (modelName.StartsWith("hexgrad/", StringComparison.OrdinalIgnoreCase))
            return metadata?.Hexgrad?.Voice;

        if (modelName.StartsWith("canopylabs/", StringComparison.OrdinalIgnoreCase))
            return metadata?.CanopyLabs?.Voice;

        // Fallback: allow a generic voice in provider metadata in case new model families appear.
        // (Together's OpenAPI keeps voice at top-level, but our metadata stores it per-family.)
        return null;
    }

    private static string ResolveSpeechMimeType(string? responseFormat, string? contentType)
    {
        var fmt = (responseFormat ?? string.Empty).Trim().ToLowerInvariant();
        return fmt switch
        {
            "mp3" => "audio/mpeg",
            "wav" => "audio/wav",
            "raw" => contentType ?? "application/octet-stream",
            _ => contentType ?? "application/octet-stream"
        };
    }
}
