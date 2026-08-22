using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.MCP.Media;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.MumeAI;

public partial class MumeAIProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.MediaType)) throw new ArgumentException("MediaType is required.", nameof(request));

        var audio = request.Audio is JsonElement element && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : request.Audio?.ToString();
        if (string.IsNullOrWhiteSpace(audio)) throw new ArgumentException("Audio is required.", nameof(request));
        if (MediaContentHelpers.TryParseDataUrl(audio, out _, out var parsedBase64)) audio = parsedBase64;

        using var form = new MultipartFormDataContent();
        AddMumeTranscriptionOptions(form, GetMumeProviderOptions(request.ProviderOptions));
        AddMumeFormValue(form, "model", request.Model);
        var file = new ByteArrayContent(Convert.FromBase64String(audio));
        file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);
        form.Add(file, "file", "audio" + request.MediaType.GetAudioExtension());

        ApplyAuthHeader();
        using var response = await _client.PostAsync("v1/audio/transcriptions", form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Mume AI transcription failed ({(int)response.StatusCode}): {raw}");
        return ConvertMumeTranscription(raw, request.Model, response.GetHeaders());
    }

    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAITranscriptionRequest();
        var responseFormat = options.ResolveOpenAITranscriptionResponseFormat();
        var request = await options.ToTranscriptionRequest(options.Model, GetIdentifier(), cancellationToken);
        var metadata = MumePayload(options.AdditionalProperties);
        metadata["language"] = options.Language;
        metadata["prompt"] = options.Prompt;
        metadata["response_format"] = options.ResponseFormat;
        metadata["temperature"] = options.Temperature;
        request.ProviderOptions = new Dictionary<string, JsonElement>
        {
            [GetIdentifier()] = JsonSerializer.SerializeToElement(metadata)
        };
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

    private static void AddMumeTranscriptionOptions(MultipartFormDataContent form, JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object) return;
        foreach (var property in metadata.EnumerateObject())
        {
            if (property.NameEquals("model") || property.NameEquals("file")) continue;
            var value = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()
                : property.Value.GetRawText();
            AddMumeFormValue(form, property.Name, value);
        }
    }

    private static void AddMumeFormValue(MultipartFormDataContent form, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) form.Add(new StringContent(value), name);
    }

    private TranscriptionResponse ConvertMumeTranscription(string raw, string model, IDictionary<string, string> headers)
    {
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var duration = root.TryGetProperty("usage", out var usage) ? MumeNumber(usage, "seconds") : null;
        return new TranscriptionResponse
        {
            Text = MumeString(root, "text") ?? string.Empty,
            Language = MumeString(root, "language"),
            DurationInSeconds = duration.HasValue ? (float)duration.Value : null,
            Segments = [],
            Warnings = [],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = headers,
                ModelId = model.ToModelId(GetIdentifier()),
                Body = raw
            }
        };
    }

}
