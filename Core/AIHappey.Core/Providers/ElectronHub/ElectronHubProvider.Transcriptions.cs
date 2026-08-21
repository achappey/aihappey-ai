using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.ElectronHub;

public partial class ElectronHubProvider
{
    private const string ElectronHubTranscriptionsEndpoint = "v1/audio/transcriptions";

    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MediaType);
        var encoded = request.Audio is JsonElement { ValueKind: JsonValueKind.String } element ? element.GetString() : request.Audio?.ToString();
        if (string.IsNullOrWhiteSpace(encoded)) throw new ArgumentException("Audio is required.", nameof(request));
        var comma = encoded.IndexOf(',');
        if (encoded.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0) encoded = encoded[(comma + 1)..];
        byte[] audio;
        try { audio = Convert.FromBase64String(encoded); }
        catch (FormatException exception) { throw new ArgumentException("Audio must be valid base64.", nameof(request), exception); }

        using var form = new MultipartFormDataContent();
        using var file = new ByteArrayContent(audio);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(request.MediaType);
        form.Add(file, "file", "audio" + request.MediaType.GetAudioExtension());
        form.Add(new StringContent(request.Model, Encoding.UTF8), "model");
        ElectronHubAddFormOptions(form, request.ProviderOptions);

        ApplyAuthHeader();
        using var response = await _client.PostAsync(ElectronHubTranscriptionsEndpoint, form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"ElectronHub transcription failed ({(int)response.StatusCode}): {raw}");
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var text = root.TryGetProperty("text", out var textElement) ? textElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("ElectronHub transcription response did not include text.");

        return new TranscriptionResponse
        {
            Text = text,
            DurationInSeconds = root.TryGetProperty("duration", out var duration) && duration.TryGetSingle(out var seconds) ? seconds : null,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new ResponseData { Timestamp = DateTime.UtcNow, Headers = response.GetHeaders(), ModelId = request.Model.ToModelId(GetIdentifier()), Body = root },
            Request = new TranscriptionRequestItem { Body = JsonSerializer.Serialize(new { request.Model, request.MediaType, audioBytes = audio.Length, request.ProviderOptions }) }
        };
    }

    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ApplyAuthHeader();
        return _client.OpenAICompatibleTranscriptionRequestAsync(options, ElectronHubTranscriptionsEndpoint, cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);
        if (!string.IsNullOrWhiteSpace(response.Text)) yield return new OpenAITranscriptionTextDelta { Delta = response.Text };
        yield return new OpenAITranscriptionTextDone { Text = response.Text };
    }

    private static void ElectronHubAddFormOptions(MultipartFormDataContent form, IReadOnlyDictionary<string, JsonElement>? options)
    {
        if (options is null || !options.TryGetValue("electronhub", out var value) || value.ValueKind != JsonValueKind.Object) return;
        foreach (var property in value.EnumerateObject())
        {
            if (property.NameEquals("file") || property.NameEquals("model")) continue;
            var text = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.GetRawText();
            if (text is not null) form.Add(new StringContent(text, Encoding.UTF8), property.Name);
        }
    }
}
