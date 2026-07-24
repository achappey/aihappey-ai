using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Zyphra;

public partial class ZyphraProvider
{
    private async Task<IReadOnlyList<ZyphraVoice>> ListZyphraVoicesAsync(string apiKey, CancellationToken cancellationToken)
    {
        var defaultVoicesTask = ListZyphraVoicesAsync("v1/audio/default-voices", apiKey, isCloned: false, cancellationToken);
        var clonedVoicesTask = ListZyphraVoicesAsync("v1/audio/cloned-voices", apiKey, isCloned: true, cancellationToken);

        await Task.WhenAll(defaultVoicesTask, clonedVoicesTask);

        return [.. defaultVoicesTask.Result
            .Concat(clonedVoicesTask.Result)
            .Where(voice => !string.IsNullOrWhiteSpace(voice.VoiceId))
            .GroupBy(voice => voice.VoiceId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())];
    }

    private async Task<IReadOnlyList<ZyphraVoice>> ListZyphraVoicesAsync(
        string endpoint,
        string apiKey,
        bool isCloned,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await _client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Zyphra voices list failed ({(int)response.StatusCode}): {body}");

        using var document = JsonDocument.Parse(body);
        return ParseZyphraVoices(document.RootElement, isCloned);
    }

    private IEnumerable<Model> BuildZyphraVoiceShortcutModels(IEnumerable<ZyphraVoice> voices)
        => voices
            .Where(voice => !string.IsNullOrWhiteSpace(voice.VoiceId))
            .Select(voice => new Model
            {
                Id = $"{ZyphraSpeechModel}/{voice.VoiceId}".ToModelId(GetIdentifier()),
                Name = $"ZONOS2 · {voice.DisplayName} ({voice.VoiceId})",
                OwnedBy = nameof(Zyphra),
                Type = "speech",
                Description = BuildZyphraVoiceDescription(voice),
                Tags = voice.IsCloned ? ["voice"] : ["voice"]
            });

    private static IReadOnlyList<ZyphraVoice> ParseZyphraVoices(JsonElement root, bool isCloned)
    {
        if (root.ValueKind != JsonValueKind.Array)
            return [];

        var voices = new List<ZyphraVoice>();
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var voiceId = ReadZyphraVoiceString(item, "voice_id");
            if (string.IsNullOrWhiteSpace(voiceId))
                continue;

            var name = ReadZyphraVoiceString(item, "display_name")
                ?? ReadZyphraVoiceString(item, "name")
                ?? voiceId;

            voices.Add(new ZyphraVoice(
                voiceId.Trim(),
                name.Trim(),
                ReadZyphraVoiceString(item, "description"),
                isCloned));
        }

        return voices;
    }

    private static string BuildZyphraVoiceDescription(ZyphraVoice voice)
    {
        var source = voice.IsCloned ? "cloned" : "default";
        var description = $"Zyphra ZONOS2 {source} voice shortcut for {voice.DisplayName} ({voice.VoiceId}).";
        return string.IsNullOrWhiteSpace(voice.Description)
            ? description
            : $"{description} {voice.Description.Trim()}";
    }

    private static string? ReadZyphraVoiceString(JsonElement value, string propertyName)
    {
        if (value.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String)
        {
            return property.GetString();
        }

        return null;
    }

    private sealed record ZyphraVoice(string VoiceId, string DisplayName, string? Description, bool IsCloned);
}
