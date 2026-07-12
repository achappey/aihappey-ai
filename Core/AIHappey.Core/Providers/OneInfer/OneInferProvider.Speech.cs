using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.OneInfer;

public partial class OneInferProvider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var metadata = GetOneInferProviderOptions(request.ProviderOptions);
        var payload = OneInferJsonObjectToDictionary(metadata);

        payload["model"] = request.Model;
        payload["prompt"] = request.Text;

        if (!string.IsNullOrWhiteSpace(request.Voice))
            payload["voice_id"] = request.Voice;
        if (!string.IsNullOrWhiteSpace(request.OutputFormat))
            payload["format"] = request.OutputFormat;
        if (request.Speed.HasValue)
            payload["speed"] = request.Speed.Value;
        if (!string.IsNullOrWhiteSpace(request.Language))
            payload["language"] = request.Language;
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            payload["instructions"] = request.Instructions;
        if (!payload.ContainsKey("stream"))
            payload["stream"] = false;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/ula/generate-audio")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, OneInferJsonOptions),
                Encoding.UTF8,
                MediaTypeHeaderValue.Parse(MediaTypeNames.Application.Json))
        };
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OneInfer audio generation failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        var raw = Encoding.UTF8.GetString(bytes);
        if (!TryParseOneInferJson(raw, out var document))
        {
            var contentType = response.Content.Headers.ContentType?.MediaType;
            var format = NormalizeOneInferAudioFormat(request.OutputFormat, contentType);
            return new SpeechResponse
            {
                Audio = new SpeechAudioResponse
                {
                    Base64 = Convert.ToBase64String(bytes),
                    MimeType = ResolveOneInferAudioMimeType(format, contentType),
                    Format = format
                },
                Warnings = warnings,
                Request = new() { Body = payload },
                ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
                Response = new ResponseData
                {
                    Timestamp = now,
                    Headers = response.GetHeaders(),
                    ModelId = request.Model.ToModelId(GetIdentifier()),
                    Body = raw
                }
            };
        }

        using (document)
        {
            var root = document.RootElement.Clone();
            var data = OneInferGetData(root);
            var audio = await ExtractOneInferSpeechAudioAsync(data, request.OutputFormat, cancellationToken);

            return new SpeechResponse
            {
                Audio = audio,
                Warnings = warnings,
                Request = new() { Body = payload },
                ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
                Response = new ResponseData
                {
                    Timestamp = ReadOneInferUnixTimestamp(data, "created") ?? now,
                    Headers = response.GetHeaders(),
                    ModelId = request.Model.ToModelId(GetIdentifier()),
                    Body = root
                }
            };
        }
    }

    private async Task<SpeechAudioResponse> ExtractOneInferSpeechAudioAsync(
        JsonElement data,
        string? requestedFormat,
        CancellationToken cancellationToken)
    {
        if (!data.TryGetProperty("audios", out var audios) || audios.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("OneInfer audio generation response contained no audios.");

        foreach (var item in audios.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var format = OneInferTryGetString(item, "format") ?? NormalizeOneInferAudioFormat(requestedFormat, null);
            var mimeType = OneInferTryGetString(item, "mime_type", "mimeType", "content_type", "contentType")
                ?? ResolveOneInferAudioMimeType(format, null);
            var base64 = OneInferTryGetString(item, "base64_data", "base64", "data", "b64_json");

            if (!string.IsNullOrWhiteSpace(base64))
            {
                return new SpeechAudioResponse
                {
                    Base64 = base64.RemoveDataUrlPrefix(),
                    MimeType = mimeType,
                    Format = format
                };
            }

            var url = OneInferTryGetString(item, "url", "audio_url", "audioUrl");
            if (string.IsNullOrWhiteSpace(url))
                continue;

            return await NormalizeOneInferSpeechAudioUrlAsync(url, format, mimeType, cancellationToken);
        }

        throw new InvalidOperationException("OneInfer audio generation response contained no supported audio payloads.");
    }

    private async Task<SpeechAudioResponse> NormalizeOneInferSpeechAudioUrlAsync(
        string value,
        string format,
        string mimeType,
        CancellationToken cancellationToken)
    {
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return new SpeechAudioResponse
            {
                Base64 = value.RemoveDataUrlPrefix(),
                MimeType = OneInferTryGetDataUrlMediaType(value) ?? mimeType,
                Format = format
            };
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            using var audioResponse = await _client.GetAsync(uri, cancellationToken);
            var bytes = await audioResponse.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!audioResponse.IsSuccessStatusCode || bytes.Length == 0)
                throw new InvalidOperationException($"Failed to download OneInfer audio from returned URL ({(int)audioResponse.StatusCode}).");

            var responseMimeType = audioResponse.Content.Headers.ContentType?.MediaType
                ?? OneInferGuessAudioMimeType(value)
                ?? mimeType;

            return new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(bytes),
                MimeType = responseMimeType,
                Format = NormalizeOneInferAudioFormat(format, responseMimeType)
            };
        }

        return new SpeechAudioResponse
        {
            Base64 = value.RemoveDataUrlPrefix(),
            MimeType = mimeType,
            Format = format
        };
    }
}
