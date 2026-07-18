using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Common.Extensions;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Google;

public partial class GoogleAIProvider
{
    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var responseFormat = options.ResolveOpenAITranscriptionResponseFormat();
        var request = await options.ToTranscriptionRequest(
            options.Model,
            GetIdentifier(),
            cancellationToken);
        var response = await TranscriptionRequest(request, cancellationToken);

        return response.ToOpenAITranscriptionResponse(responseFormat);
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var request = await options.ToTranscriptionRequest(
            options.Model,
            GetIdentifier(),
            cancellationToken);

        ApplyAuthHeader();

        var model = NormalizeGoogleTranscriptionModelId(request.Model);
        var audioData = NormalizeGoogleTranscriptionAudioData(request.Audio);
        var payload = BuildGoogleTranscriptionPayload(model, audioData, request.MediaType);
        payload["stream"] = true;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, InteractionsRelativeUrl);
        httpRequest.Headers.Accept.Clear();
        httpRequest.Headers.Accept.ParseAdd("text/event-stream");
        httpRequest.Headers.TryAddWithoutValidation("Api-Revision", "2026-05-20");
        httpRequest.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        httpRequest.Content = new StringContent(
            payload.ToJsonString(GoogleTranscriptionJsonOptions),
            Encoding.UTF8,
            MediaTypeNames.Application.Json);

        using var response = await _client.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"{Google} streaming transcription failed ({(int)response.StatusCode}): {error}");
        }

        var transcript = new StringBuilder();
        string? completedText = null;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            var data = line["data:".Length..].Trim();
            if (data.Length == 0 || string.Equals(data, "[DONE]", StringComparison.OrdinalIgnoreCase))
                continue;

            JsonElement root;
            try
            {
                using var document = JsonDocument.Parse(data);
                root = document.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"Failed to parse Google transcription SSE JSON event: {data}",
                    ex);
            }

            var eventType = TryGetString(root, "event_type")
                ?? TryGetString(root, "eventType")
                ?? TryGetString(root, "type");

            if (string.Equals(eventType, "error", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{Google} streaming transcription returned an error: {data}");

            if (string.Equals(eventType, "step.delta", StringComparison.OrdinalIgnoreCase)
                && TryGetProperty(root, "delta", out var delta)
                && TryExtractGoogleTranscriptionStreamText(delta, out var text))
            {
                transcript.Append(text);
                yield return new OpenAITranscriptionTextDelta { Delta = text };
                continue;
            }

            if (string.Equals(eventType, "interaction.completed", StringComparison.OrdinalIgnoreCase))
            {
                var completionText = ExtractGoogleTranscriptionText(root);
                if (!string.IsNullOrWhiteSpace(completionText))
                    completedText = completionText;
            }
        }

        var finalText = transcript.Length > 0
            ? transcript.ToString()
            : completedText;

        if (!string.IsNullOrWhiteSpace(finalText))
            yield return new OpenAITranscriptionTextDone { Text = finalText };
    }

    private static bool TryExtractGoogleTranscriptionStreamText(JsonElement delta, out string text)
    {
        text = string.Empty;

        if (delta.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in delta.EnumerateArray())
            {
                if (TryExtractGoogleTranscriptionStreamText(item, out text))
                    return true;
            }

            return false;
        }

        if (delta.ValueKind != JsonValueKind.Object)
            return false;

        var type = TryGetString(delta, "type");
        if (!string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
            return false;

        text = TryGetString(delta, "text") ?? string.Empty;
        return !string.IsNullOrWhiteSpace(text);
    }
}
