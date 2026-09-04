using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.PawaAI;

public partial class PawaAIProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.MediaType))
            throw new ArgumentException("MediaType is required.", nameof(request));

        var encoded = request.Audio switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => request.Audio?.ToString()
        };
        if (string.IsNullOrWhiteSpace(encoded))
            throw new ArgumentException("Audio is required.", nameof(request));
        var comma = encoded.IndexOf(',');
        if (encoded.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0)
            encoded = encoded[(comma + 1)..];
        var audio = Convert.FromBase64String(encoded);

        var options = GetPawaOptions(request.ProviderOptions);
        using var form = new MultipartFormDataContent();
        var audioContent = new ByteArrayContent(audio);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);
        form.Add(audioContent, "files", "audio" + request.MediaType.GetAudioExtension());
        form.Add(new StringContent(NormalizePawaModelId(request.Model)), "model");

        var language = ReadPawaString(options, "language") ?? "English";
        var diarization = ReadPawaBoolean(options, "is_speaker_diarization") ?? false;
        form.Add(new StringContent(language), "language");
        form.Add(new StringContent(diarization ? "true" : "false"), "is_speaker_diarization");
        AddPawaFormOption(form, options, "prompt");
        AddPawaFormOption(form, options, "temperature");

        ApplyAuthHeader();
        using var response = await _client.PostAsync("v1/voice/speech-to-text", form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsurePawaSuccess(response, raw, "transcription request");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var text = ExtractPawaTranscription(root);
        return new TranscriptionResponse
        {
            Text = text,
            Language = language,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Request = new TranscriptionRequestItem { Body = "multipart/form-data" },
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = root
            }
        };
    }

    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var format = options.ResolveOpenAITranscriptionResponseFormat();
        var request = await options.ToTranscriptionRequest(options.Model, GetIdentifier(), cancellationToken);
        var response = await TranscriptionRequest(request, cancellationToken);
        return response.ToOpenAITranscriptionResponse(format);
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

    private static string ExtractPawaTranscription(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data)
            || !data.TryGetProperty("transcriptions", out var transcriptions))
            return string.Empty;

        if (transcriptions.ValueKind == JsonValueKind.Array)
            return string.Join("\n", transcriptions.EnumerateArray()
                .Select(item => item.TryGetProperty("transcript", out var transcript) ? transcript.GetString() : null)
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        if (transcriptions.ValueKind == JsonValueKind.Object
            && transcriptions.TryGetProperty("transcript", out var single))
            return single.GetString() ?? string.Empty;
        if (transcriptions.ValueKind == JsonValueKind.String)
            return transcriptions.GetString() ?? string.Empty;
        return string.Empty;
    }

    private static string? ReadPawaString(JsonElement options, string name)
        => options.ValueKind == JsonValueKind.Object
           && options.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? ReadPawaBoolean(JsonElement options, string name)
        => options.ValueKind == JsonValueKind.Object
           && options.TryGetProperty(name, out var value)
           && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static void AddPawaFormOption(MultipartFormDataContent form, JsonElement options, string name)
    {
        if (options.ValueKind != JsonValueKind.Object || !options.TryGetProperty(name, out var value))
            return;
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        if (!string.IsNullOrWhiteSpace(text))
            form.Add(new StringContent(text, Encoding.UTF8), name);
    }
}
