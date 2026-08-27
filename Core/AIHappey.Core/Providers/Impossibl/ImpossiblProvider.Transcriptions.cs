using AIHappey.Core.AI;
using AIHappey.Common.Extensions;
using AIHappey.Core.Extensions;
using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Impossibl;

public partial class ImpossiblProvider
{
    private const int ImpossiblMaxAudioBytes = 25 * 1024 * 1024;

    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.MediaType)) throw new ArgumentException("MediaType is required.", nameof(request));

        var audioValue = request.Audio is JsonElement { ValueKind: JsonValueKind.String } element
            ? element.GetString() : request.Audio?.ToString();
        if (string.IsNullOrWhiteSpace(audioValue)) throw new ArgumentException("Audio is required.", nameof(request));
        var bytes = Convert.FromBase64String(audioValue.RemoveDataUrlPrefix());
        if (bytes.Length > ImpossiblMaxAudioBytes)
            throw new ArgumentOutOfRangeException(nameof(request), "Impossibl transcription files must not exceed 25 MB.");

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(request.MediaType);
        form.Add(file, "file", "audio" + request.MediaType.GetAudioExtension());
        form.Add(new StringContent(request.Model), "model");

        if (request.ProviderOptions is not null
            && request.ProviderOptions.TryGetValue(GetIdentifier(), out var metadata)
            && metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in metadata.EnumerateObject())
                AddImpossiblTranscriptionField(form, property);
        }

        ApplyAuthHeader();
        using var response = await _client.PostAsync("v1/audio/transcriptions", form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Impossibl transcription failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var language = root.TryGetProperty("languages", out var languages) && languages.ValueKind == JsonValueKind.Array
            ? languages.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("code", out var code) ? code.GetString() : null).FirstOrDefault(value => value is not null)
            : null;

        return new TranscriptionResponse
        {
            Text = root.TryGetProperty("text", out var text) ? text.GetString() ?? string.Empty : string.Empty,
            Language = language,
            Segments = [],
            Warnings = [],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = root
            }
        };
    }


    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return _client.OpenAICompatibleTranscriptionRequestAsync(options, "v1/audio/transcriptions", cancellationToken);
    }

    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return _client.OpenAICompatibleTranscriptionStreamingAsync(options, "v1/audio/transcriptions", cancellationToken);
    }

    private static void AddImpossiblTranscriptionField(MultipartFormDataContent form, JsonProperty property)
    {
        if (property.NameEquals("file") || property.NameEquals("model") || property.NameEquals("stream")) return;
        if (property.Value.ValueKind == JsonValueKind.Array
            && (property.NameEquals("keywords") || property.NameEquals("keywords[]")
                || property.NameEquals("languages") || property.NameEquals("languages[]")))
        {
            var fieldName = property.Name.EndsWith("[]", StringComparison.Ordinal) ? property.Name : property.Name + "[]";
            foreach (var item in property.Value.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } value)
                    form.Add(new StringContent(value), fieldName);
            return;
        }

        var fieldValue = property.Value.ValueKind == JsonValueKind.String
            ? property.Value.GetString() : property.Value.GetRawText();
        if (fieldValue is not null) form.Add(new StringContent(fieldValue), property.Name);
    }
}
