using System.Globalization;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Reve;
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
        var metadata = request.GetProviderMetadata<ReveImageProviderMetadata>(GetIdentifier());

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
        ReveImageProviderMetadata? metadata)
    {
        var references = request.Files?
            .Select(file =>
            {
                var data = file.Data;
                return new
                {
                    data = data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                        ? data.RemoveDataUrlPrefix()
                        : data
                };
            })
            .ToArray();

        return new
        {
            prompt = request.Prompt,
            references,
            aspect_ratio = request.AspectRatio,
            version = ResolveV2Version(request.Model),
            postprocessing = metadata?.Postprocessing
        };
    }

    private static string ResolveV2Version(string model)
        => model.Equals("latest", StringComparison.OrdinalIgnoreCase)
            || model.Equals("reve-v2-create@20260601", StringComparison.OrdinalIgnoreCase)
            ? "reve-v2-create@260601"
            : model;
}

