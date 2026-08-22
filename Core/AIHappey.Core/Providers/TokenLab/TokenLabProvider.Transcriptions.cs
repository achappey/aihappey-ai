using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.TokenLab;

public partial class TokenLabProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.MediaType)) throw new ArgumentException("MediaType is required.", nameof(request));
        var audio = request.Audio is JsonElement { ValueKind: JsonValueKind.String } element ? element.GetString() : request.Audio?.ToString();
        if (string.IsNullOrWhiteSpace(audio)) throw new ArgumentException("Audio is required.", nameof(request));
        if (audio.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) audio = audio[(audio.IndexOf(',') + 1)..];

        using var form = new MultipartFormDataContent();
        AddFormValue(form, "model", request.Model);
        AddProviderFormValues(form, GetTokenLabProviderOptions(request.ProviderOptions));
        var file = new ByteArrayContent(Convert.FromBase64String(audio));
        file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);
        form.Add(file, "file", "audio" + GetAudioExtension(request.MediaType));
        var result = await SendTokenLabJsonAsync(HttpMethod.Post, "v1/audio/transcriptions", form, "transcription", cancellationToken);
        var text = FindTokenLabString(result.Root, "text", "transcription")
            ?? throw new InvalidOperationException($"TokenLab transcription returned no text: {result.Root.GetRawText()}");

        return new TranscriptionResponse
        {
            Text = text,
            Language = FindTokenLabString(result.Root, "language"),
            ProviderMetadata = CreateTokenLabMetadata(result.Root),
            Request = new TranscriptionRequestItem { Body = "multipart/form-data" },
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Headers = result.Headers,
                Body = result.Root
            }
        };
    }

    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.File is null) throw new ArgumentException("File is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Model)) throw new ArgumentException("Model is required.", nameof(options));

        using var form = new MultipartFormDataContent();
        AddFormValue(form, "model", options.Model);
        AddFormValue(form, "language", options.Language);
        AddFormValue(form, "prompt", options.Prompt);
        AddFormValue(form, "response_format", options.ResponseFormat);
        AddFormValue(form, "temperature", options.Temperature);
        if (options.TimestampGranularities is not null)
            foreach (var value in options.TimestampGranularities) AddFormValue(form, "timestamp_granularities[]", value);
        if (options.Include is not null)
            foreach (var value in options.Include) AddFormValue(form, "include[]", value);
        if (options.ChunkingStrategy is not null) AddFormValue(form, "chunking_strategy", JsonSerializer.Serialize(options.ChunkingStrategy, TokenLabJson));
        AddAdditionalFormValues(form, options.AdditionalProperties);
        form.Add(ToFileContent(options.File.ContentType, options.File.OpenReadStream()), "file", options.File.FileName);

        var result = await SendTokenLabJsonAsync(HttpMethod.Post, "v1/audio/transcriptions", form, "transcription", cancellationToken);
        return JsonSerializer.Deserialize<OpenAITranscriptionResponse>(result.Root.GetRawText(), TokenLabJson)
            ?? throw new InvalidOperationException("TokenLab transcription returned an empty response.");
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);
        if (!string.IsNullOrWhiteSpace(response.Text)) yield return new OpenAITranscriptionTextDelta { Delta = response.Text };
        yield return new OpenAITranscriptionTextDone { Text = response.Text };
    }

    private static void AddProviderFormValues(MultipartFormDataContent form, JsonElement? options)
    {
        if (options is not { ValueKind: JsonValueKind.Object } value) return;
        foreach (var property in value.EnumerateObject())
            form.Add(new StringContent(property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()! : property.Value.GetRawText()), property.Name);
    }

    private static string GetAudioExtension(string mediaType) => mediaType.ToLowerInvariant() switch
    {
        "audio/mpeg" or "audio/mp3" => ".mp3",
        "audio/wav" or "audio/x-wav" => ".wav",
        "audio/ogg" => ".ogg",
        "audio/webm" => ".webm",
        "audio/mp4" or "audio/m4a" => ".m4a",
        _ => ".bin"
    };
}
