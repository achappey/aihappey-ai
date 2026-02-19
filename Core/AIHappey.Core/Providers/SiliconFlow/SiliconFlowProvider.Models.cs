using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.SiliconFlow;

public partial class SiliconFlowProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return await Task.FromResult<IEnumerable<Model>>([]);


        ApplyAuthHeader();

        using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"SiliconFlow API error: {err}");
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
            {
                model.Id = idEl.GetString()?.ToModelId(GetIdentifier()) ?? "";
                model.Name = idEl.GetString() ?? "";
            }

            if (model.Id.Contains("/black-forest-lab/")
            || model.Id.Contains("Qwen-Image")
            || model.Id.Contains("Z-Image-Turbo"))
                model.Type = "image";

            if (model.Id.Contains("/fish-speech-1.5")
                || model.Id.Contains("/CosyVoice2-0.5B")
                || model.Id.Contains("/IndexTTS-2"))
                model.Type = "speech";

            if (model.Id.Contains("I2V")
                || model.Id.Contains("T2V"))
                model.Type = "video";

            if (el.TryGetProperty("context_length", out var contextLengthEl))
                model.ContextWindow = contextLengthEl.GetInt32();

            if (el.TryGetProperty("type", out var typeEl) && !string.IsNullOrEmpty(typeEl.GetString()))
                model.Type = typeEl.GetString()!;

            if (model.Id.Contains("rerank"))
                model.Type = "reranking";

            if (el.TryGetProperty("organization", out var orgEl))
                model.OwnedBy = orgEl.GetString() ?? "";

            if (el.TryGetProperty("created", out var createdEl) && createdEl.ValueKind == JsonValueKind.Number)
            {
                var unix = createdEl.GetInt64();
                model.Created = unix;
            }

            if (!string.IsNullOrEmpty(model.Id))
                models.Add(model);
        }

        models.Add(new Model
        {
            OwnedBy = "FunAudioLLM",
            Type = "transcription",
            Name = "SenseVoiceSmall",
            Id = "FunAudioLLM/SenseVoiceSmall".ToModelId(GetIdentifier())
        });


        models.Add(new Model
        {
            OwnedBy = "TeleAI",
            Type = "transcription",
            Name = "TeleSpeechASR",
            Id = "TeleAI/TeleSpeechASR".ToModelId(GetIdentifier())
        });

        return models.Where(a => a.Type != "moderation"
            && a.Type != "code");
    }

}