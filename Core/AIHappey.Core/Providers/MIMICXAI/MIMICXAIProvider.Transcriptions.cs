using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.MIMICXAI;

public partial class MIMICXAIProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (bytes, mediaType) = DecodeAudio(request.Audio, request.MediaType);
        var language = GetProviderString(request.ProviderOptions, "language");
        var result = await TranscribeAsync(request.Model, bytes, mediaType, language, cancellationToken);
        return ToTranscriptionResponse(result, request.Model);
    }

    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        await using var stream = options.File.OpenReadStream();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);
        var result = await TranscribeAsync(options.Model, memory.ToArray(), options.File.ContentType ?? "audio/mpeg", options.Language, cancellationToken);
        return string.Equals(options.ResponseFormat, "verbose_json", StringComparison.OrdinalIgnoreCase)
            ? new OpenAITranscriptionVerboseResponse { Text = ReadText(result.Root), Language = GetString(result.Root, "language") ?? string.Empty, Duration = ReadDouble(result.Root, "duration") }
            : new OpenAITranscriptionResponse { Text = ReadText(result.Root) };
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);
        if (!string.IsNullOrWhiteSpace(response.Text)) yield return new OpenAITranscriptionTextDelta { Delta = response.Text };
        yield return new OpenAITranscriptionTextDone { Text = response.Text };
    }

    private async Task<MimicXJsonResult> TranscribeAsync(string? model, byte[] audio, string mediaType, string? language,
        CancellationToken cancellationToken)
    {
        if (NormalizeAgentModel(model) != "Nova") throw new NotSupportedException("MIMICXAI transcription requires Nova.");
        ApplyAuthHeader();
        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(audio);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
        form.Add(content, "audio", AudioFileName(mediaType));
        if (!string.IsNullOrWhiteSpace(language)) form.Add(new StringContent(language), "language");
        using var response = await _client.PostAsync("v1/transcribe", form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"MIMICXAI transcription failed ({(int)response.StatusCode}): {raw}");
        using var document = JsonDocument.Parse(raw);
        ThrowAgentPayloadError(document.RootElement);
        return new MimicXJsonResult(document.RootElement.Clone(), response.GetHeaders());
    }

    private TranscriptionResponse ToTranscriptionResponse(MimicXJsonResult result, string model)
    {
        var segments = result.Root.TryGetProperty("segments", out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Select(value => new TranscriptionSegment { Text = ReadText(value), StartSecond = (float)ReadDouble(value, "start"), EndSecond = (float)ReadDouble(value, "end") }).ToList()
            : [];
        return new TranscriptionResponse
        {
            Text = ReadText(result.Root), Language = GetString(result.Root, "language"), DurationInSeconds = (float?)ReadNullableDouble(result.Root, "duration"), Segments = segments,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root),
            Response = new ResponseData { Timestamp = DateTime.UtcNow, Headers = result.Headers, ModelId = model.ToModelId(GetIdentifier()), Body = result.Root }
        };
    }

    private static string ReadText(JsonElement root) => GetString(root, "text") ?? GetString(root, "transcription") ?? string.Empty;
    private static double ReadDouble(JsonElement root, string name) => ReadNullableDouble(root, name) ?? 0;
    private static double? ReadNullableDouble(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) ? number : null;
    private static string? GetProviderString(Dictionary<string, JsonElement>? options, string name)
        => options is not null && options.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
