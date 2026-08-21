using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.OneKey;

public partial class OneKeyProvider
{
    private const string OneKeyTranscriptionsEndpoint = "v1/audio/transcriptions";
    private static readonly HashSet<string> OneKeyTranscriptionReserved =
        new(["file", "model", "response_format"], StringComparer.OrdinalIgnoreCase);

    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.MediaType)) throw new ArgumentException("MediaType is required.", nameof(request));
        var audio = request.Audio is JsonElement { ValueKind: JsonValueKind.String } element ? element.GetString() : request.Audio?.ToString();
        if (string.IsNullOrWhiteSpace(audio)) throw new ArgumentException("Audio is required.", nameof(request));

        using var form = new MultipartFormDataContent();
        AddOneKeyTranscriptionMetadata(form, request.GetProviderMetadata<JsonElement>(GetIdentifier()));
        var file = new ByteArrayContent(Convert.FromBase64String(audio.RemoveDataUrlPrefix()));
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(request.MediaType);
        form.Add(file, "file", "audio" + request.MediaType.GetAudioExtension());
        form.Add(new StringContent(request.Model, Encoding.UTF8), "model");
        form.Add(new StringContent("verbose_json", Encoding.UTF8), "response_format");

        ApplyAuthHeader();
        using var response = await _client.PostAsync(OneKeyTranscriptionsEndpoint, form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"OneKey transcription failed ({(int)response.StatusCode}): {raw}");
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        return new TranscriptionResponse
        {
            Text = ReadOneKeyString(root, "text") ?? string.Empty,
            Language = ReadOneKeyString(root, "language"),
            DurationInSeconds = ReadOneKeyFloat(root, "duration"),
            Segments = ReadOneKeySegments(root),
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow, Headers = response.GetHeaders(), ModelId = request.Model.ToModelId(GetIdentifier()), Body = root
            }
        };
    }

    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return _client.OpenAICompatibleTranscriptionRequestAsync(options, OneKeyTranscriptionsEndpoint, cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);
        if (!string.IsNullOrEmpty(response.Text)) yield return new OpenAITranscriptionTextDelta { Delta = response.Text };
        yield return new OpenAITranscriptionTextDone { Text = response.Text };
    }

    private static void AddOneKeyTranscriptionMetadata(MultipartFormDataContent form, JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object) return;
        foreach (var property in metadata.EnumerateObject())
        {
            if (OneKeyTranscriptionReserved.Contains(property.Name)) continue;
            var value = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.GetRawText();
            if (value is not null) form.Add(new StringContent(value, Encoding.UTF8), property.Name);
        }
    }
    private static string? ReadOneKeyString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static float? ReadOneKeyFloat(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? (float)value.GetDouble() : null;
    private static List<TranscriptionSegment> ReadOneKeySegments(JsonElement root)
    {
        var result = new List<TranscriptionSegment>();
        if (!root.TryGetProperty("segments", out var segments) || segments.ValueKind != JsonValueKind.Array) return result;
        foreach (var segment in segments.EnumerateArray()) result.Add(new TranscriptionSegment
        {
            Text = ReadOneKeyString(segment, "text") ?? string.Empty,
            StartSecond = ReadOneKeyFloat(segment, "start") ?? 0,
            EndSecond = ReadOneKeyFloat(segment, "end") ?? 0
        });
        return result;
    }
}
