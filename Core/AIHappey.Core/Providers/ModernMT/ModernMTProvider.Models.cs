using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.ModernMT;

public sealed partial class ModernMTProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        // Avoid throwing during model discovery when the key is not configured.
        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return [];

        ApplyAuthHeader();

        return await ListTranslationModelsAsync(cancellationToken);
    }

    private async Task<IEnumerable<Model>> ListTranslationModelsAsync(CancellationToken cancellationToken)
    {
        // ModernMT docs: GET /translate/languages
        // Recommended workaround for method limitations: POST + X-HTTP-Method-Override: GET
        using var req = new HttpRequestMessage(HttpMethod.Post, "translate/languages")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        req.Headers.Add("X-HTTP-Method-Override", "GET");

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"ModernMT languages failed ({(int)resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // expected: { "status": 200, "data": ["en", ...] }
        if (root.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.Number)
        {
            var status = statusEl.GetInt32();
            if (status < 200 || status >= 300)
                throw new InvalidOperationException($"ModernMT languages returned status {status}: {body}");
        }

        if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array)
            return [];

        var models = new List<Model>();
        foreach (var item in dataEl.EnumerateArray())
        {
            var code = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (string.IsNullOrWhiteSpace(code))
                continue;

            var languageCode = code!.Trim();

            models.Add(new Model
            {
                OwnedBy = nameof(ModernMT),
                Type = "language",
                Id = $"translate/{languageCode}".ToModelId(GetIdentifier()),
                Name = $"Translate to {languageCode}",
                Description = languageCode,
            });
        }

        return models;
    }
}

