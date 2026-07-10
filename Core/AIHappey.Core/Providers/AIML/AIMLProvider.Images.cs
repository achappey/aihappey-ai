using AIHappey.Core.AI;
using System.Text.Json;
using System.Net.Mime;
using System.Text;
using System.Text.Json.Nodes;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.AIML;

public partial class AIMLProvider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var now = DateTime.UtcNow;
        var payload = imageRequest.GetImageRequestPayload();

        var json = JsonSerializer.Serialize(payload, JsonOpts);
        using var resp = await _client.PostAsync("v1/images/generations",
            new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json), cancellationToken);
        var text = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"{resp.StatusCode}: {text}");

        var root = JsonNode.Parse(text) ?? throw new Exception($"Something went wrong");
        var data = root["data"]!.AsArray();
        var images = new List<string>();

        foreach (var item in data)
        {
            byte[] bytes;

            if (item?["b64_json"] is JsonNode b64)
            {
                images.Add(b64.GetValue<string>().ToDataUrl(MediaTypeNames.Image.Png));
                bytes = Convert.FromBase64String(b64.GetValue<string>());
            }
            else if (item?["url"] is JsonNode urlNode)
            {
                var url = urlNode.GetValue<string>();
                bytes = await _client.GetByteArrayAsync(url, cancellationToken);
                images.Add(Convert.ToBase64String(bytes).ToDataUrl(MediaTypeNames.Image.Png));
            }
            else
            {
                continue;
            }
        }

        var providerMetadata = new Dictionary<string, JsonElement>();
        decimal? cost = null;

        if (root["meta"] is JsonObject meta)
        {
            JsonNode? usage = null;
            JsonNode? metrics = null;

            if (meta["usage"] is JsonObject usageObject)
            {
                usage = usageObject.DeepClone();

                if (usageObject["usd_spent"] is JsonValue costValue
                    && costValue.TryGetValue<decimal>(out var parsedCost))
                {
                    cost = parsedCost;
                }
            }

            if (meta["metrics"] is JsonObject metricsObject)
                metrics = metricsObject.DeepClone();

            providerMetadata[GetIdentifier()] =
                JsonSerializer.SerializeToElement(new
                {
                    usage,
                    metrics
                }, JsonSerializerOptions.Web);
        }

        if (cost is not null)
        {
            providerMetadata["gateway"] =
                JsonSerializer.SerializeToElement(new
                {
                    cost
                }, JsonSerializerOptions.Web);
        }

        return new()
        {
            Images = images,
            ProviderMetadata = providerMetadata,
            Response = new()
            {
                Timestamp = now,
                ModelId = imageRequest.Model.ToModelId(GetIdentifier())
            }
        };
    }

}
