using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.SenseNova;

public sealed partial class SenseNovaProvider
{
    private async Task<IEnumerable<Model>> ListSenseNovaModelsAsync(CancellationToken cancellationToken)
    {
        var key = keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return [];

        return await asyncCacheHelper.GetOrCreateAsync(
            this.GetCacheKey(key),
            async ct =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "v1/models");
                request.Headers.Authorization = new("Bearer", key);
                using var response = await _client.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                if (!document.RootElement.TryGetProperty("data", out var data) ||
                    data.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException("SenseNova model response did not contain a data list.");

                return (IEnumerable<Model>)data.EnumerateArray()
                    .Select(ToSenseNovaModel)
                    .OfType<Model>()
                    .DistinctBy(model => model.Id)
                    .ToArray();
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private Model? ToSenseNovaModel(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;

        var id = GetString(item, "id");
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var type = HasModality(item, "output_modalities", "image")
            ? "image"
            : HasModality(item, "input_modalities", "text") && HasModality(item, "output_modalities", "text")
                ? "language"
                : null;
        if (type is null)
            return null;

        var model = item.Deserialize<Model>(JsonSerializerOptions.Web)!;
        model.Id = id.ToModelId(GetIdentifier());
        model.Name = string.IsNullOrWhiteSpace(model.Name) ? id : model.Name;
        model.OwnedBy = string.IsNullOrWhiteSpace(model.OwnedBy) ? GetIdentifier() : model.OwnedBy;
        model.Type = type;
        return model;
    }

    private static bool HasModality(JsonElement item, string property, string modality)
        => item.TryGetProperty(property, out var values) &&
           values.ValueKind == JsonValueKind.Array &&
           values.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == modality);

    private static string? GetString(JsonElement item, string property)
        => item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
