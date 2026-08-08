using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.ApiAirforce;

public partial class ApiAirforceProvider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Voice))
            throw new ArgumentException("Voice is required.", nameof(request));

        var format = ResolveApiAirforceSpeechFormat(request.OutputFormat);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = NormalizeModelId(request.Model),
            ["input"] = request.Text,
            ["voice"] = request.Voice,
            ["response_format"] = format,
            ["speed"] = request.Speed,
            ["language_code"] = request.Language
        };

        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "model", "input", "voice", "response_format", "speed", "language_code"
        };
        MergeRawProviderOptions(payload, request.ProviderOptions, GetIdentifier(), blocked);

        var result = await SendApiAirforceSpeechAsync(payload, format, cancellationToken);
        var warnings = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            AddUnsupportedWarning(warnings, "instructions", "Use providerOptions.apiairforce.voice_settings for provider-specific voice control.");

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(result.Audio),
                MimeType = result.MimeType,
                Format = format
            },
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Request = new SpeechRequestItem { Body = payload },
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Headers = result.Headers,
                Body = payload
            }
        };
    }

    private async Task<ApiAirforceSpeechResult> SendApiAirforceSpeechAsync(
        Dictionary<string, object?> payload,
        string responseFormat,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, ApiAirforceMediaJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"ApiAirforce speech failed ({(int)response.StatusCode} {response.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        return new ApiAirforceSpeechResult(
            bytes,
            response.Content.Headers.ContentType?.MediaType ?? ResolveAudioMimeType(responseFormat),
            response.GetHeaders());
    }

    private static string ResolveApiAirforceSpeechFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return "mp3";

        return format.Trim().ToLowerInvariant() switch
        {
            "mpeg" => "mp3",
            "wav" or "wave" => "pcm_24000",
            "pcm" => "pcm_24000",
            "ulaw" => "ulaw_8000",
            _ => format.Trim().ToLowerInvariant()
        };
    }

    private sealed record ApiAirforceSpeechResult(
        byte[] Audio,
        string MimeType,
        Dictionary<string, string> Headers);
}
