using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.ChatCompletions.Models;

namespace AIHappey.Core.Providers.Interfaze;

public partial class InterfazeProvider
{
    private static ChatCompletionOptions NormalizeChatOptions(ChatCompletionOptions options)
    {
        var normalized = JsonSerializer.Deserialize<ChatCompletionOptions>(
            JsonSerializer.Serialize(options, JsonSerializerOptions.Web), JsonSerializerOptions.Web)!;
        normalized.Headers = options.Headers is null
            ? null
            : new Dictionary<string, string>(options.Headers, StringComparer.OrdinalIgnoreCase);

        foreach (var message in normalized.Messages)
        {
            if (message.Content.ValueKind != JsonValueKind.Array)
                continue;

            var parts = JsonNode.Parse(message.Content.GetRawText())!.AsArray();
            for (var i = 0; i < parts.Count; i++)
            {
                var part = parts[i]!;
                if (part["type"]?.GetValue<string>() != "input_audio")
                    continue;

                var audio = part["input_audio"]!;
                var format = audio["format"]?.GetValue<string>()
                    ?? throw new ArgumentException("Interfaze audio input requires a format.");
                ArgumentException.ThrowIfNullOrWhiteSpace(format);
                var data = audio["data"]?.GetValue<string>()
                    ?? throw new ArgumentException("Interfaze audio input requires data.");
                parts[i] = CreateAudioFilePart(data, AudioMediaType(format), "audio." + format);
            }

            message.Content = JsonSerializer.SerializeToElement(parts);
        }

        return normalized;
    }

    private static string AudioMediaType(string format) => format.ToLowerInvariant() switch
    {
        "mp3" or "mpeg" => "audio/mpeg",
        "wav" or "wave" => "audio/wav",
        "m4a" or "mp4" => "audio/mp4",
        _ => "audio/" + format.ToLowerInvariant()
    };

    private static JsonObject CreateAudioFilePart(string data, string mediaType, string filename)
        => new()
        {
            ["type"] = "file",
            ["file"] = new JsonObject
            {
                ["filename"] = filename,
                ["file_data"] = NormalizeAudioData(data, mediaType)
            }
        };

