using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.MCP.Media;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.BergetAI;

public partial class BergetAIProvider
{

    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
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
        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);

        if (!string.IsNullOrWhiteSpace(response.Text))
            yield return new OpenAITranscriptionTextDelta { Delta = response.Text };

        yield return new OpenAITranscriptionTextDone { Text = response.Text };
    }

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

        var audioString = request.Audio switch
        {
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
            _ => request.Audio?.ToString()
        };

        if (string.IsNullOrWhiteSpace(audioString))
            throw new ArgumentException("Audio is required.", nameof(request));

        if (MediaContentHelpers.TryParseDataUrl(audioString, out _, out var parsedBase64))
            audioString = parsedBase64;

        var bytes = Convert.FromBase64String(audioString);
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        if (metadata.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null
            and not JsonValueKind.Object)
        {
            throw new JsonException("BergetAI provider metadata must be a JSON object.");
        }

        string? metadataModel = null;
        if (metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in metadata.EnumerateObject())
            {
                if (string.Equals(property.Name, "model", StringComparison.OrdinalIgnoreCase))
                {
                    metadataModel = property.Value.Deserialize<string>(JsonSerializerOptions.Web);
                }
            }
        }

        var effectiveModel = !string.IsNullOrWhiteSpace(metadataModel)
            ? metadataModel
            : request.Model;
        var now = DateTime.UtcNow;

        using var form = new MultipartFormDataContent();

        var fileName = "audio" + request.MediaType.GetAudioExtension();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);

        form.Add(file, "file", fileName);
        form.Add(new StringContent(effectiveModel), "model");

        if (metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in metadata.EnumerateObject())
            {
                if (string.Equals(property.Name, "model", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("file", StringComparison.Ordinal)
                    || property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    continue;
                }

                form.Add(new StringContent(GetFormValue(property.Value)), property.Name);
            }
        }

        using var resp = await _client.PostAsync("v1/audio/transcriptions", form, cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Berget STT failed ({(int)resp.StatusCode}): {json}");

        return ConvertBergetTranscriptionResponse(json, effectiveModel, now, GetIdentifier());
    }

    private static string GetFormValue(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => value.GetRawText()
        };

    private static TranscriptionResponse ConvertBergetTranscriptionResponse(string json, string model, DateTime timestamp, string providerKey)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var text = root.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String
            ? textEl.GetString() ?? string.Empty
            : string.Empty;

        var providerMetadata = providerKey.CreatePrimitiveProviderMetadata();

        return new TranscriptionResponse
        {
            Text = text,
            Segments = [],
            ProviderMetadata = providerMetadata,
            Response = new ResponseData
            {
                Timestamp = timestamp,
                ModelId = model.ToModelId(providerKey),
                Body = root.Clone()
            }
        };
    }
}
