using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;

namespace AIHappey.Core.Providers.PawaAI;

public partial class PawaAIProvider
{
    private const string AgentModelPrefix = "agent/";

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        var cacheKey = this.GetCacheKey();

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
                using var resp = await _client.SendAsync(req, ct);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(ct);
                    throw new Exception($"PawaAI API error: {err}");
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                var models = new List<Model>();
                var root = doc.RootElement;

                var arr = root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array
                        ? dataEl.EnumerateArray()
                        : Enumerable.Empty<JsonElement>();

                foreach (var el in arr)
                {
                    Model model = new();

                    if (el.TryGetProperty("name", out var idEl))
                    {
                        model.Id = idEl.GetString()?.ToModelId(GetIdentifier()) ?? "";
                        model.Name = idEl.GetString() ?? "";
                    }

                    model.ContextWindow = el.TryGetProperty("contextLength", out var v) &&
                        v.ValueKind == JsonValueKind.Number
                            ? v.GetInt32()
                            : null;

                    if (el.TryGetProperty("aliasName", out var nameEl))
                        model.Name = nameEl.GetString() ?? model.Name;

                    if (el.TryGetProperty("modelType", out var typeEl))
                    {
                        var modelType = typeEl.GetString();
                        var inputType = el.TryGetProperty("inputType", out var inputEl)
                            ? inputEl.GetString()
                            : null;

                        var outputType = el.TryGetProperty("outputType", out var outputEl)
                            ? outputEl.GetString()
                            : null;

                        model.Type = modelType switch
                        {
                            "chat" => "language",
                            "embedding" => "embedding",
                            "parsing" => "language",
                            "voice" when inputType == "audio" => "transcription",
                            "voice" when outputType == "audio" => "speech",
                            _ => model.Id.GuessModelType()
                        };
                    }

                    if (!string.IsNullOrEmpty(model.Id))
                        models.Add(model);
                }

                var agents = await ListPawaAgentsAsync(ct);
                var languageModels = models
                    .Where(model => string.Equals(model.Type, "language", StringComparison.OrdinalIgnoreCase)
                        && !NormalizePawaModelId(model.Id).StartsWith(AgentModelPrefix, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var agentModels = agents
                    .Where(agent => string.Equals(agent.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(agent.AgentReferenceId))
                    .SelectMany(agent => languageModels.Select(languageModel =>
                    {
                        var languageModelId = NormalizePawaModelId(languageModel.Id);
                        return new Model
                        {
                            Id = $"{AgentModelPrefix}{agent.AgentReferenceId}/{languageModelId}".ToModelId(GetIdentifier()),
                            Name = $"{agent.Name} · {languageModel.Name}",
                            Description = string.IsNullOrWhiteSpace(agent.Description)
                                ? $"Pawa AI agent backed by {languageModel.Name}."
                                : $"{agent.Description} Backed by {languageModel.Name}.",
                            OwnedBy = GetIdentifier(),
                            Type = "language",
                            ContextWindow = languageModel.ContextWindow,
                            MaxTokens = languageModel.MaxTokens,
                            Created = ParsePawaDate(agent.CreatedAt),
                            Tags = ["agent", agent.AgentReferenceId!, languageModelId]
                        };
                    }));

                return models.Concat(agentModels).ToList();
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private static long? ParsePawaDate(string? value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUnixTimeSeconds()
            : null;
}