    private static string NormalizeAudioData(string data, string mediaType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(data);
        if (Uri.TryCreate(data, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https")
            return data;

        var base64 = data;
        if (data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var separator = data.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
            if (separator < 0)
                throw new ArgumentException("Interfaze audio data URLs must use base64 encoding.");
            base64 = data[(separator + ";base64,".Length)..];
        }

        try
        {
            if (Convert.FromBase64String(base64).Length == 0)
                throw new ArgumentException("Interfaze audio data must not be empty.");
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Interfaze audio must be valid base64 data.", nameof(data), exception);
        }

        return data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? data
            : $"data:{mediaType};base64,{base64}";
    }

    private static IEnumerable<JsonElement> ReadPrecontext(JsonElement precontext)
    {
        if (precontext.ValueKind == JsonValueKind.Array)
        {
            foreach (var task in precontext.EnumerateArray())
                yield return task;
        }
        else if (precontext.ValueKind == JsonValueKind.Object)
            yield return precontext;
        else
            throw new JsonException("Interfaze precontext must be an object or array.");
    }

    private static JsonObject CreatePrecontextTool(JsonElement task, int? index = null)
    {
        if (task.ValueKind != JsonValueKind.Object
            || !task.TryGetProperty("name", out var nameElement)
            || nameElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(nameElement.GetString())
            || !task.TryGetProperty("result", out var result))
            throw new JsonException("Interfaze precontext requires a task name and result.");

        var tool = new JsonObject
        {
            ["id"] = $"interfaze_precontext_{Guid.NewGuid():N}",
            ["type"] = "function",
            ["provider_executed"] = true,
            ["function"] = new JsonObject
            {
                ["name"] = nameElement.GetString(),
                ["arguments"] = "{}",
                ["output"] = JsonNode.Parse(result.GetRawText())
            }
        };
        if (index.HasValue)
            tool["index"] = index.Value;
        return tool;
    }

    private static void AddPrecontextTools(ChatCompletion response)
    {
        if (response.AdditionalProperties?.TryGetValue("precontext", out var precontext) != true)
            return;

        var tools = ReadPrecontext(precontext).Select(task => CreatePrecontextTool(task)).ToList();
        if (tools.Count == 0)
            return;

        var choices = response.Choices.Select(choice => JsonSerializer.SerializeToNode(choice)!.AsObject()).ToList();
        if (choices.Count == 0)
            throw new JsonException("Interfaze returned precontext without a completion choice.");
        var message = choices[0]["message"]!.AsObject();
        var calls = message["tool_calls"] as JsonArray ?? new JsonArray();
        message["tool_calls"] = calls;
        foreach (var tool in tools)
            calls.Add(tool);
        response.Choices = choices;
    }

    private static IEnumerable<ChatCompletionUpdate> MapStreamUpdate(
        ChatCompletionUpdate update, Dictionary<int, PrecontextParser> parsers,
        Dictionary<(int Choice, int Tool), int> nativeToolIndexes, Func<int> nextToolIndex)
    {
        var choices = new List<object>();
        foreach (var rawChoice in update.Choices)
        {
            var choice = JsonSerializer.SerializeToNode(rawChoice)!.AsObject();
            var index = choice["index"]?.GetValue<int>() ?? 0;
            if (!parsers.TryGetValue(index, out var parser))
                parsers[index] = parser = new PrecontextParser();

            if (choice["delta"]?["tool_calls"] is JsonArray nativeTools)
            {
                foreach (var tool in nativeTools)
                {
                    var key = (index, tool!["index"]?.GetValue<int>() ?? 0);
                    if (!nativeToolIndexes.TryGetValue(key, out var mappedIndex))
                        nativeToolIndexes[key] = mappedIndex = nextToolIndex();
                    tool["index"] = mappedIndex;
                }
            }

            if (choice["delta"] is JsonObject delta && delta["content"] is JsonValue content
                && content.TryGetValue<string>(out var text))
            {
                delta.Remove("content");
                foreach (var piece in parser.Push(text))
                {
                    var pieceDelta = new JsonObject();
                    if (piece.Text is not null)
                        pieceDelta["content"] = piece.Text;
                    else
                        pieceDelta["tool_calls"] = new JsonArray(CreatePrecontextTool(piece.Task!.Value, nextToolIndex()));

                    yield return CopyUpdate(update, [new JsonObject { ["index"] = index, ["delta"] = pieceDelta }], includeUsage: false);
                }
            }

            if (choice["finish_reason"] is not null)
            {
                foreach (var piece in parser.Complete())
                    yield return CopyUpdate(update, [new JsonObject
                    {
                        ["index"] = index,
                        ["delta"] = new JsonObject { ["content"] = piece.Text }
                    }], includeUsage: false);
            }
            choices.Add(choice);
        }

        yield return CopyUpdate(update, choices, includeUsage: true);
    }

    private static ChatCompletionUpdate CopyUpdate(ChatCompletionUpdate source, IEnumerable<object> choices, bool includeUsage)
        => new()
        {
            Id = source.Id, Model = source.Model, Created = source.Created, Object = source.Object,
            ServiceTier = source.ServiceTier, Choices = choices,
            Usage = includeUsage ? source.Usage : null, AdditionalProperties = source.AdditionalProperties
        };

    private sealed record PrecontextPiece(string? Text = null, JsonElement? Task = null);

    private sealed class PrecontextParser
    {
        private const string Open = "<precontext>";
        private const string Close = "</precontext>";
        private string _pending = "";
        private bool _inPrecontext;

        public List<PrecontextPiece> Push(string text)
        {
            _pending += text;
            var pieces = new List<PrecontextPiece>();
            while (_pending.Length > 0)
            {
                if (_inPrecontext)
                {
                    var json = _pending.TrimStart();
                    var bytes = Encoding.UTF8.GetBytes(json);
                    var reader = new Utf8JsonReader(bytes, isFinalBlock: false, state: default);
                    if (!JsonDocument.TryParseValue(ref reader, out var document))
                        break;

                    using (document)
                    {
                        var rest = Encoding.UTF8.GetString(bytes[(int)reader.BytesConsumed..]).TrimStart();
                        if (rest.Length < Close.Length && Close.StartsWith(rest, StringComparison.Ordinal))
                            break;
                        if (!rest.StartsWith(Close, StringComparison.Ordinal))
                            throw new JsonException("Interfaze precontext is missing its closing tag.");

                        foreach (var task in ReadPrecontext(document.RootElement))
                            pieces.Add(new(Task: task.Clone()));
                        _pending = rest[Close.Length..];
                        _inPrecontext = false;
                    }
                    continue;
                }

                var start = _pending.IndexOf(Open, StringComparison.Ordinal);
                if (start >= 0)
                {
                    if (start > 0)
                        pieces.Add(new(Text: _pending[..start]));
                    _pending = _pending[(start + Open.Length)..];
                    _inPrecontext = true;
                    continue;
                }

                var keep = Math.Min(_pending.Length, Open.Length - 1);
                while (keep > 0 && !Open.StartsWith(_pending[^keep..], StringComparison.Ordinal))
                    keep--;
                if (_pending.Length > keep)
                    pieces.Add(new(Text: _pending[..(_pending.Length - keep)]));
                _pending = keep > 0 ? _pending[^keep..] : "";
                break;
            }
            return pieces;
        }

        public List<PrecontextPiece> Complete()
        {
            if (_inPrecontext)
                throw new JsonException("Interfaze stream ended with incomplete precontext.");
            var pieces = _pending.Length > 0 ? new List<PrecontextPiece> { new(Text: _pending) } : [];
            _pending = "";
            return pieces;
        }
    }
}
