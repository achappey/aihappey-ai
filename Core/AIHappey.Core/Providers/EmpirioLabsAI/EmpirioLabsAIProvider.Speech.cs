using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.EmpirioLabsAI;

public partial class EmpirioLabsAIProvider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text)) throw new ArgumentException("Text is required.", nameof(request));

        var payload = CreateEmpirioVercelPayload(request.ProviderOptions,
            "model", "input", "voice", "output_format", "response_format", "speed", "language", "instructions");
        payload["model"] = request.Model;
        payload["input"] = request.Text;
        SetEmpirio(payload, "voice", request.Voice);
        SetEmpirio(payload, "output_format", request.OutputFormat);
        SetEmpirio(payload, "speed", request.Speed);
        SetEmpirio(payload, "language", request.Language);
        SetEmpirio(payload, "instructions", request.Instructions);
        var result = await SendEmpirioJsonAsync(HttpMethod.Post, "v1/audio/speech", payload, "speech request", cancellationToken);
        var audio = await ReadEmpirioAudioAsync(result.Root, request.OutputFormat, cancellationToken);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(audio.Bytes),
                MimeType = audio.MediaType,
                Format = audio.Format
            },
            Warnings = [],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root),
            Request = new SpeechRequestItem { Body = payload },
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = result.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = result.Root
            }
        };
    }

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Model)) throw new ArgumentException("Model is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Input)) throw new ArgumentException("Input is required.", nameof(options));
        var payload = CreateEmpirioOpenAIPayload(options.AdditionalProperties,
            "model", "input", "voice", "response_format", "output_format", "instructions", "speed", "stream_format");
        payload["model"] = options.Model;
        payload["input"] = options.Input;
        SetEmpirio(payload, "voice", options.Voice);
        SetEmpirio(payload, "output_format", options.ResponseFormat);
        SetEmpirio(payload, "instructions", options.Instructions);
        SetEmpirio(payload, "speed", options.Speed);
        var result = await SendEmpirioJsonAsync(HttpMethod.Post, "v1/audio/speech", payload, "speech request", cancellationToken);
        var audio = await ReadEmpirioAudioAsync(result.Root, options.ResponseFormat, cancellationToken);
        return (audio.Bytes, audio.MediaType);
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!IsEmpirioStreamingSpeechModel(options.Model))
        {
            var (audio, _) = await OpenAISpeechRequestAsync(options, cancellationToken);
            yield return new AudioSpeechStreamDelta { Audio = Convert.ToBase64String(audio) };
            yield return new AudioSpeechStreamDone();
            yield break;
        }

        var payload = CreateEmpirioOpenAIPayload(options.AdditionalProperties,
            "model", "input", "voice", "response_format", "output_format", "instructions", "speed", "stream_format");
        payload["model"] = options.Model;
        payload["input"] = options.Input;
        SetEmpirio(payload, "voice", options.Voice);
        SetEmpirio(payload, "output_format", options.ResponseFormat);
        SetEmpirio(payload, "speed", options.Speed);

        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech:stream")
        {
            Content = new StringContent(payload.ToJsonString(EmpirioMediaJson), Encoding.UTF8, "application/json")
        };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"EmpirioLabs streaming speech failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(cancellationToken)}");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        string? eventName = null;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                eventName = line[6..].Trim();
                continue;
            }
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") yield break;
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            var type = eventName ?? GetEmpirioString(root, "type");
            if (string.Equals(type, "audio.chunk", StringComparison.OrdinalIgnoreCase))
            {
                var chunk = GetEmpirioString(root, "audio") ?? GetEmpirioString(root, "data") ?? GetEmpirioString(root, "chunk");
                if (!string.IsNullOrWhiteSpace(chunk)) yield return new AudioSpeechStreamDelta { Audio = chunk };
            }
            else if (string.Equals(type, "audio.done", StringComparison.OrdinalIgnoreCase))
            {
                yield return new AudioSpeechStreamDone { Usage = ReadEmpirioSpeechUsage(root) };
            }
            else if (string.Equals(type, "audio.error", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"EmpirioLabs streaming speech failed: {GetEmpirioError(root)}");
        }
    }

    private async Task<EmpirioAudio> ReadEmpirioAudioAsync(JsonElement root, string? requestedFormat, CancellationToken cancellationToken)
    {
        var valueRoot = GetEmpirioPayloadRoot(root);
        JsonElement item = valueRoot;
        if (valueRoot.ValueKind == JsonValueKind.Object
            && valueRoot.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
            item = data[0];
        else if (root.TryGetProperty("data", out data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
            item = data[0];

        var format = GetEmpirioString(item, "format") ?? requestedFormat ?? "mp3";
        var fallbackMime = EmpirioAudioMime(format);
        var base64 = GetEmpirioString(item, "b64_json") ?? GetEmpirioString(item, "audio");
        if (!string.IsNullOrWhiteSpace(base64))
            return new EmpirioAudio(Convert.FromBase64String(base64), fallbackMime, NormalizeEmpirioAudioFormat(format));
        var url = GetEmpirioString(item, "url") ?? GetEmpirioString(root, "url");
        if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("EmpirioLabs speech response contained no audio URL or base64 audio.");
        var downloaded = await DownloadEmpirioMediaAsync(url, fallbackMime, cancellationToken);
        return new EmpirioAudio(downloaded.Bytes, downloaded.MediaType, NormalizeEmpirioAudioFormat(format));
    }

    private static bool IsEmpirioStreamingSpeechModel(string? model)
        => !string.IsNullOrWhiteSpace(model)
            && (model.Contains("tts-1-5-mini", StringComparison.OrdinalIgnoreCase)
                || model.Contains("tts-1-5-max", StringComparison.OrdinalIgnoreCase));

    private static AudioSpeechUsage? ReadEmpirioSpeechUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;
        return new AudioSpeechUsage
        {
            InputTokens = usage.TryGetProperty("input_tokens", out var input) && input.TryGetInt32(out var i) ? i : null,
            OutputTokens = usage.TryGetProperty("output_tokens", out var output) && output.TryGetInt32(out var o) ? o : null,
            TotalTokens = usage.TryGetProperty("total_tokens", out var total) && total.TryGetInt32(out var t) ? t : null
        };
    }

    private static string NormalizeEmpirioAudioFormat(string value) => value.Trim().ToLowerInvariant() switch
    {
        "wav" or "wave" => "wav", "ogg_opus" or "opus" or "ogg" => "opus", "flac" => "flac", "pcm" => "pcm", _ => "mp3"
    };

    private static string EmpirioAudioMime(string? format) => NormalizeEmpirioAudioFormat(format ?? "mp3") switch
    {
        "wav" => "audio/wav", "opus" => "audio/ogg", "flac" => "audio/flac", "pcm" => "audio/pcm", _ => "audio/mpeg"
    };

    private sealed record EmpirioAudio(byte[] Bytes, string MediaType, string Format);
}
