using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.PaxaLabs;

public partial class PaxaLabsProvider
{
    private const string TtsModelId = "paxa-tts-flash-v1";
    private const string TranslationModelId = "paxa-translation-lite-v1";
    private const string OcrModelId = "paxa-ocr-lite-v1";

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var cacheKey = this.GetCacheKey();

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {

                using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
                using var resp = await _client.SendAsync(req, cancellationToken);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(cancellationToken);
                    throw new Exception($"PaxaLabs API error: {err}");
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                var models = new List<Model>();
                var root = doc.RootElement;


                var arr = root.TryGetProperty("models", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array
                        ? dataEl.EnumerateArray()
                        : Enumerable.Empty<JsonElement>();

                foreach (var el in arr)
                {
                    Model model = new() { OwnedBy = "paxalabs" };

                    if (el.TryGetProperty("id", out var idEl))
                    {
                        model.Id = idEl.GetString()?.ToModelId(GetIdentifier()) ?? "";
                        model.Name = idEl.GetString() ?? "";
                    }

                    if (el.TryGetProperty("name", out var nameEl))
                        model.Name = nameEl.GetString() ?? model.Name;

                    var product = el.TryGetProperty("product", out var productEl)
                        ? productEl.GetString()
                        : null;
                    model.Type = product switch
                    {
                        "tts" => "speech",
                        "translation" => "translation",
                        "ocr" => "ocr",
                        _ => product ?? "model"
                    };
                    model.Tags = string.IsNullOrWhiteSpace(product) ? null : [product];
                    model.Description = product switch
                    {
                        "tts" => "Paxa Labs Thai and English text-to-speech model.",
                        "translation" => "Paxa Labs translation model targeting Thai.",
                        "ocr" => "Paxa Labs PDF and image OCR model.",
                        _ => null
                    };
                 
                    if (!string.IsNullOrEmpty(model.Id))
                        models.Add(model);

                    if (string.Equals(product, "tts", StringComparison.OrdinalIgnoreCase)
                        && el.TryGetProperty("voices", out var voicesEl)
                        && voicesEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var voiceEl in voicesEl.EnumerateArray())
                        {
                            var voice = voiceEl.GetString();
                            var baseId = el.TryGetProperty("id", out var baseIdEl) ? baseIdEl.GetString() : null;
                            if (string.IsNullOrWhiteSpace(voice) || string.IsNullOrWhiteSpace(baseId))
                                continue;

                            models.Add(new Model
                            {
                                Id = $"{baseId}/{voice}".ToModelId(GetIdentifier()),
                                Name = $"{model.Name} / {voice}",
                                OwnedBy = "paxalabs",
                                Type = "speech",
                                Tags = ["tts", "voice"],
                                Description = $"{model.Name} with the Paxa Labs '{voice}' voice."
                            });
                        }
                    }
                }

                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }
}
