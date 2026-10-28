using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Sference;

public sealed partial class SferenceProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Get, "v1/models");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await ReadResponseAsync(response, cancellationToken);
        using var document = JsonDocument.Parse(raw);
        var data = document.RootElement.GetProperty("data");
        if (data.ValueKind != JsonValueKind.Array)
            throw new JsonException("Sference model discovery requires a data array.");

        return data.EnumerateArray().Select(item =>
        {
            var id = item.GetProperty("id").GetString();
            if (string.IsNullOrWhiteSpace(id))
                throw new JsonException("Sference returned a model without an ID.");
            var decision = id.Equals("Cloudflare/clef", StringComparison.OrdinalIgnoreCase);
            return new Model
            {
                Id = id.ToModelId(GetIdentifier()),
                Name = ReadString(item, "name") ?? id,
                OwnedBy = ReadString(item, "owned_by") ?? id.Split('/')[0],
                Description = ReadString(item, "description"),
                Created = item.TryGetProperty("created", out var created) && created.ValueKind == JsonValueKind.Number
                    && created.TryGetInt64(out var timestamp) ? timestamp : null,
                ContextWindow = ReadInt(item, "context_window") ?? (decision ? 65536 : null),
                MaxTokens = ReadInt(item, "max_tokens"),
                Type = decision ? "decision" : "language",
                Pricing = decision ? new ModelPricing { Input = 0.24m, Output = 0 } : null
            };
        }).ToArray();
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? ReadInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number : null;

    private static async Task<string> ReadResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Sference API error ({(int)response.StatusCode})"
                + (response.Headers.TryGetValues("Retry-After", out var retry) ? $" [Retry-After: {string.Join(", ", retry)}]" : "")
                + $": {raw}", null, response.StatusCode);
        return raw;
    }
}
