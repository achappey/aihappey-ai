using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Xml;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Bineric;

public partial class BinericProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.MediaType))
            throw new ArgumentException("MediaType is required.", nameof(request));

        var audio = request.Audio switch
        {
            JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString(),
            _ => request.Audio?.ToString()
        };

        if (string.IsNullOrWhiteSpace(audio))
            throw new ArgumentException("Audio is required.", nameof(request));

        var audioAttachment = audio.Trim().StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? audio.Trim()
            : $"data:{request.MediaType.Trim()};base64,{audio.Trim()}";

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = "speech-to-text",
            ["messages"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["content"] = "Transcribe this audio",
                    ["attachments"] = new[] { audioAttachment }
                }
            }
        };
        var warnings = new List<object>();

        AddRawProviderOptions(payload, request.GetProviderMetadata<JsonElement>(GetIdentifier()), warnings);

        var payloadJson = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);
        var now = DateTime.UtcNow;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "api/v1/ai/chat/completions")
        {
            Content = new StringContent(payloadJson, Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Bineric transcription failed ({(int)response.StatusCode}): {responseJson}");

        using var document = JsonDocument.Parse(responseJson);
        var root = document.RootElement;
        var segments = ParseSegments(root);
        var text = GetAssistantContent(root) ?? string.Join(" ", segments.Select(segment => segment.Text));

        return new TranscriptionResponse
        {
            Text = text,
            Segments = segments,
            Warnings = warnings,
            Request = new TranscriptionRequestItem { Body = payloadJson },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new ResponseData
            {
                Timestamp = now,
                Headers = response.GetHeaders(),
                ModelId = GetResponseModel(root, request.Model).ToModelId(GetIdentifier()),
                Body = root.Clone()
            }
        };
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
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);

        if (!string.IsNullOrWhiteSpace(response.Text))
            yield return new OpenAITranscriptionTextDelta { Delta = response.Text };

        yield return new OpenAITranscriptionTextDone { Text = response.Text };
    }

    private static void AddRawProviderOptions(
        Dictionary<string, object?> payload,
        JsonElement options,
        List<object> warnings)
    {
        if (options.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in options.EnumerateObject())
        {
            if (property.NameEquals("model") || property.NameEquals("messages"))
            {
                warnings.Add(new { type = "ignored", feature = $"providerOptions.{property.Name}" });
                continue;
            }

            payload[property.Name] = property.Value.Clone();
        }
    }

    private static string? GetAssistantContent(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            return null;
        }

        var choice = choices[0];
        return choice.TryGetProperty("message", out var message)
               && message.ValueKind == JsonValueKind.Object
               && message.TryGetProperty("content", out var content)
               && content.ValueKind == JsonValueKind.String
            ? content.GetString()
            : null;
    }

    private static List<TranscriptionSegment> ParseSegments(JsonElement root)
    {
        var segments = new List<TranscriptionSegment>();

        if (!root.TryGetProperty("_bineric", out var bineric)
            || bineric.ValueKind != JsonValueKind.Object
            || !bineric.TryGetProperty("phrases", out var phrases)
            || phrases.ValueKind != JsonValueKind.Array)
        {
            return segments;
        }

        foreach (var phrase in phrases.EnumerateArray())
        {
            if (phrase.ValueKind != JsonValueKind.Object
                || !phrase.TryGetProperty("text", out var text)
                || text.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(text.GetString()))
            {
                continue;
            }

            var start = ReadIsoDurationSeconds(phrase, "startTime");
            var duration = ReadIsoDurationSeconds(phrase, "duration");
            segments.Add(new TranscriptionSegment
            {
                Text = text.GetString()!,
                StartSecond = start,
                EndSecond = start + duration
            });
        }

        return segments;
    }

    private static float ReadIsoDurationSeconds(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            return 0f;
        }

        try
        {
            return (float)XmlConvert.ToTimeSpan(value.GetString()!).TotalSeconds;
        }
        catch (FormatException)
        {
            return 0f;
        }
    }

    private static string GetResponseModel(JsonElement root, string fallback)
        => root.TryGetProperty("model", out var model)
           && model.ValueKind == JsonValueKind.String
           && !string.IsNullOrWhiteSpace(model.GetString())
            ? model.GetString()!
            : fallback;
}

