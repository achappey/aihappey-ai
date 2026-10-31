using AIHappey.Core.AI;
using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Common.Extensions;
using System.Globalization;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.Scaleway;

public partial class ScalewayProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(
          TranscriptionRequest request,
          CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var bytes = Convert.FromBase64String(request.Audio.ToString()!);
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        if (metadata.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            EnsureMetadataObject(metadata);

        using var form = new MultipartFormDataContent();

        // file (required)
        var fileName = "audio" + request.MediaType.GetAudioExtension();
        var file = new ByteArrayContent(bytes);

        if (!string.IsNullOrWhiteSpace(request.MediaType))
            file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);

        form.Add(file, "file", fileName);

        // required
        form.Add(new StringContent(request.Model), "model");

        var language = ReadMetadataValue<string>(metadata, "language");
        if (!string.IsNullOrWhiteSpace(language))
            form.Add(new StringContent(language), "language");

        var prompt = ReadMetadataValue<string>(metadata, "prompt");
        if (!string.IsNullOrWhiteSpace(prompt))
            form.Add(new StringContent(prompt), "prompt");

        // temperature (optional)
        var temperature = ReadMetadataValue<float?>(metadata, "temperature");
        if (temperature is not null)
        {
            form.Add(
                new StringContent(
                    temperature.Value.ToString(CultureInfo.InvariantCulture)
                ),
                "temperature"
            );
        }

        AddAdditionalMetadataFields(form, metadata);

        var url = "v1/audio/transcriptions";

        using var resp = await _client.PostAsync(url, form, cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Scaleway STT failed ({(int)resp.StatusCode}): {json}");

        return ConvertScalewayResponse(json, request.Model, GetIdentifier(), resp.GetHeaders());
    }

    private static void AddAdditionalMetadataFields(MultipartFormDataContent form, JsonElement metadata)
    {
        if (metadata.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return;

        EnsureMetadataObject(metadata);

        foreach (var property in metadata.EnumerateObject())
        {
            if (IsMappedMetadataProperty(property.Name))
                continue;

            var value = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.GetRawText();
            form.Add(new StringContent(value), property.Name);
        }
    }

    private static T? ReadMetadataValue<T>(JsonElement metadata, string name)
    {
        if (metadata.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return default;

        EnsureMetadataObject(metadata);

        var found = false;
        var value = default(JsonElement);
        foreach (var property in metadata.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;

            found = true;
            value = property.Value;
        }

        return !found || value.ValueKind == JsonValueKind.Null
            ? default
            : value.Deserialize<T>(JsonSerializerOptions.Web);
    }

    private static bool IsMappedMetadataProperty(string name)
        => name.Equals("file", StringComparison.OrdinalIgnoreCase)
            || name.Equals("model", StringComparison.OrdinalIgnoreCase)
            || name.Equals("language", StringComparison.OrdinalIgnoreCase)
            || name.Equals("prompt", StringComparison.OrdinalIgnoreCase)
            || name.Equals("temperature", StringComparison.OrdinalIgnoreCase);

    private static void EnsureMetadataObject(JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            throw new JsonException("Scaleway provider metadata must be a JSON object.");
    }

    private static TranscriptionResponse ConvertScalewayResponse(
        string json,
        string model,
        string providerId,
        Dictionary<string, string> headers)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        return new TranscriptionResponse
        {
            Text = root.TryGetProperty("text", out var t)
                ? t.GetString() ?? ""
                : "",
            Segments = [],
            ProviderMetadata = providerId.CreatePrimitiveProviderMetadata(),
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                Headers = headers,
                ModelId = model.ToModelId(providerId),
                Body = root.Clone(),
            }
        };
    }
}
