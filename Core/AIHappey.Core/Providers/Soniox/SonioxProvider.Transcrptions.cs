using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Common.Model.Providers.Soniox;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Soniox;

public partial class SonioxProvider
{
    private const int MaxTranscriptionPollAttempts = 300;
    private static readonly TimeSpan TranscriptionPollInterval = TimeSpan.FromSeconds(1);

    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.MediaType))
            throw new ArgumentException("MediaType is required.", nameof(request));

        var audio = Convert.FromBase64String(request.Audio.ToString()!);
        var metadata = request.GetProviderMetadata<SonioxTranscriptionProviderMetadata>(GetIdentifier());
        string? fileId = null;
        string? transcriptionId = null;
        Exception? primaryFailure = null;

        try
        {
            fileId = await UploadFileAsync(audio, request.MediaType, metadata?.ClientReferenceId, cancellationToken);
            transcriptionId = await CreateTranscriptionAsync(request.Model, fileId, metadata, cancellationToken);
            var terminal = await PollTranscriptionAsync(transcriptionId, cancellationToken);
            var transcript = await GetJsonAsync($"v1/transcriptions/{transcriptionId}/transcript", cancellationToken);
            using (transcript)
                return ConvertTranscript(request.Model, terminal, transcript.RootElement);
        }
        catch (Exception ex)
        {
            primaryFailure = ex;
            throw;
        }
        finally
        {
            await CleanupRemoteResourceAsync(transcriptionId is null ? null : $"v1/transcriptions/{transcriptionId}", primaryFailure);
            await CleanupRemoteResourceAsync(fileId is null ? null : $"v1/files/{fileId}", primaryFailure);
        }
    }

    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ValidateOpenAITranscriptionRequest();
        var format = options.ResolveOpenAITranscriptionResponseFormat();
        var request = await options.ToTranscriptionRequest(options.Model, GetIdentifier(), cancellationToken);
        return (await TranscriptionRequest(request, cancellationToken)).ToOpenAITranscriptionResponse(format);
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

    private async Task<string> UploadFileAsync(byte[] audio, string mediaType, string? referenceId, CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(audio);
        content.Headers.ContentType = new(mediaType);
        form.Add(content, "file", "audio" + mediaType.GetAudioExtension());
        if (!string.IsNullOrWhiteSpace(referenceId))
            form.Add(new StringContent(referenceId), "client_reference_id");

        using var response = await _client.PostAsync("v1/files", form, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Soniox file upload failed ({(int)response.StatusCode}): {body}");
        using var document = JsonDocument.Parse(body);
        return ReadString(document.RootElement, "id")
            ?? throw new InvalidOperationException("Soniox file upload response did not contain an id.");
    }

    private async Task<string> CreateTranscriptionAsync(string requestedModel, string fileId,
        SonioxTranscriptionProviderMetadata? metadata, CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = NormalizeSonioxModel(requestedModel),
            ["file_id"] = fileId,
            ["language_hints"] = metadata?.LanguageHints,
            ["language_hints_strict"] = metadata?.LanguageHintsStrict,
            ["enable_speaker_diarization"] = metadata?.EnableSpeakerDiarization,
            ["enable_language_identification"] = metadata?.EnableLanguageIdentification,
            ["context"] = metadata?.Context,
            ["client_reference_id"] = metadata?.ClientReferenceId
        };
        using var response = await _client.PostAsJsonAsync("v1/transcriptions", payload, SonioxJsonOptions, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Soniox transcription creation failed ({(int)response.StatusCode}): {body}");
        using var document = JsonDocument.Parse(body);
        return ReadString(document.RootElement, "id")
            ?? throw new InvalidOperationException("Soniox transcription response did not contain an id.");
    }

    private async Task<JsonElement> PollTranscriptionAsync(string id, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxTranscriptionPollAttempts; attempt++)
        {
            using var result = await GetJsonAsync($"v1/transcriptions/{id}", cancellationToken);
            var root = result.RootElement.Clone();
            var status = ReadString(root, "status");
            if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
                return root;
            if (string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Soniox transcription failed: {ReadString(root, "error_type")}: {ReadString(root, "error_message")}");
            await Task.Delay(TranscriptionPollInterval, cancellationToken);
        }
        throw new TimeoutException($"Soniox transcription polling exceeded {MaxTranscriptionPollAttempts} attempts.");
    }

    private TranscriptionResponse ConvertTranscript(string requestedModel, JsonElement terminal, JsonElement transcript)
    {
        var segments = ReadArray(transcript, "tokens").Select(token => new TranscriptionSegment
        {
            Text = ReadString(token, "text") ?? string.Empty,
            StartSecond = ReadNumber(token, "start_ms") / 1000f,
            EndSecond = ReadNumber(token, "end_ms") / 1000f
        }).ToArray();
        var durationMs = ReadNullableNumber(terminal, "audio_duration_ms");
        var language = ReadArray(transcript, "tokens")
            .Select(x => ReadString(x, "language"))
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

        return new TranscriptionResponse
        {
            Text = ReadString(transcript, "text") ?? string.Empty,
            Segments = segments,
            Language = language,
            DurationInSeconds = durationMs is null ? null : durationMs / 1000f,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                ModelId = requestedModel.ToModelId(GetIdentifier()),
                Body = transcript.Clone()
            }
        };
    }

    private async Task CleanupRemoteResourceAsync(string? path, Exception? primaryFailure)
    {
        if (path is null)
            return;
        try
        {
            using var response = await _client.DeleteAsync(path, CancellationToken.None);
            if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound && primaryFailure is null)
            {
                var body = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"Soniox cleanup of {path} failed ({(int)response.StatusCode}): {body}");
            }
        }
        catch when (primaryFailure is not null)
        {
            // Preserve the original provider/cancellation failure while still attempting every cleanup step.
        }
    }

    private static float ReadNumber(JsonElement element, string property)
        => ReadNullableNumber(element, property) ?? 0;

    private static float? ReadNullableNumber(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.Number
            ? (float)value.GetDouble()
            : null;
}
