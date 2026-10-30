using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.XVoice;

public partial class XVoiceProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = keyResolver.Resolve(ProviderId);
        if (string.IsNullOrWhiteSpace(key))
            return [];
        return await cache.GetOrCreateAsync<IEnumerable<Model>>(
            this.GetCacheKey(key),
            async ct =>
            {
                var native = await GetModelsAsync(cancellationToken: ct);
                var result = new List<Model>();
                foreach (var item in native.GetProperty("data").EnumerateArray())
                {
                    var type = TranscriptionString(item, "type");
                    if (type is not ("tts" or "stt"))
                        continue;
                    var id = item.GetProperty("id").GetString()!;
                    result.Add(ToModel(item));
                    if (type == "tts")
                    {
                        var voices = await GetVoicesAsync(id, cancellationToken: ct);
                        foreach (var voice in voices.GetProperty("data").EnumerateArray())
                            result.Add(ToModel(item, voice));
                    }
                }
                return result;
            }, TimeSpan.FromHours(4), cancellationToken: cancellationToken);
    }

    public Task<JsonElement> GetModelsAsync(string? type = null, CancellationToken cancellationToken = default)
        => GetJsonAsync("v1/models" + (type is null ? "" : $"?type={Uri.EscapeDataString(type)}"), cancellationToken);

    public Task<JsonElement> GetModelInfoAsync(string model, CancellationToken cancellationToken = default)
        => GetJsonAsync($"v1/models/{Uri.EscapeDataString(ParseSpeechModel(model).Model)}", cancellationToken);

    public Task<JsonElement> GetVoicesAsync(
        string? model = null, string? language = null, string? locale = null, CancellationToken cancellationToken = default)
    {
        var query = new List<string>();
        if (model is not null) query.Add("model=" + Uri.EscapeDataString(ParseSpeechModel(model).Model));
        if (language is not null) query.Add("language=" + Uri.EscapeDataString(language));
        if (locale is not null) query.Add("locale=" + Uri.EscapeDataString(locale));
        return GetJsonAsync("v1/voices" + (query.Count == 0 ? "" : "?" + string.Join("&", query)), cancellationToken);
    }

    private async Task<JsonElement> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using var response = await SendAsync(request, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.Clone();
    }

    private static Model ToModel(JsonElement native, JsonElement? voice = null)
    {
        var id = native.GetProperty("id").GetString()!;
        var tags = new List<string>();
        foreach (var field in new[] { "capabilities", "supported_languages" })
            if (native.TryGetProperty(field, out var values) && values.ValueKind == JsonValueKind.Array)
                tags.AddRange(values.EnumerateArray().Select(v => v.GetString()!));
        if (TranscriptionString(native, "status") is { } status)
            tags.Add(status);
        if (TranscriptionString(native, "public_version") is { } version)
            tags.Add("version:" + version);
        var name = TranscriptionString(native, "name") ?? id;
        var description = TranscriptionString(native, "description");
        if (native.TryGetProperty("speech_options", out var speech) && speech.ValueKind == JsonValueKind.Object)
            description = $"{description} speech_options: {speech.GetRawText()}".Trim();
        if (voice is { } v)
        {
            id += "/" + v.GetProperty("id").GetString();
            name += " / " + (TranscriptionString(v, "name") ?? v.GetProperty("id").GetString());
            foreach (var field in new[] { "gender", "locale", "status" })
                if (TranscriptionString(v, field) is { } value)
                    tags.Add(value);
            if (v.TryGetProperty("languages", out var languages))
                tags.AddRange(languages.EnumerateArray().Select(l => l.GetString()!));
            description = $"{description} Voice: {v.GetRawText()}".Trim();
        }
        return new Model
        {
            Id = $"{ProviderId}/{id}",
            Name = name,
            Description = description,
            Type = TranscriptionString(native, "type") == "tts" ? "speech" : "transcription",
            Created = native.TryGetProperty("created", out var created) ? created.GetInt64() : null,
            OwnedBy = TranscriptionString(native, "owned_by") ?? ProviderId,
            Tags = tags.Distinct(StringComparer.Ordinal).ToArray()
        };
    }

    private static (string Model, string? Voice) ParseSpeechModel(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var local = model.StartsWith(ProviderId + "/", StringComparison.OrdinalIgnoreCase)
            ? model[(ProviderId.Length + 1)..] : model;
        var parts = local.Split('/');
        if (parts.Length > 2 || parts.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("xVoice model must be [xvoice/]model[/voice].", nameof(model));
        return (parts[0], parts.Length == 2 ? parts[1] : null);
    }
}
