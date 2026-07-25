using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.RekaAI;

public partial class RekaAIProvider
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

        var metadata = request.GetProviderMetadata<Common.Model.Providers.RekaAI.RekaAITranscriptionProviderMetadata>(GetIdentifier());
        var model = NormalizeRekaTranscriptionModelId(request.Model);
        var audioBase64 = NormalizeRekaTranscriptionAudioData(request.Audio!);
        var payload = BuildRekaTranscriptionPayload(model, audioBase64, request.MediaType!, metadata, stream: true);
        var requestBody = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions");
        httpRequest.Headers.Accept.Clear();
        httpRequest.Headers.Accept.ParseAdd("text/event-stream");
        httpRequest.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        httpRequest.Content = new StringContent(requestBody, Encoding.UTF8, MediaTypeNames.Application.Json);

        using var response = await _client.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"RekaAI streaming transcription failed ({(int)response.StatusCode}): {error}");
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
            catch (JsonException exception)
            {
                throw new InvalidOperationException($"Failed to parse RekaAI transcription SSE JSON event: {data}", exception);
            }

            if (root.TryGetProperty("error", out var error))
                throw new InvalidOperationException($"RekaAI streaming transcription returned an error: {error}");

            if (TryExtractRekaTranscriptionDelta(root, out var delta))
            {
                transcript.Append(delta);
                yield return new OpenAITranscriptionTextDelta { Delta = delta };
                continue;
            }

            var completionText = ExtractRekaTranscriptionText(root);
            if (!string.IsNullOrWhiteSpace(completionText))
                completedText = completionText;
        }

        var finalText = transcript.Length > 0 ? transcript.ToString() : completedText;
        if (!string.IsNullOrWhiteSpace(finalText))
            yield return new OpenAITranscriptionTextDone { Text = finalText };
    }

    private static bool TryExtractRekaTranscriptionDelta(JsonElement root, out string delta)
    {
        delta = string.Empty;

        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var choice in choices.EnumerateArray())
        {
            if (choice.TryGetProperty("delta", out var contentDelta)
                && contentDelta.ValueKind == JsonValueKind.Object
                && contentDelta.TryGetProperty("content", out var content)
                && TryExtractRekaTextContent(content, out delta))
            {
                return true;
            }
        }

        return false;
    }
}
