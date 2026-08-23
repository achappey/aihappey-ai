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

namespace AIHappey.Core.Providers.Token360;

public partial class Token360Provider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var providerOptions = request.ProviderOptions?.TryGetValue(GetIdentifier(), out var options) == true
            ? options
            : default(JsonElement?);
        var hasFileUrl = providerOptions is { ValueKind: JsonValueKind.Object }
            && providerOptions.Value.TryGetProperty("file_url", out var fileUrl)
            && fileUrl.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(fileUrl.GetString());

        using var form = new MultipartFormDataContent();
        AddToken360FormOptions(form, providerOptions, ["file", "model"]);
        form.Add(new StringContent(NormalizeToken360Model(request.Model), Encoding.UTF8), "model");

        if (!hasFileUrl)
        {
            var audioString = request.Audio switch
            {
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                _ => request.Audio?.ToString()
            };
            if (string.IsNullOrWhiteSpace(audioString))
                throw new ArgumentException("Audio or providerOptions.token360.file_url is required.", nameof(request));
            if (MediaContentHelpers.TryParseDataUrl(audioString, out _, out var base64))
                audioString = base64;

            byte[] audio;
            try
            {
                audio = Convert.FromBase64String(audioString);
            }
            catch (FormatException ex)
            {
                throw new ArgumentException("Audio must be Base64 or a Base64 data URL.", nameof(request), ex);
            }

            var mediaType = string.IsNullOrWhiteSpace(request.MediaType) ? "application/octet-stream" : request.MediaType;
            var extension = ".bin";
            try
            {
                extension = mediaType.GetAudioExtension();
            }
            catch (NotSupportedException)
            {
                // Keep a neutral file name for uncommon codecs supported by Token360.
            }

            var file = new ByteArrayContent(audio);
            file.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
            form.Add(file, "file", "audio" + extension);
        }

        ApplyAuthHeader();
        var now = DateTime.UtcNow;
        using var response = await _client.PostAsync("v1/audio/transcriptions", form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Token360 transcription failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var segments = ParseToken360Segments(root);
        var text = TryGetToken360String(root, "text") ?? string.Join(" ", segments.Select(x => x.Text));
        var model = TryGetToken360String(root, "model") ?? NormalizeToken360Model(request.Model);

        return new TranscriptionResponse
        {
            Text = text,
            Language = TryGetToken360String(root, "language"),
            DurationInSeconds = TryGetToken360Float(root, "duration"),
            Segments = segments,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = model.ToModelId(GetIdentifier()),
                Headers = response.GetHeaders(),
                Body = root
            }
        };
    }

    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return _client.OpenAICompatibleTranscriptionRequestAsync(
            CopyToken360TranscriptionRequest(options),
            "v1/audio/transcriptions",
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

    private static OpenAITranscriptionRequest CopyToken360TranscriptionRequest(OpenAITranscriptionRequest source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new OpenAITranscriptionRequest
        {
            File = source.File,
            Model = NormalizeToken360Model(source.Model),
            Language = source.Language,
            Prompt = source.Prompt,
            ResponseFormat = source.ResponseFormat,
            Temperature = source.Temperature,
            TimestampGranularities = source.TimestampGranularities,
            Stream = false,
            Include = source.Include,
            ChunkingStrategy = source.ChunkingStrategy,
            KnownSpeakerNames = source.KnownSpeakerNames,
            KnownSpeakerReferences = source.KnownSpeakerReferences,
            AdditionalProperties = source.AdditionalProperties is null
                ? null
                : new Dictionary<string, JsonElement>(source.AdditionalProperties, StringComparer.Ordinal)
        };
    }

    private static void AddToken360FormOptions(
        MultipartFormDataContent form,
        JsonElement? options,
        IEnumerable<string> reservedNames)
    {
        if (options is not { ValueKind: JsonValueKind.Object } objectOptions)
            return;

        var reserved = new HashSet<string>(reservedNames, StringComparer.OrdinalIgnoreCase);
        foreach (var property in objectOptions.EnumerateObject())
        {
            if (reserved.Contains(property.Name) || property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                continue;

            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in property.Value.EnumerateArray())
                    form.Add(new StringContent(Token360FormValue(item), Encoding.UTF8), property.Name + "[]");
            }
            else
            {
                form.Add(new StringContent(Token360FormValue(property.Value), Encoding.UTF8), property.Name);
            }
        }
    }

    private static string Token360FormValue(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => value.GetRawText(),
            _ => value.GetRawText()
        };

    private static List<TranscriptionSegment> ParseToken360Segments(JsonElement root)
    {
        List<TranscriptionSegment> result = [];
        if (!root.TryGetProperty("segments", out var segments) || segments.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var segment in segments.EnumerateArray())
        {
            result.Add(new TranscriptionSegment
            {
                Text = TryGetToken360String(segment, "text") ?? string.Empty,
                StartSecond = TryGetToken360Float(segment, "start") ?? 0,
                EndSecond = TryGetToken360Float(segment, "end") ?? 0
            });
        }

        return result;
    }

    private static float? TryGetToken360Float(JsonElement element, string name)
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
