using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.OrqRouter;

public partial class OrqRouterProvider
{
    private async Task<SpeechResponse> OrqRouterSpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Voice))
            throw new ArgumentException("Voice is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        var providerOptions = ReadOrqRouterProviderOptions(request.ProviderOptions);
        var payload = new Dictionary<string, object?>
        {
            ["input"] = request.Text,
            ["model"] = request.Model,
            ["voice"] = request.Voice,
            ["response_format"] = string.IsNullOrWhiteSpace(request.OutputFormat) ? null : request.OutputFormat
        };

        if (request.Speed is not null)
            payload["speed"] = request.Speed.Value;

        MergeOrqRouterProviderOptions(payload, providerOptions, ReservedOrqRouterSpeechKeys);

        var requestBody = JsonSerializer.Serialize(payload, OrqRouterJsonOptions);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v2/router/audio/speech")
        {
            Content = new StringContent(requestBody, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"OrqRouter speech request failed ({(int)response.StatusCode})."
                : $"OrqRouter speech request failed ({(int)response.StatusCode}): {error}");
        }

        var contentType = response.Content.Headers.ContentType?.MediaType;
        var requestedFormat = ReadOrqRouterSpeechFormat(payload);
        var format = ResolveOrqRouterAudioFormat(requestedFormat, contentType, "mp3");
        var mimeType = ResolveOrqRouterAudioMimeType(format, contentType);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(bytes),
                MimeType = mimeType,
                Format = format
            },
            Warnings = warnings,
            ProviderMetadata = ProviderId.CreatePrimitiveProviderMetadata(new
            {
                response = new
                {
                    statusCode = (int)response.StatusCode,
                    contentType,
                    contentLength = bytes.LongLength
                }
            }),
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = new
                {
                    statusCode = (int)response.StatusCode,
                    contentType,
                    contentLength = bytes.LongLength
                }
            },
            Request = new SpeechRequestItem
            {
                Body = payload
            }
        };
    }

    private static readonly HashSet<string> ReservedOrqRouterSpeechKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "input", "model", "voice", "response_format", "speed"
    };

    private static string? ReadOrqRouterSpeechFormat(Dictionary<string, object?> payload)
    {
        if (!payload.TryGetValue("response_format", out var value))
            return null;

        return value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement { ValueKind: JsonValueKind.Number } element => element.GetRawText(),
            _ => value?.ToString()
        };
    }
}
