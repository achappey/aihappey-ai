using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.Runware;

namespace AIHappey.Core.Providers.Runware;

public sealed partial class RunwareProvider
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(imageRequest);
        if (string.IsNullOrWhiteSpace(imageRequest.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(imageRequest));
        if (string.IsNullOrWhiteSpace(imageRequest.Model))
            throw new ArgumentException("Model is required.", nameof(imageRequest));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var options = imageRequest.GetImageProviderMetadata<RunwareImageProviderMetadata>(GetIdentifier());

        var payloadTask = BuildImageInferencePayload(imageRequest, options);

        var json = JsonSerializer.Serialize(new[] { payloadTask }, JsonOpts);
        using var resp = await _client.PostAsync("", new StringContent(json, Encoding.UTF8, "application/json"), cancellationToken);

        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"{resp.StatusCode}: {raw}");

        // Needed only to wrap base64/url responses into a data URL.
        var outputFormatForParsing = options?.OutputFormat ?? "PNG";
        var images = await ExtractImagesAsync(raw, outputFormatForParsing, cancellationToken);
        if (images.Count == 0)
            throw new Exception("Runware returned no images.");

        Dictionary<string, JsonElement>? providerMetadata = null;
        if (imageRequest.Model.StartsWith("bria:", StringComparison.OrdinalIgnoreCase))
            providerMetadata = ExtractBriaProviderMetadata(raw);

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            ProviderMetadata = providerMetadata,
            Response = new()
            {
                Timestamp = now,
                ModelId = imageRequest.Model,
                Body = JsonDocument.Parse(raw).RootElement.Clone()
            }
        };
    }

    private static Dictionary<string, JsonElement>? ExtractBriaProviderMetadata(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array)
            return null;

        // Best-effort: extract Bria/FIBO structured metadata without copying large image payloads.
        var items = new List<Dictionary<string, JsonElement>>();

        foreach (var item in dataEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var meta = new Dictionary<string, JsonElement>();

            if (item.TryGetProperty("bria", out var bria))
                meta["bria"] = bria.Clone();

            if (item.TryGetProperty("schema", out var schema))
                meta["schema"] = schema.Clone();

            if (item.TryGetProperty("jsonSchema", out var jsonSchema))
                meta["jsonSchema"] = jsonSchema.Clone();

            if (item.TryGetProperty("trace", out var trace))
                meta["trace"] = trace.Clone();

            if (item.TryGetProperty("fibo", out var fibo))
                meta["fibo"] = fibo.Clone();

            if (meta.Count > 0)
                items.Add(meta);
        }

        if (items.Count == 0)
            return null;

        return new Dictionary<string, JsonElement>
        {
            ["bria"] = JsonSerializer.SerializeToElement(items, JsonSerializerOptions.Web)
        };
    }
}


