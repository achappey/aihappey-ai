using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.EverypixelLabs;

public partial class EverypixelLabsProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.MediaType)) throw new ArgumentException("MediaType is required.", nameof(request));

        var audio = request.Audio is JsonElement { ValueKind: JsonValueKind.String } element
            ? element.GetString()
            : request.Audio?.ToString();
        if (string.IsNullOrWhiteSpace(audio)) throw new ArgumentException("Audio is required.", nameof(request));
        if (!audio.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            audio = $"data:{request.MediaType};base64,{audio}";

        var payload = new Dictionary<string, object?> { ["audio_url"] = audio };
        CopyEverypixelProviderOptions(request.ProviderOptions, payload, "language", "hints", "denoise", "callback_url");
        var requestBody = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "v1/transcribe")
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };
        using var createResponse = await _client.SendAsync(createRequest, cancellationToken);
        var createRaw = await createResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!createResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} transcription failed ({(int)createResponse.StatusCode}): {createRaw}");

        var create = DeserializeOrThrow<EverypixelTaskStatusResponse>(createRaw, "transcription create response");
        if (string.IsNullOrWhiteSpace(create.TaskId))
            throw new InvalidOperationException($"{ProviderName} transcription response missing task_id: {createRaw}");
        var final = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            ct => GetTaskStatusAsync(create.TaskId, ct), s => IsTerminalStatus(s.Status),
            TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(15), null, cancellationToken);
        if (!string.Equals(final.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{ProviderName} transcription task failed (task_id={create.TaskId}, status={final.Status}): {final.RawJson}");

        var text = ExtractEverypixelTranscript(final.Result, final.RawRoot);
        return new TranscriptionResponse
        {
            Text = text,
            Segments = [],
            Warnings = [],
            Request = new TranscriptionRequestItem { Body = requestBody },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new { task_id = create.TaskId, create = createRaw, status = final.RawJson }),
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = createResponse.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = final.RawJson
            }
        };
    }

    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAITranscriptionRequest();
        var format = options.ResolveOpenAITranscriptionResponseFormat();
        var request = await options.ToTranscriptionRequest(options.Model, GetIdentifier(), cancellationToken);
        var response = await TranscriptionRequest(request, cancellationToken);
        return response.ToOpenAITranscriptionResponse(format);
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(response.Text))
            yield return new OpenAITranscriptionTextDelta { Delta = response.Text };
        yield return new OpenAITranscriptionTextDone { Text = response.Text };
    }

    private static string ExtractEverypixelTranscript(JsonElement result, JsonElement root)
    {
        if (result.ValueKind == JsonValueKind.String) return result.GetString() ?? string.Empty;
        foreach (var element in new[] { result, root })
        {
            if (element.ValueKind != JsonValueKind.Object) continue;
            foreach (var key in new[] { "text", "transcript", "transcription" })
                if (TryGetPropertyIgnoreCase(element, key, out var value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString() ?? string.Empty;
        }
        return result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? string.Empty : result.GetRawText();
    }
}
