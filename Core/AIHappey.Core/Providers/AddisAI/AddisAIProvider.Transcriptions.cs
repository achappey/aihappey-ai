using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.AddisAI;

public partial class AddisAIProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.MediaType))
            throw new ArgumentException("MediaType is required.", nameof(request));

        var audio = request.Audio switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => request.Audio?.ToString()
        };
        if (string.IsNullOrWhiteSpace(audio))
            throw new ArgumentException("Audio is required.", nameof(request));

        if (audio.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var commaIndex = audio.IndexOf(',');
            if (commaIndex < 0)
                throw new ArgumentException("Audio data URL is invalid.", nameof(request));
            audio = audio[(commaIndex + 1)..];
        }

        var language = GetSttLanguage(request.Model);
        var audioBytes = Convert.FromBase64String(audio);
        using var form = new MultipartFormDataContent();
        var audioContent = new ByteArrayContent(audioBytes);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);
        form.Add(audioContent, "audio", "audio" + GetAudioExtension(request.MediaType));
        var requestData = JsonSerializer.Serialize(new { language_code = language }, AddisJson);
        form.Add(new StringContent(requestData), "request_data");

        ApplyAuthHeader();
        using var response = await _client.PostAsync("v2/stt", form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"AddisAI transcription failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"AddisAI transcription response did not include data: {raw}");

        var transcription = GetRequiredString(data, "transcription", raw);
        return new TranscriptionResponse
        {
            Text = transcription,
            Language = language,
            ProviderMetadata = new()
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(new
                {
                    language,
                    confidence = root.TryGetProperty("confidence", out var confidence) ? confidence.Clone() : default,
                    response = root.Clone()
                }, AddisJson)
            },
            Request = new TranscriptionRequestItem { Body = requestData },
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = root.Clone()
            }
        };
    }

    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
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
