using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.RewindAI;

public partial class RewindAIProvider
{
  
    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.MediaType))
            throw new ArgumentException("MediaType is required.", nameof(request));

        var audio = GetRewindAIAudioBase64(request.Audio);
        ApplyAuthHeader();
        using var form = new MultipartFormDataContent();
        var audioContent = new ByteArrayContent(Convert.FromBase64String(audio));
        audioContent.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);
        form.Add(audioContent, "file", "audio" + GetRewindAIAudioExtension(request.MediaType));

        var rawOptions = GetRewindAIProviderOptions(request.ProviderOptions);
        if (rawOptions is { ValueKind: JsonValueKind.Object })
        {
            foreach (var property in rawOptions.Value.EnumerateObject())
            {
                if (property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                    continue;
                form.Add(new StringContent(property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty
                    : property.Value.GetRawText(), Encoding.UTF8), property.Name);
            }
        }

        using var response = await _client.PostAsync("v1/stt/", form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"RewindAI transcription failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var text = ReadRewindAIString(root, "text", "transcript", "transcription");
        if (string.IsNullOrWhiteSpace(text) && root.TryGetProperty("data", out var data))
            text = ReadRewindAIString(data, "text", "transcript", "transcription");

        return new TranscriptionResponse
        {
            Text = text,
            Language = ReadRewindAIString(root, "language"),
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Request = new TranscriptionRequestItem { Body = rawOptions?.GetRawText() },
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = root
            }
        };
    }

        

    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var responseFormat = options.ResolveOpenAITranscriptionResponseFormat();
        var request = await options.ToTranscriptionRequest(options.Model, GetIdentifier(), cancellationToken);
        var response = await TranscriptionRequest(request, cancellationToken);
        return response.ToOpenAITranscriptionResponse(responseFormat);
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);
        if (!string.IsNullOrWhiteSpace(response.Text))
            yield return new OpenAITranscriptionTextDelta { Delta = response.Text };
        yield return new OpenAITranscriptionTextDone { Text = response.Text };
    }

}
