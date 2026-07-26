using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using System.Text.Json;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;

namespace AIHappey.Core.Providers.CaseDev;

public partial class CaseDevProvider
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

        ApplyAuthHeader();

        var audio = ReadCaseDevAudioBytes(request.Audio);
        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var fileName = "audio" + GetCaseDevAudioExtension(request.MediaType);
        string? vaultId = null;

        try
        {
            vaultId = await CreateTransientCaseDevVaultAsync(cancellationToken);
            var objectId = await UploadCaseDevVaultObjectAsync(vaultId, fileName, request.MediaType, audio, cancellationToken);
            var (transcriptionId, createBody) = await CreateCaseDevTranscriptionAsync(
                vaultId,
                objectId,
                request.Model,
                metadata,
                cancellationToken);
            var completed = await PollCaseDevTranscriptionAsync(transcriptionId, cancellationToken);
            var resultObjectId = ReadCaseDevString(completed, "result_object_id");
            var result = !string.IsNullOrWhiteSpace(resultObjectId)
                ? await DownloadCaseDevTranscriptAsync(vaultId, resultObjectId, cancellationToken)
                : completed;

            return CreateCaseDevTranscriptionResponse(
                result,
                completed,
                request.Model,
                now,
                warnings,
                createBody);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(vaultId))
            {
                try
                {
                    await DeleteCaseDevVaultAsync(vaultId, CancellationToken.None);
                }
                catch (Exception exception)
                {
                    // The request result remains useful, but surface cleanup failure for operators.
                    warnings.Add(new
                    {
                        type = "cleanup_failed",
                        provider = GetIdentifier(),
                        resource = "vault",
                        vault_id = vaultId,
                        error = exception.Message
                    });
                }
            }
        }
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

    private async Task<string> CreateTransientCaseDevVaultAsync(CancellationToken cancellationToken)
    {
        var payload = new
        {
            name = $"aihappey-transcription-{Guid.NewGuid():N}",
            description = "Temporary AIHappey transcription input; deleted immediately after processing.",
            enableGraph = false,
            enableIndexing = false
        };
        using var response = await _client.PostAsync(
            "/vault",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, MediaTypeNames.Application.Json),
            cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"CaseDev vault creation failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var vaultId = ReadCaseDevString(document.RootElement, "id");
        if (string.IsNullOrWhiteSpace(vaultId))
            throw new InvalidOperationException("CaseDev vault creation response did not contain an id.");

        return vaultId;
    }

    private async Task<string> UploadCaseDevVaultObjectAsync(
        string vaultId,
        string fileName,
        string mediaType,
        byte[] audio,
        CancellationToken cancellationToken)
    {
        var payload = new { filename = fileName, contentType = mediaType, auto_index = false };
        using var createResponse = await _client.PostAsync(
            $"/vault/{Uri.EscapeDataString(vaultId)}/upload",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, MediaTypeNames.Application.Json),
            cancellationToken);
        var createRaw = await createResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!createResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"CaseDev vault upload initialization failed ({(int)createResponse.StatusCode}): {createRaw}");

        using var document = JsonDocument.Parse(createRaw);
        var root = document.RootElement;
        var uploadUrl = ReadCaseDevString(root, "uploadUrl") ?? ReadCaseDevString(root, "upload_url");
        var objectId = ReadCaseDevString(root, "objectId") ?? ReadCaseDevString(root, "object_id");
        if (string.IsNullOrWhiteSpace(uploadUrl) || string.IsNullOrWhiteSpace(objectId))
            throw new InvalidOperationException("CaseDev vault upload initialization response did not contain uploadUrl and objectId.");

        using var put = new HttpRequestMessage(HttpMethod.Put, uploadUrl)
        {
            Content = new ByteArrayContent(audio)
        };
        put.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        using var uploadResponse = await _vaultUploadClient.SendAsync(put, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!uploadResponse.IsSuccessStatusCode)
        {
            var uploadError = await uploadResponse.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"CaseDev vault upload failed ({(int)uploadResponse.StatusCode}): {uploadError}");
        }

        return objectId;
    }

    private async Task<(string Id, string RequestBody)> CreateCaseDevTranscriptionAsync(
        string vaultId,
        string objectId,
        string model,
        JsonElement metadata,
        CancellationToken cancellationToken)
    {
        var payload = CopyCaseDevOptions(metadata);
        // Gateway-level fields always win over raw provider options.
        payload["vault_id"] = vaultId;
        payload["object_id"] = objectId;
        payload["speech_models"] = new[] { NormalizeCaseDevModel(model) };
        payload.Remove("audio_url");

        var requestBody = JsonSerializer.Serialize(payload, CaseDevSpeechJsonOptions);
        using var response = await _client.PostAsync(
            "/voice/transcription",
            new StringContent(requestBody, Encoding.UTF8, MediaTypeNames.Application.Json),
            cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"CaseDev transcription creation failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var id = ReadCaseDevString(document.RootElement, "id");
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException("CaseDev transcription creation response did not contain an id.");

        return (id, requestBody);
    }

    private async Task<JsonElement> PollCaseDevTranscriptionAsync(string transcriptionId, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddMinutes(30);
        var delay = TimeSpan.FromSeconds(1);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var response = await _client.GetAsync($"/voice/transcription/{Uri.EscapeDataString(transcriptionId)}", cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"CaseDev transcription status request failed ({(int)response.StatusCode}): {raw}");

            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var status = ReadCaseDevString(root, "status");
            if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
                return root.Clone();
            if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"CaseDev transcription failed: {raw}");
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("CaseDev transcription did not complete within 30 minutes.");

            await Task.Delay(delay, cancellationToken);
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 1.5, 5));
        }
    }

    private async Task<JsonElement> DownloadCaseDevTranscriptAsync(string vaultId, string objectId, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(
            $"/vault/{Uri.EscapeDataString(vaultId)}/objects/{Uri.EscapeDataString(objectId)}/download",
            cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"CaseDev transcript download failed ({(int)response.StatusCode}): {raw}");

        try
        {
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(new { text = raw });
        }
    }

    private async Task DeleteCaseDevVaultAsync(string vaultId, CancellationToken cancellationToken)
    {
        using var response = await _client.DeleteAsync($"/vault/{Uri.EscapeDataString(vaultId)}?async=false", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"CaseDev vault cleanup failed ({(int)response.StatusCode}): {raw}");
        }
    }

    private TranscriptionResponse CreateCaseDevTranscriptionResponse(
        JsonElement result,
        JsonElement completed,
        string model,
        DateTime timestamp,
        IEnumerable<object> warnings,
        string requestBody)
    {
        var segments = new List<TranscriptionSegment>();
        if (result.TryGetProperty("utterances", out var utterances) && utterances.ValueKind == JsonValueKind.Array)
        {
            foreach (var utterance in utterances.EnumerateArray())
            {
                var text = ReadCaseDevString(utterance, "text");
                if (string.IsNullOrWhiteSpace(text))
                    continue;
                var speaker = ReadCaseDevString(utterance, "speaker");
                segments.Add(new TranscriptionSegment
                {
                    Text = string.IsNullOrWhiteSpace(speaker) ? text : $"{speaker}: {text}",
                    StartSecond = ReadCaseDevMilliseconds(utterance, "start") / 1000f,
                    EndSecond = ReadCaseDevMilliseconds(utterance, "end") / 1000f
                });
            }
        }

        var textValue = ReadCaseDevString(result, "text")
            ?? (segments.Count > 0 ? string.Join("\n", segments.Select(segment => segment.Text)) : string.Empty);
        return new TranscriptionResponse
        {
            Text = textValue,
            Language = ReadCaseDevString(result, "language_code") ?? ReadCaseDevString(completed, "language_code"),
            DurationInSeconds = ReadCaseDevNumber(result, "audio_duration") ?? ReadCaseDevNumber(completed, "audio_duration"),
            Segments = segments,
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new ResponseData
            {
                Timestamp = timestamp,
                ModelId = model.ToModelId(GetIdentifier()),
                Body = result
            },
            Request = new TranscriptionRequestItem { Body = requestBody }
        };
    }

    private static byte[] ReadCaseDevAudioBytes(object audio)
    {
        var encoded = audio switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => audio.ToString()
        };
        if (string.IsNullOrWhiteSpace(encoded))
            throw new ArgumentException("Audio is required.", nameof(audio));

        var comma = encoded.IndexOf(',');
        if (encoded.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0)
            encoded = encoded[(comma + 1)..];
        return Convert.FromBase64String(encoded);
    }

    private string NormalizeCaseDevModel(string model)
    {
        var prefix = GetIdentifier() + "/";
        return model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? model[prefix.Length..]
            : model.Trim();
    }

    private static string GetCaseDevAudioExtension(string mediaType)
        => mediaType.Trim().ToLowerInvariant() switch
        {
            "audio/mpeg" => ".mp3",
            "audio/mp4" or "audio/x-m4a" => ".m4a",
            "audio/wav" or "audio/x-wav" => ".wav",
            "audio/flac" => ".flac",
            "audio/ogg" => ".ogg",
            "audio/opus" => ".opus",
            "audio/webm" or "video/webm" => ".webm",
            "video/mp4" => ".mp4",
            "video/quicktime" => ".mov",
            "video/x-msvideo" => ".avi",
            "video/x-matroska" => ".mkv",
            _ => ".bin"
        };

    private static string? ReadCaseDevString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static float ReadCaseDevMilliseconds(JsonElement element, string name)
        => ReadCaseDevNumber(element, name) ?? 0;

    private static float? ReadCaseDevNumber(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? (float)value.GetDouble()
            : null;

}
