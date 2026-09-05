using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.HiNow;

public partial class HiNowProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var audioText = request.Audio?.ToString() ?? throw new ArgumentException("Audio is required.", nameof(request));
        var comma = audioText.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? audioText.IndexOf(',') : -1;
        var bytes = Convert.FromBase64String(comma >= 0 ? audioText[(comma + 1)..] : audioText);
        var result = await SendHiNowTranscriptionAsync(request.Model, bytes, request.MediaType, request.ProviderOptions, cancellationToken);
        return ToHiNowTranscription(result, request.Model);
    }

    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAITranscriptionRequest();
        await using var stream = options.File.OpenReadStream();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);
        var result = await SendHiNowTranscriptionAsync(options.Model, memory.ToArray(), options.File.ContentType ?? "audio/mpeg", options.AdditionalProperties, cancellationToken);
        return new OpenAITranscriptionResponse { Text = GetHiNowString(result.Root, "text") ?? string.Empty };
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);
        yield return new OpenAITranscriptionTextDelta { Delta = response.Text };
        yield return new OpenAITranscriptionTextDone { Text = response.Text };
    }

    private async Task<HiNowJsonResult> SendHiNowTranscriptionAsync(
        string model, byte[] audio, string mediaType, Dictionary<string, JsonElement>? options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("Model is required.", nameof(model));
        ApplyAuthHeader();
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audio);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
        form.Add(file, "file", "audio" + GetHiNowAudioExtension(mediaType));
        form.Add(new StringContent(model), "model");
        if (options is not null && options.TryGetValue(GetIdentifier(), out var provider) && provider.ValueKind == JsonValueKind.Object)
            foreach (var property in provider.EnumerateObject()) AddHiNowFormValue(form, property.Name, property.Value);
        else foreach (var property in options ?? []) AddHiNowFormValue(form, property.Key, property.Value);
        using var response = await _client.PostAsync("v1/audio/transcriptions", form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"HiNow transcription failed ({(int)response.StatusCode}): {raw}");
        using var document = JsonDocument.Parse(raw);
        return new HiNowJsonResult(document.RootElement.Clone(), response.GetHeaders());
    }

    private TranscriptionResponse ToHiNowTranscription(HiNowJsonResult result, string model) => new()
    {
        Text = GetHiNowString(result.Root, "text") ?? string.Empty,
        DurationInSeconds = result.Root.TryGetProperty("duration", out var duration) && duration.TryGetSingle(out var seconds) ? seconds : null,
        Segments = [], Warnings = [],
        ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root),
        Response = new ResponseData { Timestamp = DateTime.UtcNow, Headers = result.Headers, ModelId = model.ToModelId(GetIdentifier()), Body = result.Root },
        Request = new TranscriptionRequestItem { Body = "multipart/form-data" }
    };

    private static void AddHiNowFormValue(MultipartFormDataContent form, string name, JsonElement value)
    {
        if (name is "model" or "file" || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return;
        form.Add(new StringContent(value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText()), name);
    }

    private static string GetHiNowAudioExtension(string mediaType) => mediaType.ToLowerInvariant() switch
    { "audio/wav" or "audio/x-wav" => ".wav", "audio/ogg" => ".ogg", _ => ".mp3" };
}
