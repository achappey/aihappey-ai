using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.MCP.Media;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.NagaAI;

public partial class NagaAIProvider
{
    private const string NagaAITranscriptionsEndpoint = "v1/audio/transcriptions";

    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var audioString = request.Audio switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => request.Audio?.ToString()
        };
        if (string.IsNullOrWhiteSpace(audioString))
            throw new ArgumentException("Audio is required.", nameof(request));
        if (MediaContentHelpers.TryParseDataUrl(audioString, out _, out var base64))
            audioString = base64;

        byte[] audio;
        try
        {
            audio = Convert.FromBase64String(audioString);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Audio must be Base64 or a Base64 data URL.", nameof(request), exception);
        }

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        using var form = new MultipartFormDataContent();
        AddNagaAIFormProperties(form, metadata, ["model", "file", "prompt", "language"]);
        form.Add(new StringContent(request.Model, Encoding.UTF8), "model");

        var mediaType = string.IsNullOrWhiteSpace(request.MediaType)
            ? "application/octet-stream"
            : request.MediaType;
        var extension = ".bin";
        try { extension = mediaType.GetAudioExtension(); }
        catch (NotSupportedException) { }
        var file = new ByteArrayContent(audio);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
        form.Add(file, "file", "audio" + extension);

        ApplyAuthHeader();
        var now = DateTime.UtcNow;
        using var response = await _client.PostAsync(NagaAITranscriptionsEndpoint, form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"NagaAI transcription failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        return new TranscriptionResponse
        {
            Text = ReadNagaAIString(root, "text") ?? string.Empty,
            Language = ReadNagaAIString(root, "language"),
            DurationInSeconds = ReadNagaAIFloat(root, "duration"),
            Segments = ReadNagaAISegments(root),
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Headers = response.GetHeaders(),
                Body = root
            }
        };
    }

    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ApplyAuthHeader();
        return _client.OpenAICompatibleTranscriptionRequestAsync(
            options,
            NagaAITranscriptionsEndpoint,
            cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(response.Text))
            yield return new OpenAITranscriptionTextDelta { Delta = response.Text };
        yield return new OpenAITranscriptionTextDone { Text = response.Text };
    }

    private static void AddNagaAIFormProperties(
        MultipartFormDataContent form,
        JsonElement metadata,
        IEnumerable<string> reservedNames)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return;
        var reserved = new HashSet<string>(reservedNames, StringComparer.OrdinalIgnoreCase);
        foreach (var property in metadata.EnumerateObject())
        {
            if (reserved.Contains(property.Name)
                || property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                continue;
            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in property.Value.EnumerateArray())
                    form.Add(new StringContent(NagaAIFormValue(item), Encoding.UTF8), property.Name + "[]");
            }
            else
            {
                form.Add(new StringContent(NagaAIFormValue(property.Value), Encoding.UTF8), property.Name);
            }
        }
    }

    private static string NagaAIFormValue(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => value.GetRawText()
        };

    private static List<TranscriptionSegment> ReadNagaAISegments(JsonElement root)
    {
        if (!root.TryGetProperty("segments", out var segments) || segments.ValueKind != JsonValueKind.Array)
            return [];
        return segments.EnumerateArray().Select(segment => new TranscriptionSegment
        {
            Text = ReadNagaAIString(segment, "text") ?? string.Empty,
            StartSecond = ReadNagaAIFloat(segment, "start") ?? 0,
            EndSecond = ReadNagaAIFloat(segment, "end") ?? 0
        }).ToList();
    }

    private static float? ReadNagaAIFloat(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out var number))
            return number;
        return value.ValueKind == JsonValueKind.String
            && float.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                ? number
                : null;
    }
}
