using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Novita;

public partial class NovitaProvider : IModelProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"API error: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var models = new List<Model>();
        var root = doc.RootElement;

        // ✅ root is already an array
        var arr = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray()
            : root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array
                ? dataEl.EnumerateArray()
                : Enumerable.Empty<JsonElement>();

        foreach (var el in arr)
        {
            Model model = new();

            if (el.TryGetProperty("id", out var idEl))
                model.Id = idEl.GetString()?.ToModelId(GetIdentifier()) ?? "";

            if (el.TryGetProperty("context_size", out var contextLengthEl))
                model.ContextWindow = contextLengthEl.GetInt32();

            model.Type = "language";

            if (el.TryGetProperty("display_name", out var orgEl))
                model.Name = orgEl.GetString() ?? "";

            if (el.TryGetProperty("max_output_tokens", out var maxTokensEl))
                model.MaxTokens = maxTokensEl.GetInt32();

            if (el.TryGetProperty("description", out var descEl))
                model.Description = descEl.GetString() ?? "";

            if (el.TryGetProperty("created", out var createdEl) && createdEl.ValueKind == JsonValueKind.Number)
            {
                var unix = createdEl.GetInt64();
                model.Created = unix;
            }

            if (el.TryGetProperty("input_token_price_per_m", out var inputPrice)
             && inputPrice.ValueKind == JsonValueKind.Number
             && el.TryGetProperty("output_token_price_per_m", out var outputPrice)
             && outputPrice.ValueKind == JsonValueKind.Number)
            {
              /*  model.Pricing = new ModelPricing
                {
                    Input = inputPrice.GetInt32().ToString(),
                    Output = outputPrice.GetInt32().ToString(),
                };*/
            }


            if (!string.IsNullOrEmpty(model.Id))
                models.Add(model);
        }

        models.AddRange(StaticModels(GetIdentifier()));
        return models;

    }

    private static IReadOnlyList<Model> StaticModels(string providerId) =>
[
    new()
    {
        Id = "glm-asr".ToModelId(providerId),
        Name = "glm-asr-2512",
        Type = "transcription"
    },
    new()
    {
        Id = "glm-tts".ToModelId(providerId),
        Name = "glm-tts",
        Type = "speech"
    },
    new()
    {
        Id = "async/txt2speech".ToModelId(providerId),
        Name = "txt2speech",
        Type = "speech"
    },
    new()
    {
        Id = "minimax-speech-2.5-turbo-preview".ToModelId(providerId),
        Name = "minimax-speech-2.5-turbo-preview",
        Type = "speech"
    },
    new()
    {
        Id = "minimax-speech-2.6-turbo".ToModelId(providerId),
        Name = "minimax-speech-2.6-turbo",
        Type = "speech"
    },
    new()
    {
        Id = "minimax-speech-2.6-hd".ToModelId(providerId),
        Name = "minimax-speech-2.6-hd",
        Type = "speech"
    },
    new()
    {
        Id = "minimax-speech-2.5-hd-preview".ToModelId(providerId),
        Name = "minimax-speech-2.5-hd-preview",
        Type = "speech"
    },
    new()
    {
        Id = "minimax-speech-02-turbo".ToModelId(providerId),
        Name = "minimax-speech-02-turbo",
        Type = "speech"
    },
    new()
    {
        Id = "minimax-speech-02-hd".ToModelId(providerId),
        Name = "minimax-speech-02-hd",
        Type = "speech"
    },
];
}