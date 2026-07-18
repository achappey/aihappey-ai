using System.Net.Http.Headers;
using AIHappey.Vercel.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Common.Extensions;
using System.Text.Json;

namespace AIHappey.Core.Providers.OpperAI;

public partial class OpperAIProvider
{
    private async Task<SpeechResponse> OpperAISpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var now = DateTime.UtcNow;
        var providerOptions = GetOpperAIProviderOptions(request.ProviderOptions);
        var payload = BuildOpperAISpeechPayload(request, providerOptions);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v3/audio/speech")
        {
            Content = CreateOpperAIJsonContent(payload)
        };
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"OpperAI speech generation failed ({(int)response.StatusCode})."
                : $"OpperAI speech generation failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var audio = ExtractOpperAISpeechAudio(root, request.OutputFormat);

        JsonElement? usage = root.TryGetProperty("usage", out var usageElement)
                  && usageElement.ValueKind == JsonValueKind.Object
                      ? usageElement.Clone()
                      : null;

        decimal? cost = usage is { } rawUsage
            && rawUsage.TryGetProperty("cost", out var costElement)
            && costElement.ValueKind == JsonValueKind.Number
            && costElement.TryGetDecimal(out var parsedCost)
                ? parsedCost
                : null;

        return new SpeechResponse
        {
            Audio = audio,
            Warnings = [],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(usage != null ? new
            {
                usage
            } : null, cost),
            Response = new()
            {
                Timestamp = ResolveOpperAITimestamp(root, now),
                Headers = response.GetHeaders(),
                ModelId = (TryGetOpperAIString(root, "model") ?? request.Model).ToModelId(GetIdentifier()),
                Body = root
            },
            Request = new()
            {
                Body = payload
            }
        };
    }

    private static Dictionary<string, object?> BuildOpperAISpeechPayload(
        SpeechRequest request,
        Dictionary<string, object?> providerOptions)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["input"] = request.Text,
            ["voice"] = request.Voice,
            ["format"] = request.OutputFormat,
            ["speed"] = request.Speed
        };

        AddOpperAIParameters(payload, providerOptions);
        payload["store"] = false;
        return payload;
    }

    private static SpeechAudioResponse ExtractOpperAISpeechAudio(
        System.Text.Json.JsonElement root,
        string? requestedFormat)
    {
        var audioRoot = TryGetOpperAIProperty(root, "audio", out var audioElement)
            && audioElement.ValueKind == System.Text.Json.JsonValueKind.Object
            ? audioElement
            : root;

        var mediaType = TryGetOpperAIString(audioRoot, "mime_type", "mimeType")
            ?? ResolveOpperAISpeechMimeType(requestedFormat, null);
        var b64 = TryGetOpperAIString(audioRoot, "b64_json", "base64", "data") ?? string.Empty;
        var format = requestedFormat
            ?? TryGetOpperAIString(root, "format", "response_format")
            ?? mediaType.Split('/').LastOrDefault()
            ?? "mp3";

        return new SpeechAudioResponse
        {
            Base64 = b64.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? b64.RemoveDataUrlPrefix() : b64,
            MimeType = mediaType,
            Format = format
        };
    }
}
