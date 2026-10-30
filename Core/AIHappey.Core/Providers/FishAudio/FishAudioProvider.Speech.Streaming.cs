using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.FishAudio;

public partial class FishAudioProvider
{
    private async IAsyncEnumerable<StreamingSpeechPart> StreamFishSpeechAsync(StreamingSpeechRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        if (request.Text is not { Length: > 0 }) throw new ArgumentException("At least one speech text part is required.");
        var text = new StringBuilder();
        Dictionary<string, object?>? payload = null;
        JsonElement? configuration = null;
        foreach (var part in request.Text)
        {
            if (part is null || part.Type != "text" || part.Text is null) throw new ArgumentException("Speech text parts must contain text.");
            var options = FishOptions(request.ProviderOptions);
            foreach (var option in FishOptions(part.ProviderOptions)) options[option.Key] = option.Value;
            var current = BuildFishSpeechPayload(new SpeechRequest { Model = request.Model, Text = "validation", Voice = part.Voice ?? request.Voice,
                Speed = (float?)request.Speed, Language = request.Language, OutputFormat = request.OutputFormat?.Type,
                ProviderOptions = new() { [GetIdentifier()] = JsonSerializer.SerializeToElement(options) } }, request.OutputFormat?.Rate, true);
            var normalized = JsonSerializer.SerializeToElement(current);
            if (configuration is not null && !JsonElement.DeepEquals(configuration.Value, normalized))
                throw new NotSupportedException("Fish Audio streaming requires one effective voice and options configuration across text parts.");
            configuration = normalized; payload ??= current;
            text.Append(part.Text); // Boundaries can occur mid-word. Never insert separators.
        }
        if (string.IsNullOrWhiteSpace(text.ToString())) throw new ArgumentException("Speech text is required.");
        payload!["text"] = text.ToString();
        var format = (string)payload["format"]!;
        var rate = payload.TryGetValue("sample_rate", out var sr) && sr is not null && !(sr is JsonElement je && je.ValueKind == JsonValueKind.Null)
            ? JsonSerializer.SerializeToElement(sr).GetInt32() : format == "opus" ? 48000 : 44100;
        using var message = FishRequest(HttpMethod.Post, "v1/tts/stream/with-timestamp", payload);
        message.Headers.Add("model", NormalizeModel(request.Model));
        message.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"FishAudio streaming TTS failed ({(int)response.StatusCode}): {error}");
        }
        if (response.Content.Headers.ContentType?.MediaType is not "text/event-stream")
            throw new InvalidOperationException("Fish Audio streaming response is not an SSE stream.");
        yield return new SpeechStreamStartPart { Warnings = string.IsNullOrWhiteSpace(request.Instructions) ? [] : [new { type = "unsupported", feature = "instructions" }] };
        yield return new SpeechResponseMetadataPart { Timestamp = DateTime.UtcNow, ModelId = request.Model, Headers = response.GetHeaders() };
        var alignments = new Dictionary<int, JsonElement>();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await foreach (var data in ReadFishSseAsync(stream, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (data == "[DONE]") break;
            JsonElement item;
            try { item = JsonSerializer.Deserialize<JsonElement>(data); }
            catch (JsonException) { throw new InvalidOperationException("Fish Audio returned invalid speech SSE JSON."); }
            if (item.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Fish Audio returned an invalid speech event.");
            if (item.TryGetProperty("error", out _)) throw new InvalidOperationException("Fish Audio speech stream reported an error.");
            if (!item.TryGetProperty("audio_base64", out var encoded) || encoded.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("Fish Audio speech event is missing audio_base64.");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(encoded.GetString()!); }
            catch (FormatException) { throw new InvalidOperationException("Fish Audio speech event contains invalid base64 audio."); }
            if (request.IncludeRawChunks == true) yield return new SpeechRawPart { RawValue = item.Clone() };
            var metadata = new Dictionary<string, JsonElement>();
            foreach (var property in item.EnumerateObject()) if (property.Name != "audio_base64") metadata[property.Name] = property.Value.Clone();
            if (item.TryGetProperty("alignment", out var alignment) && alignment.ValueKind == JsonValueKind.Object && item.TryGetProperty("chunk_seq", out var seq))
            {
                var snapshot = JsonSerializer.SerializeToElement(metadata);
                alignments[seq.GetInt32()] = snapshot; // Cumulative snapshots supersede previous snapshots, never append.
            }
            yield return new SpeechAudioPart { Audio = Convert.ToBase64String(bytes), AudioFormat = new() { Type = ResolveSpeechMimeType(null, format), Rate = rate },
                ProviderMetadata = new() { [GetIdentifier()] = JsonSerializer.SerializeToElement(metadata) } };
        }
        ct.ThrowIfCancellationRequested();
        yield return new SpeechFinishPart { ProviderMetadata = new() { [GetIdentifier()] = JsonSerializer.SerializeToElement(new { alignments = alignments.OrderBy(p => p.Key).Select(p => p.Value).ToArray() }) } };
    }

    private static async IAsyncEnumerable<string> ReadFishSseAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true);
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0) { yield return data.ToString(0, data.Length - 1); data.Clear(); }
                continue;
            }
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var value = line[5..];
            if (value.StartsWith(' ')) value = value[1..];
            data.Append(value).Append('\n');
            if (data.Length > 8 * 1024 * 1024) throw new InvalidOperationException("Fish Audio speech SSE event exceeded its limit.");
        }
        ct.ThrowIfCancellationRequested();
        if (data.Length > 0) yield return data.ToString(0, data.Length - 1);
    }
}
