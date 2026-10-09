using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.OpenAI;

/// <summary>Provider transport only. The public Vercel request and response are never changed.</summary>
public static class OpenAIFileTranscription
{
    public static async Task<TranscriptionResponse> GenerateAsync(HttpClient client, TranscriptionRequest request, CancellationToken ct)
    {
        var options = request.ProviderOptions?.TryGetValue("openai", out var value) == true && value.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(value.GetRawText())!.AsObject() : new JsonObject();
        if (options["stream"]?.GetValue<bool>() == true)
            throw new NotSupportedException("Use the existing Vercel streaming transcription API for streaming requests.");
        var format = options["response_format"]?.GetValue<string>() ?? "json";
        options["response_format"] = format;
        var audio = request.Audio.ToString() ?? "";
        if (audio.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) audio = audio[(audio.IndexOf(',') + 1)..];
        var bytes = Convert.FromBase64String(audio);
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(request.MediaType);
        var filename = "audio" + request.MediaType.GetAudioExtension();
        form.Add(file, "file", filename);
        form.Add(new StringContent(request.Model), "model");
        foreach (var property in options)
            if (property.Key is not "file" and not "model") Add(form, property.Key, property.Value);
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/audio/transcriptions") { Content = form };
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream(); var block = new byte[81920];
        while (await stream.ReadAsync(block, ct) is var read && read > 0)
        {
            if (buffer.Length + read > 16 * 1024 * 1024) throw new InvalidOperationException("Transcription response is too large.");
            await buffer.WriteAsync(block.AsMemory(0, read), ct);
        }
        var raw = Encoding.UTF8.GetString(buffer.ToArray());
        JsonObject body;
        if (format is "text" or "srt" or "vtt") body = new() { ["text"] = raw };
        else body = JsonNode.Parse(raw)?.AsObject() ?? throw new JsonException("Invalid transcription response.");
        var text = body["text"]?.GetValue<string>() ?? throw new JsonException("Transcription response has no text.");
        var details = new JsonObject();
        foreach (var key in new[] { "languages", "usage", "logprobs" })
            if (body[key] is { } node) details[key] = node.DeepClone();
        var segments = new List<TranscriptionSegment>();
        if (body["segments"] is JsonArray list)
            foreach (var segment in list.OfType<JsonObject>())
                if (segment["start"] is JsonValue start && segment["end"] is JsonValue end)
                    segments.Add(new() { Text = segment["text"]?.GetValue<string>() ?? "", StartSecond = start.GetValue<float>(), EndSecond = end.GetValue<float>() });
        var duration = body["duration"]?.GetValue<float>();
        if (duration is null && body["usage"] is JsonObject usage && usage["type"]?.GetValue<string>() == "duration") duration = usage["seconds"]?.GetValue<float>();
        return new()
        {
            Text = text, Language = body["language"]?.GetValue<string>(), DurationInSeconds = duration, Segments = segments,
            ProviderMetadata = new() { ["openai"] = JsonSerializer.SerializeToElement(details) },
            Request = new() { Body = new JsonObject { ["model"] = request.Model, ["file"] = new JsonObject { ["name"] = filename, ["mediaType"] = request.MediaType, ["size"] = bytes.Length }, ["options"] = options.DeepClone() }.ToJsonString() },
            Response = new() { Timestamp = DateTime.UtcNow, ModelId = "openai/" + request.Model, Body = format is "text" or "srt" or "vtt" ? raw : JsonSerializer.SerializeToElement(body) }
        };
    }

    private static void Add(MultipartFormDataContent form, string name, JsonNode? node)
    {
        if (node is null) return;
        if (node is JsonArray array) { foreach (var item in array) Add(form, name + "[]", item); return; }
        if (node is JsonObject obj) { foreach (var item in obj) Add(form, name + "[" + item.Key + "]", item.Value); return; }
        form.Add(new StringContent(node is JsonValue value && value.TryGetValue<string>(out var text) ? text : node.ToJsonString(), Encoding.UTF8), name);
    }
}
