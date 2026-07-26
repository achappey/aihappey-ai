using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Mistral;

public partial class MistralProvider
{


    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAITranscriptionRequest();

        var responseFormat = options.ResolveOpenAITranscriptionResponseFormat();
        var request = await options.ToTranscriptionRequest(options.Model, GetIdentifier(), cancellationToken);
        var response = await TranscriptionRequest(request, cancellationToken);

        return response.ToOpenAITranscriptionResponse(responseFormat);
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAITranscriptionRequest();

        var request = await options.ToTranscriptionRequest(options.Model, GetIdentifier(), cancellationToken);
        ApplyAuthHeader();

        var bytes = Convert.FromBase64String(request.Audio.ToString()!);
        using var form = new MultipartFormDataContent();
        using var audioContent = new ByteArrayContent(bytes);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);
        form.Add(audioContent, "file", "audio" + request.MediaType.GetAudioExtension());
        form.Add(new StringContent(request.Model), "model");
        form.Add(new StringContent("true"), "stream");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/audio/transcriptions")
        {
            Content = form
        };
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _client.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"{GetName()} streaming transcription request failed ({(int)response.StatusCode}): {error}");
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

            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            var type = GetJsonString(root, "type");
            if (string.Equals(type, "transcription.text.delta", StringComparison.OrdinalIgnoreCase))
            {
                var delta = GetJsonString(root, "text");
                if (!string.IsNullOrWhiteSpace(delta))
                {
                    transcript.Append(delta);
                    yield return new OpenAITranscriptionTextDelta { Delta = delta };
                }
            }
            else if (string.Equals(type, "transcription.done", StringComparison.OrdinalIgnoreCase))
            {
                completedText = GetJsonString(root, "text");
            }
        }

        var finalText = transcript.Length > 0 ? transcript.ToString() : completedText;
        if (!string.IsNullOrWhiteSpace(finalText))
            yield return new OpenAITranscriptionTextDone { Text = finalText };
    }

    private static string? GetJsonString(JsonElement element, string name)
        => element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
