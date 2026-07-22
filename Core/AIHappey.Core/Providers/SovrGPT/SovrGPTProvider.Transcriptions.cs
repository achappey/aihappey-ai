using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using System.Text.Json;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;

namespace AIHappey.Core.Providers.SovrGPT;

public partial class SovrGPTProvider
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

        var audioBase64 = GetAudioBase64(request.Audio);
        if (string.IsNullOrWhiteSpace(audioBase64))
            throw new ArgumentException("Audio is required.", nameof(request));

        ApplyAuthHeader();

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var model = NormalizeModel(request.Model);
        var providerOptions = request.GetProviderMetadata<JsonElement>(GetIdentifier());

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Convert.FromBase64String(audioBase64));
        file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);
        form.Add(file, "file", "audio" + request.MediaType.GetAudioExtension());
        form.Add(new StringContent(model, Encoding.UTF8), "model");
        AddProviderOptions(form, providerOptions, warnings);

        using var response = await _client.PostAsync("v1/audio/transcriptions", form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"SovrGPT transcription request failed ({(int)response.StatusCode}): {raw}");

        var responseFormat = GetProviderOptionString(providerOptions, "response_format");
        var responseContentType = response.Content.Headers.ContentType?.MediaType;
        var isTextResponse = string.Equals(responseFormat, "text", StringComparison.OrdinalIgnoreCase)
            || responseContentType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true;

        JsonElement? responseBody = null;
        string text = raw;
        string? language = null;
        float? duration = null;
        IEnumerable<TranscriptionSegment> segments = [];

        if (!isTextResponse)
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            responseBody = root.Clone();
            text = root.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String
                ? textElement.GetString() ?? string.Empty
                : string.Empty;
            language = root.TryGetProperty("language", out var languageElement) && languageElement.ValueKind == JsonValueKind.String
                ? languageElement.GetString()
                : null;
            duration = root.TryGetProperty("duration", out var durationElement) && durationElement.ValueKind == JsonValueKind.Number
                ? (float)durationElement.GetDouble()
                : null;
            segments = ParseSegments(root);
        }

        return new TranscriptionResponse
        {
            Text = text,
            Language = language,
            DurationInSeconds = duration,
            Segments = segments,
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new ResponseData
            {
                Timestamp = now,
                Headers = response.GetHeaders(),
                ModelId = model.ToModelId(GetIdentifier()),
                Body = responseBody
            }
        };
    }

    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return _client.OpenAICompatibleTranscriptionRequestAsync(
            options,
            cancellationToken: cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // SovrGPT documents a completed STT response only. Convert it to the standard stream shape.
        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);

        if (!string.IsNullOrWhiteSpace(response.Text))
            yield return new OpenAITranscriptionTextDelta { Delta = response.Text };

        yield return new OpenAITranscriptionTextDone { Text = response.Text };
    }

    private static string GetAudioBase64(object audio)
    {
        var value = audio switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => audio?.ToString()
        };

        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var commaIndex = value.IndexOf(',');
        return value.TrimStart().StartsWith("data:", StringComparison.OrdinalIgnoreCase) && commaIndex >= 0
            ? value[(commaIndex + 1)..]
            : value;
    }

    private static string NormalizeModel(string model)
    {
        const string providerPrefix = "sovrgpt/";
        var value = model.Trim();
        return value.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase)
            ? value[providerPrefix.Length..]
            : value;
    }

    private static void AddProviderOptions(
        MultipartFormDataContent form,
        JsonElement options,
        List<object> warnings)
    {
        if (options.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in options.EnumerateObject())
        {
            if (property.NameEquals("file") || property.NameEquals("model"))
            {
                warnings.Add(new { type = "ignored", feature = $"providerOptions.{property.Name}" });
                continue;
            }

            AddMultipartJsonValue(form, property.Name, property.Value);
        }
    }

    private static void AddMultipartJsonValue(MultipartFormDataContent form, string name, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                    AddMultipartJsonValue(form, $"{name}[{property.Name}]", property.Value);
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                    AddMultipartJsonValue(form, $"{name}[]", item);
                break;
            case JsonValueKind.String:
                form.Add(new StringContent(value.GetString() ?? string.Empty, Encoding.UTF8), name);
                break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                form.Add(new StringContent(value.GetRawText(), Encoding.UTF8), name);
                break;
        }
    }

    private static string? GetProviderOptionString(JsonElement options, string name)
        => options.ValueKind == JsonValueKind.Object
           && options.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IEnumerable<TranscriptionSegment> ParseSegments(JsonElement root)
    {
        if (!root.TryGetProperty("segments", out var segmentElements)
            || segmentElements.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return segmentElements.EnumerateArray()
            .Where(segment => segment.ValueKind == JsonValueKind.Object)
            .Select(segment => new TranscriptionSegment
            {
                Text = segment.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
                    ? text.GetString() ?? string.Empty
                    : string.Empty,
                StartSecond = ReadSegmentSecond(segment, "start"),
                EndSecond = ReadSegmentSecond(segment, "end")
            })
            .ToArray();
    }

    private static float ReadSegmentSecond(JsonElement segment, string propertyName)
        => segment.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
            ? (float)value.GetDouble()
            : 0f;
}
