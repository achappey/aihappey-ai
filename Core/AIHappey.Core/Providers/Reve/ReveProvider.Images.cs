using System.Globalization;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Reve;

public partial class ReveProvider
{
    private static readonly JsonSerializerOptions ReveJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());

        if (request.N is > 1)
            warnings.Add(new { type = "unsupported", feature = "n", details = "Reve returns exactly 1 image." });

        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        if (!string.IsNullOrWhiteSpace(request.Size))
            warnings.Add(new { type = "unsupported", feature = "size" });

        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });

        var payload = BuildReveV2CreatePayload(request, metadata);

        var json = JsonSerializer.Serialize(payload, ReveJson);
        using var req = new HttpRequestMessage(HttpMethod.Post, "v2/image/create")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        req.Headers.Accept.ParseAdd(MediaTypeNames.Application.Json);

        using var resp = await _client.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"{resp.StatusCode}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        if (!root.TryGetProperty("image", out var imageEl))
            throw new Exception("Reve response did not include an image.");

        var image = imageEl.GetString();
        if (string.IsNullOrWhiteSpace(image))
            throw new Exception("Reve response contained empty image data.");

        var modelItem = await this.GetModel(request.Model, cancellationToken);

        var providerRoot = root.EnumerateObject()
            .Where(p => !p.NameEquals("image"))
            .ToDictionary(
                p => p.Name,
                p => p.Value.Clone());

        return new ImageResponse
        {
            Images = [image.ToDataUrl(MediaTypeNames.Image.Png)],
            Warnings = warnings,
            ProviderMetadata = GetIdentifier()
                .CreatePrimitiveProviderMetadata(providerRoot,
                costs: CalculateReveCosts(root, modelItem.Pricing?.Output)),
            Response = new()
            {
                Timestamp = now,
                Headers = resp.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private static decimal? CalculateReveCosts(JsonElement root, decimal? outputPrice)
    {
        if (!root.TryGetProperty("credits_used", out var creditsEl))
            return 0m;

        decimal creditsUsed = creditsEl.ValueKind switch
        {
            JsonValueKind.Number when creditsEl.TryGetDecimal(out var d) => d,
            JsonValueKind.String when decimal.TryParse(
                creditsEl.GetString(),
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out var d) => d,
            _ => 0m
        };

        if (creditsUsed <= 0)
            return 0m;

        return outputPrice.HasValue ?
            creditsUsed * outputPrice : null;
    }

    private static object BuildReveV2CreatePayload(
        ImageRequest request,
        JsonElement metadata)
    {
        var references = request.Files?
            .Select(file =>
            {
                var data = file.GetDataOrUrl();
                return new
                {
                    data = data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                        ? data.RemoveDataUrlPrefix()
                        : data
                };
            })
            .ToArray();

        var payload = GetAdditionalMetadata(metadata, "postprocessing");
        payload["prompt"] = request.Prompt;
        payload["references"] = references;
        payload["aspect_ratio"] = request.AspectRatio;
        payload["version"] = ResolveV2Version(request.Model);

        payload["postprocessing"] = GetRevePostprocessingMetadata(metadata);

        return payload;
    }

    private static Dictionary<string, object?> GetAdditionalMetadata(
        JsonElement metadata,
        string mappedProperty)
    {
        if (metadata.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return [];

        if (metadata.ValueKind != JsonValueKind.Object)
            throw new JsonException("Provider metadata must be a JSON object.");

        var payload = new Dictionary<string, object?>();
        foreach (var property in metadata.EnumerateObject())
        {
            if (string.Equals(property.Name, mappedProperty, StringComparison.OrdinalIgnoreCase))
                continue;

            payload[property.Name] = property.Value.Clone();
        }

        return payload;
    }

    private static JsonArray? GetRevePostprocessingMetadata(JsonElement metadata)
    {
        if (metadata.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return default;

        JsonElement postprocessing = default;
        var found = false;
        foreach (var property in metadata.EnumerateObject())
        {
            if (string.Equals(property.Name, "postprocessing", StringComparison.OrdinalIgnoreCase))
            {
                postprocessing = property.Value;
                found = true;
            }
        }

        if (!found)
            return null;

        var mappedItems = postprocessing.Deserialize<IReadOnlyList<RevePostprocessing>>(
            JsonSerializerOptions.Web);
        if (mappedItems is null)
            return null;

        var sourceItems = postprocessing.EnumerateArray().ToArray();
        var result = new JsonArray();
        for (var index = 0; index < sourceItems.Length; index++)
        {
            var item = JsonNode.Parse(sourceItems[index].GetRawText())!.AsObject();
            foreach (var property in item.Select(property => property.Key).ToArray())
            {
                if (IsRevePostprocessingProperty(property))
                    item.Remove(property);
            }

            var mappedItem = JsonSerializer.SerializeToNode(mappedItems[index], ReveJson)!
                .AsObject();
            foreach (var property in mappedItem)
                item[property.Key] = property.Value?.DeepClone();

            result.Add(item);
        }

        return result;
    }

    private static bool IsRevePostprocessingProperty(string property)
        => property.Equals("process", StringComparison.OrdinalIgnoreCase)
            || property.Equals("upscale_factor", StringComparison.OrdinalIgnoreCase)
            || property.Equals("max_dim", StringComparison.OrdinalIgnoreCase)
            || property.Equals("max_width", StringComparison.OrdinalIgnoreCase)
            || property.Equals("max_height", StringComparison.OrdinalIgnoreCase);

    private static string ResolveV2Version(string model)
        => model.Equals("latest", StringComparison.OrdinalIgnoreCase)
            || model.Equals("reve-v2-create@20260601", StringComparison.OrdinalIgnoreCase)
            ? "reve-v2-create@260601"
            : model;

    private sealed class RevePostprocessing
    {
        public RevePostprocessing()
        {
        }

        [JsonPropertyName("process")]
        public string? Process { get; set; }

        [JsonPropertyName("upscale_factor")]
        public int? UpscaleFactor { get; set; }

        [JsonPropertyName("max_dim")]
        public int? MaxDim { get; set; }

        [JsonPropertyName("max_width")]
        public int? MaxWidth { get; set; }

        [JsonPropertyName("max_height")]
        public int? MaxHeight { get; set; }
    }
}
