using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.CompilerServices;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;

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

    private static AIToolCallContentPart CreatePrecontextTool(JsonElement task)
    {
        if (task.ValueKind != JsonValueKind.Object
            || !task.TryGetProperty("name", out var nameElement)
            || nameElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(nameElement.GetString())
            || !task.TryGetProperty("result", out var result))
            throw new JsonException("Interfaze precontext requires a task name and result.");

        return new AIToolCallContentPart
        {
            Type = "tool-call",
            ToolCallId = $"interfaze_precontext_{Guid.NewGuid():N}",
            ToolName = nameElement.GetString(),
            ProviderExecuted = true,
            State = "output-available",
            Input = JsonSerializer.SerializeToElement(new { }),
            Output = result.Clone()
        };
    }

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        var options = request.ToChatCompletionOptions(GetIdentifier());
        options.Stream = false;
        options.Store ??= false;
        var native = await CompleteChatAsync(options, cancellationToken);
        var response = native.ToUnifiedResponse(GetIdentifier());
        var items = new List<AIOutputItem>();
        if (native.AdditionalProperties?.TryGetValue("precontext", out var precontext) == true)
            items.AddRange(ReadPrecontext(precontext).Select(task => ToolOutputItem(CreatePrecontextTool(task))));

        foreach (var item in response.Output?.Items ?? [])
        {
            var content = new List<AIContentPart>();
            foreach (var part in item.Content ?? [])
            {
                if (part is not AITextContentPart text)
                {
                    content.Add(part);
                    continue;
                }
                var parser = new PrecontextParser();
                var pieces = parser.Push(text.Text).Concat(parser.Complete()).ToList();
                items.AddRange(pieces.Where(piece => piece.Task.HasValue)
                    .Select(piece => ToolOutputItem(CreatePrecontextTool(piece.Task!.Value))));
                content.Add(new AITextContentPart
                {
                    Type = "text", Text = string.Concat(pieces.Select(piece => piece.Text)), Metadata = text.Metadata
                });
            }
            items.Add(new AIOutputItem
            {
                Type = item.Type, Role = item.Role, Content = content, Metadata = item.Metadata
            });
        }
        return new AIResponse
        {
            ProviderId = response.ProviderId, Model = response.Model, Status = response.Status,
            Usage = response.Usage, Metadata = response.Metadata,
            Output = new AIOutput { Items = items, Metadata = response.Output?.Metadata }
        };
    }

    private static AIOutputItem ToolOutputItem(AIToolCallContentPart tool)
        => new() { Type = "tool-call", Role = "assistant", Content = [tool] };

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var parsers = new Dictionary<string, (PrecontextParser Parser, AIStreamEvent Source)>();
        await foreach (var streamEvent in this.StreamUnifiedViaChatCompletionsAsync(
            request, cancellationToken: cancellationToken))
        {
            var id = streamEvent.Event.Id ?? "";
            if (streamEvent.Event.Data is AITextDeltaEventData text)
            {
                if (!parsers.TryGetValue(id, out var state))
                    state = (new PrecontextParser(), streamEvent);
                parsers[id] = (state.Parser, streamEvent);
                foreach (var piece in state.Parser.Push(text.Delta))
                    foreach (var mapped in MapPrecontextPiece(piece, streamEvent))
                        yield return mapped;
                continue;
            }
            if (streamEvent.Event.Type == "text-end" && parsers.Remove(id, out var completed))
            {
                foreach (var piece in completed.Parser.Complete())
                    foreach (var mapped in MapPrecontextPiece(piece, completed.Source))
                        yield return mapped;
            }
            if (streamEvent.Event.Type == "finish")
            {
                foreach (var state in parsers.Values)
                    foreach (var piece in state.Parser.Complete())
                        foreach (var mapped in MapPrecontextPiece(piece, state.Source))
                            yield return mapped;
                parsers.Clear();
            }
            yield return streamEvent;
        }
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var state in parsers.Values)
            foreach (var piece in state.Parser.Complete())
                foreach (var mapped in MapPrecontextPiece(piece, state.Source))
                    yield return mapped;
    }

    private IEnumerable<AIStreamEvent> MapPrecontextPiece(PrecontextPiece piece, AIStreamEvent source)
    {
        AIStreamEvent Event(string type, string? id, object data) => new()
        {
            ProviderId = GetIdentifier(), Metadata = source.Metadata,
            Event = new AIEventEnvelope
            {
                Type = type, Id = id, Timestamp = source.Event.Timestamp, Data = data
            }
        };

        if (piece.Text is not null)
        {
            yield return Event("text-delta", source.Event.Id, new AITextDeltaEventData
            {
                Delta = piece.Text,
                ProviderMetadata = (source.Event.Data as AITextDeltaEventData)?.ProviderMetadata
            });
            yield break;
        }
        var tool = CreatePrecontextTool(piece.Task!.Value);
        yield return Event("tool-input-available", tool.ToolCallId, new AIToolInputAvailableEventData
        {
            ToolName = tool.ToolName!, Input = tool.Input!, ProviderExecuted = true
        });
        yield return Event("tool-output-available", tool.ToolCallId, new AIToolOutputAvailableEventData
        {
            ToolName = tool.ToolName, Output = tool.Output!, ProviderExecuted = true
        });
    }

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
