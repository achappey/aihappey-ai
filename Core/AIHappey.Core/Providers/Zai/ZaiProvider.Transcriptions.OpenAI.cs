using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Zai;

public partial class ZaiProvider 
{
    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAITranscriptionRequest();
        ApplyAuthHeader();

        using var request = new HttpRequestMessage(HttpMethod.Post, "v4/audio/transcriptions")
        {
            Content = CreateZaiTranscriptionContent(options, stream: false)
        };

        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw CreateZaiTranscriptionException("Transcription", response, raw);

        using var document = JsonDocument.Parse(raw);
        var text = ReadZaiString(document.RootElement, "text") ?? string.Empty;

        return options.ResolveOpenAITranscriptionResponseFormat() == "verbose_json"
            ? new OpenAITranscriptionVerboseResponse
            {
                Text = text,
                Language = string.Empty,
                Duration = 0,
                Segments = [],
                Words = []
            }
            : new OpenAITranscriptionResponse { Text = text };
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAITranscriptionRequest();
        ApplyAuthHeader();

        using var request = new HttpRequestMessage(HttpMethod.Post, "v4/audio/transcriptions")
        {
            Content = CreateZaiTranscriptionContent(options, stream: true)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw CreateZaiTranscriptionException("Streaming transcription", response, error);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var dataLines = new List<string>();
        var transcript = new StringBuilder();
        var emittedDone = false;

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                foreach (var streamEvent in ParseZaiTranscriptionEvent(dataLines, transcript))
                {
                    if (streamEvent is OpenAITranscriptionTextDone)
                        emittedDone = true;
                    yield return streamEvent;
                }

                dataLines.Clear();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                dataLines.Add(line["data:".Length..].TrimStart());
        }

        foreach (var streamEvent in ParseZaiTranscriptionEvent(dataLines, transcript))
        {
            if (streamEvent is OpenAITranscriptionTextDone)
                emittedDone = true;
            yield return streamEvent;
        }

        if (!emittedDone)
            yield return new OpenAITranscriptionTextDone { Text = transcript.ToString() };
    }

    private static MultipartFormDataContent CreateZaiTranscriptionContent(
        OpenAITranscriptionRequest options,
        bool stream)
    {
        var content = new MultipartFormDataContent();
        var file = new StreamContent(options.File.OpenReadStream());
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(
            string.IsNullOrWhiteSpace(options.File.ContentType)
                ? "application/octet-stream"
                : options.File.ContentType);
        content.Add(file, "file", string.IsNullOrWhiteSpace(options.File.FileName) ? "audio" : options.File.FileName);
        content.Add(new StringContent(options.Model, Encoding.UTF8), "model");

        if (!string.IsNullOrWhiteSpace(options.Prompt))
            content.Add(new StringContent(options.Prompt, Encoding.UTF8), "prompt");

        content.Add(new StringContent(stream ? "true" : "false", Encoding.UTF8), "stream");
        return content;
    }

    private static IEnumerable<IOpenAITranscriptionStreamEvent> ParseZaiTranscriptionEvent(
        List<string> dataLines,
        StringBuilder transcript)
    {
        if (dataLines.Count == 0)
            yield break;

        var data = string.Join("\n", dataLines).Trim();
        if (string.IsNullOrWhiteSpace(data) || string.Equals(data, "[DONE]", StringComparison.OrdinalIgnoreCase))
            yield break;

        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;
        var type = ReadZaiString(root, "type");

        if (string.Equals(type, "transcript.text.delta", StringComparison.OrdinalIgnoreCase))
        {
            var delta = ReadZaiString(root, "delta") ?? string.Empty;
            transcript.Append(delta);
            yield return new OpenAITranscriptionTextDelta { Delta = delta };
            yield break;
        }

        if (string.Equals(type, "transcript.text.done", StringComparison.OrdinalIgnoreCase))
        {
            var text = ReadZaiString(root, "text")
                ?? ReadZaiString(root, "delta")
                ?? transcript.ToString();
            yield return new OpenAITranscriptionTextDone { Text = text };
            yield break;
        }

        if (string.Equals(type, "error", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Z.AI transcription stream returned an error: {data}");
    }

    private static string? ReadZaiString(JsonElement root, string propertyName)
        => root.ValueKind == JsonValueKind.Object
           && root.TryGetProperty(propertyName, out var property)
           && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static InvalidOperationException CreateZaiTranscriptionException(
        string operation,
        HttpResponseMessage response,
        string raw)
        => new(string.IsNullOrWhiteSpace(raw)
            ? $"Z.AI {operation.ToLowerInvariant()} failed ({(int)response.StatusCode} {response.ReasonPhrase})."
            : $"Z.AI {operation.ToLowerInvariant()} failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}");

}
