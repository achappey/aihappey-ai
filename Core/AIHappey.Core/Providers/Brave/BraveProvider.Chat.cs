using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.Brave;

public partial class BraveProvider
{
    private const string CitationStartTag = "<citation>";
    private const string CitationEndTag = "</citation>";
    private const string EnumStartTag = "<enum_start>";
    private const string EnumStartEndTag = "</enum_start>";
    private const string EnumItemStartTag = "<enum_item>";
    private const string EnumItemEndTag = "</enum_item>";
    private const string EnumEndTag = "<enum_end>";
    private const string EnumEndEndTag = "</enum_end>";
    private const string ResearchQueriesStartTag = "<queries>";
    private const string ResearchQueriesEndTag = "</queries>";
    private const string ResearchAnalyzingStartTag = "<analyzing>";
    private const string ResearchAnalyzingEndTag = "</analyzing>";
    private const string ResearchThinkingStartTag = "<thinking>";
    private const string ResearchThinkingEndTag = "</thinking>";
    private const string ResearchBlindspotsStartTag = "<blindspots>";
    private const string ResearchBlindspotsEndTag = "</blindspots>";
    private const string ResearchProgressStartTag = "<progress>";
    private const string ResearchProgressEndTag = "</progress>";
    private const string ResearchAnswerStartTag = "<answer>";
    private const string ResearchAnswerEndTag = "</answer>";
    private const string ResearchUsageStartTag = "<usage>";
    private const string ResearchUsageEndTag = "</usage>";
    private const string BraveResearchToolName = "web_search";

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var unifiedRequest = chatRequest.ToUnifiedRequest(GetIdentifier());
        var emittedSourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var emittedImageUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var researchState = new BraveResearchStreamState();
        TextStartUIMessageStreamPart? pendingTextStart = null;
        var textStarted = false;

        await foreach (var part in this.StreamUnifiedAsync(
            unifiedRequest,
            cancellationToken))
        {
            foreach (var uiPart in part.Event.ToUIMessagePart(GetIdentifier()))
            {
                if (uiPart is TextStartUIMessageStreamPart textStartPart)
                {
                    pendingTextStart ??= textStartPart;
                    continue;
                }

                if (uiPart is TextEndUIMessageStreamPart textEndPart)
                {
                    foreach (var flushedPart in FlushPendingResearchText(
                        textEndPart.Id,
                        textEndPart.ProviderMetadata,
                        researchState,
                        emittedSourceIds))
                    {
                        if (flushedPart is TextDeltaUIMessageStreamPart flushedTextPart)
                        {
                            await foreach (var processedFlushedPart in ProcessConvertedBravePart(
                                flushedTextPart,
                                emittedSourceIds,
                                emittedImageUrls,
                                cancellationToken))
                            {
                                if (processedFlushedPart is TextDeltaUIMessageStreamPart && !textStarted)
                                {
                                    textStarted = true;
                                    if (pendingTextStart is not null)
                                    {
                                        yield return pendingTextStart;
                                        pendingTextStart = null;
                                    }
                                }

                                yield return processedFlushedPart;
                            }
                        }
                        else
                        {
                            yield return flushedPart;
                        }
                    }

                    if (textStarted)
                    {
                        yield return textEndPart;
                        textStarted = false;
                    }

                    pendingTextStart = null;
                    continue;
                }

                if (TryConvertEnumControlTextDelta(uiPart))
                    continue;

                if (uiPart is TextDeltaUIMessageStreamPart textPart)
                {
                    await foreach (var processedPart in ProcessBraveTextDelta(
                        textPart,
                        researchState,
                        emittedSourceIds,
                        emittedImageUrls,
                        cancellationToken))
                    {
                        if (processedPart is TextDeltaUIMessageStreamPart && !textStarted)
                        {
                            textStarted = true;
                            if (pendingTextStart is not null)
                            {
                                yield return pendingTextStart;
                                pendingTextStart = null;
                            }
                        }

                        yield return processedPart;
                    }

                    continue;
                }

                if (TryConvertCitationTextDelta(uiPart, out var sourcePart))
                {
                    if (emittedSourceIds.Add(sourcePart.SourceId))
                        yield return sourcePart;

                    continue;
                }

                yield return uiPart;
            }
        }

        foreach (var flushedPart in FlushPendingResearchText(
            pendingTextStart?.Id ?? Guid.NewGuid().ToString("N"),
            pendingTextStart?.ProviderMetadata,
            researchState,
            emittedSourceIds))
        {
            if (flushedPart is TextDeltaUIMessageStreamPart && !textStarted)
            {
                textStarted = true;
                if (pendingTextStart is not null)
                {
                    yield return pendingTextStart;
                    pendingTextStart = null;
                }
            }

            yield return flushedPart;
        }

        yield break;
    }

    private async IAsyncEnumerable<UIMessagePart> ProcessBraveTextDelta(
        TextDeltaUIMessageStreamPart textPart,
        BraveResearchStreamState researchState,
        HashSet<string> emittedSourceIds,
        HashSet<string> emittedImageUrls,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var convertedPart in ConvertResearchTextDelta(textPart, researchState, emittedSourceIds))
        {
            await foreach (var processedPart in ProcessConvertedBravePart(
                convertedPart,
                emittedSourceIds,
                emittedImageUrls,
                cancellationToken))
            {
                yield return processedPart;
            }
        }
    }

    private async IAsyncEnumerable<UIMessagePart> ProcessConvertedBravePart(
        UIMessagePart convertedPart,
        HashSet<string> emittedSourceIds,
        HashSet<string> emittedImageUrls,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (TryConvertEnumControlTextDelta(convertedPart))
            yield break;

        if (convertedPart is TextDeltaUIMessageStreamPart convertedTextPart
            && TryParseEnumItemBlock(convertedTextPart.Delta, out var entity))
        {
            await foreach (var entityPart in ConvertEnumEntityToUIPartsAsync(
                convertedTextPart,
                entity,
                emittedSourceIds,
                emittedImageUrls,
                cancellationToken))
            {
                yield return entityPart;
            }

            yield break;
        }

        if (TryConvertCitationTextDelta(convertedPart, out var sourcePart))
        {
            if (emittedSourceIds.Add(sourcePart.SourceId))
                yield return sourcePart;

            yield break;
        }

        yield return convertedPart;
    }

    private IEnumerable<UIMessagePart> ConvertResearchTextDelta(
        TextDeltaUIMessageStreamPart textPart,
        BraveResearchStreamState researchState,
        HashSet<string> emittedSourceIds)
    {
        foreach (var segment in BraveResearchTaggedTextParser.Append(researchState, textPart.Delta, textPart.Id, textPart.ProviderMetadata))
        {
            foreach (var part in ConvertResearchSegment(textPart, segment, researchState, emittedSourceIds))
                yield return part;
        }
    }

    private IEnumerable<UIMessagePart> FlushPendingResearchText(
        string id,
        Dictionary<string, object>? providerMetadata,
        BraveResearchStreamState state,
        HashSet<string> emittedSourceIds)
    {
        foreach (var segment in BraveResearchTaggedTextParser.Flush(state, id, providerMetadata))
        {
            var textPart = new TextDeltaUIMessageStreamPart
            {
                Id = segment.Id,
                Delta = segment.Payload,
                // ProviderMetadata = segment.ProviderMetadata
            };

            foreach (var part in ConvertResearchSegment(textPart, segment, state, emittedSourceIds))
                yield return part;
        }
    }

    private IEnumerable<UIMessagePart> ConvertResearchSegment(
        TextDeltaUIMessageStreamPart textPart,
        BraveResearchTextSegment segment,
        BraveResearchStreamState state,
        HashSet<string> emittedSourceIds)
    {
        if (segment.Tag is null)
        {
            if (!string.IsNullOrEmpty(segment.Payload))
            {
                yield return new TextDeltaUIMessageStreamPart
                {
                    Id = textPart.Id,
                    Delta = segment.Payload,
                    //ProviderMetadata = textPart.ProviderMetadata
                };
            }

            yield break;
        }

        var normalizedTag = segment.Tag.ToLowerInvariant();
        if (normalizedTag == "usage")
            yield break;

        JsonElement? payload = TryParseJsonPayload(segment.Payload, out var parsedPayload)
            ? parsedPayload
            : null;

        if (normalizedTag == "answer")
        {
            var answerText = payload is JsonElement answerPayload
                ? GetString(answerPayload, "answer") ?? ExtractJsonScalarText(answerPayload)
                : segment.Payload;

            if (!string.IsNullOrEmpty(answerText))
            {
                yield return new TextDeltaUIMessageStreamPart
                {
                    Id = textPart.Id,
                    Delta = answerText,
                    //ProviderMetadata = textPart.ProviderMetadata
                };
            }

            yield break;
        }

        var rawPayload = payload?.Clone();
        var providerMetadata = CreateResearchProviderMetadata(normalizedTag, rawPayload, segment.Payload);

        foreach (var reasoningPart in CreateResearchReasoningParts(textPart.Id, normalizedTag, payload, segment.Payload, 
            providerMetadata))
            yield return reasoningPart;

        if (normalizedTag == "queries")
        {
            var toolCall = state.CreateToolCall(segment.Payload, payload);
            var inputText = toolCall.InputJson;

            yield return new ToolCallStreamingStartPart
            {
                ToolCallId = toolCall.Id,
                ToolName = BraveResearchToolName,
                Title = "Brave research search",
                ProviderExecuted = true,
                ProviderMetadata = CreateResearchToolProviderMetadata(toolCall.Id, normalizedTag, rawPayload, segment.Payload)
            };

            yield return new ToolCallDeltaPart
            {
                ToolCallId = toolCall.Id,
                InputTextDelta = inputText
            };

            yield return new ToolCallPart
            {
                ToolCallId = toolCall.Id,
                ToolName = BraveResearchToolName,
                Title = "Brave research search",
                ProviderExecuted = true,
                Input = toolCall.Input,
                ProviderMetadata = CreateResearchToolProviderMetadata(toolCall.Id, normalizedTag, rawPayload, segment.Payload)
            };

            yield break;
        }

        if (normalizedTag == "thinking" && payload is JsonElement thinkingPayload)
        {
            var urls = EnumerateStringArray(thinkingPayload, "urls_selected").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (urls.Count == 0)
                yield break;

            var toolCall = state.GetOrCreateToolCallForOutput(thinkingPayload);
            yield return CreateResearchToolOutputPart(toolCall, thinkingPayload, urls, segment.Payload);

            foreach (var sourcePart in CreateResearchSourceParts(thinkingPayload, urls))
            {
                if (emittedSourceIds.Add(sourcePart.SourceId))
                    yield return sourcePart;
            }
        }
    }

    private static IEnumerable<UIMessagePart> CreateResearchReasoningParts(
        string id,
        string tag,
        JsonElement? payload,
        string rawPayload,
        Dictionary<string, Dictionary<string, object?>> providerMetadata)
    {
        var reasoningId = $"{id}:brave-research:{tag}:{Guid.NewGuid():N}";
        var text = FormatResearchReasoningText(tag, payload, rawPayload);

        yield return new ReasoningStartUIPart
        {
            Id = reasoningId,
         //   ProviderMetadata = providerMetadata
        };

        if (!string.IsNullOrWhiteSpace(text))
        {
            yield return new ReasoningDeltaUIPart
            {
                Id = reasoningId,
                Delta = text,
        //        ProviderMetadata = providerMetadata
            };
        }

        yield return new ReasoningEndUIPart
        {
            Id = reasoningId,
            ProviderMetadata = new Dictionary<string, Dictionary<string, object>>
            {
            //    ["brave"] = providerMetadata
            }
        };
    }

    private static string FormatResearchReasoningText(string tag, JsonElement? payload, string rawPayload)
    {
        if (payload is not JsonElement element)
            return $"Brave research {tag}: {rawPayload}";

        return tag switch
        {
            "queries" => $"Searching for: {string.Join(", ", EnumerateStringArray(element, "queries"))}",
            "analyzing" => $"Analyzing {GetString(element, "query") ?? "query"}: {GetInt(element, "urls") ?? 0} URLs, {GetInt(element, "new_urls") ?? 0} new URLs.",
            "thinking" => $"Selected {EnumerateStringArray(element, "urls_selected").Count()} sources for {GetString(element, "query") ?? "query"}.",
            "blindspots" when element.ValueKind == JsonValueKind.Array => $"Blindspots: {string.Join("; ", EnumerateStringValues(element))}",
            "progress" => $"Research progress: {rawPayload}",
            _ => $"Brave research {tag}: {rawPayload}"
        };
    }

    private static ToolOutputAvailablePart CreateResearchToolOutputPart(
        BraveResearchToolCall toolCall,
        JsonElement thinkingPayload,
        IReadOnlyList<string> urls,
        string rawPayload)
    {
        var structuredContent = JsonSerializer.SerializeToElement(new
        {
            type = "brave_research_sources",
            query = GetString(thinkingPayload, "query"),
            urls_analyzed = GetInt(thinkingPayload, "urls_analyzed"),
            sources = urls.Select((url, index) => new
            {
                url,
                title = url,
                index
            }).ToArray(),
            urls_selected = urls
        }, JsonSerializerOptions.Web);

        return new ToolOutputAvailablePart
        {
            ToolCallId = toolCall.Id,
            ProviderExecuted = true,
            Output = new ModelContextProtocol.Protocol.CallToolResult
            {
                IsError = false,
                StructuredContent = structuredContent,
                Content = [$"Brave selected {urls.Count} source URLs for {GetString(thinkingPayload, "query") ?? "the research query"}.".ToTextContentBlock()]
            },
            ProviderMetadata = CreateResearchToolProviderMetadata(toolCall.Id, "thinking", thinkingPayload.Clone(), rawPayload)
        };
    }

    private static IEnumerable<SourceUIPart> CreateResearchSourceParts(JsonElement thinkingPayload, IReadOnlyList<string> urls)
    {
        var query = GetString(thinkingPayload, "query");

        foreach (var url in urls)
        {
            yield return new SourceUIPart
            {
                SourceId = $"brave-research-{url}",
                Url = url,
                Title = url,
                ProviderMetadata = new Dictionary<string, Dictionary<string, object>>
                {
                    ["brave"] = new()
                    {
                        ["kind"] = "research_source",
                        ["query"] = query ?? string.Empty,
                        ["raw"] = thinkingPayload.Clone()
                    }
                }
            };
        }
    }

    private static Dictionary<string, Dictionary<string, object?>> CreateResearchProviderMetadata(string tag, JsonElement? payload, string rawPayload)
    {
        var metadata = new Dictionary<string, Dictionary<string, object?>>
        {
            ["brave"] = new()
            {
                ["kind"] = "research",
                ["tag"] = tag,
                ["raw_text"] = rawPayload

            }
        };

        return metadata;
    }

    private static Dictionary<string, Dictionary<string, object>?> CreateResearchToolProviderMetadata(
        string toolCallId,
        string tag,
        JsonElement? payload,
        string rawPayload)
    {
        var metadata = CreateResearchProviderMetadata(tag, payload, rawPayload);
    
        return new Dictionary<string, Dictionary<string, object>?>
        {
        };
    }

    private static bool TryParseJsonPayload(string payload, out JsonElement element)
    {
        element = default;

        try
        {
            using var document = JsonDocument.Parse(payload);
            element = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ExtractJsonScalarText(JsonElement element)
        => element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    private static IEnumerable<string> EnumerateStringValues(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                continue;

            var text = item.GetString();
            if (!string.IsNullOrWhiteSpace(text))
                yield return text;
        }
    }

    private sealed record BraveResearchTextSegment(string? Tag, string Payload, string Id, Dictionary<string, object>? ProviderMetadata);

    private sealed class BraveResearchToolCall
    {
        public required string Id { get; init; }
        public required object Input { get; init; }
        public required string InputJson { get; init; }
    }

    private sealed class BraveResearchStreamState
    {
        public StringBuilder Buffer { get; } = new();
        public Queue<BraveResearchToolCall> PendingToolCalls { get; } = [];
        public BraveResearchToolCall? LastToolCall { get; set; }
        public int NextToolCallIndex { get; set; }

        public BraveResearchToolCall CreateToolCall(string rawPayload, JsonElement? payload)
        {
            object input = payload is JsonElement element ? element.Clone() : new Dictionary<string, object?> { ["raw"] = rawPayload };
            var inputJson = payload is JsonElement ? rawPayload : JsonSerializer.Serialize(input, JsonSerializerOptions.Web);
            var toolCall = new BraveResearchToolCall
            {
                Id = $"brave-research-web-search-{++NextToolCallIndex}",
                Input = input,
                InputJson = inputJson
            };

            PendingToolCalls.Enqueue(toolCall);
            LastToolCall = toolCall;
            return toolCall;
        }

        public BraveResearchToolCall GetOrCreateToolCallForOutput(JsonElement thinkingPayload)
        {
            if (PendingToolCalls.TryDequeue(out var pending))
            {
                LastToolCall = pending;
                return pending;
            }

            if (LastToolCall is not null)
                return LastToolCall;

            var query = GetString(thinkingPayload, "query") ?? "research";
            return CreateToolCall(
                JsonSerializer.Serialize(new { queries = new[] { query } }, JsonSerializerOptions.Web),
                JsonSerializer.SerializeToElement(new { queries = new[] { query } }, JsonSerializerOptions.Web));
        }
    }

    private static class BraveResearchTaggedTextParser
    {
        private static readonly (string Tag, string StartTag, string EndTag)[] Tags =
        [
            ("queries", ResearchQueriesStartTag, ResearchQueriesEndTag),
            ("analyzing", ResearchAnalyzingStartTag, ResearchAnalyzingEndTag),
            ("thinking", ResearchThinkingStartTag, ResearchThinkingEndTag),
            ("blindspots", ResearchBlindspotsStartTag, ResearchBlindspotsEndTag),
            ("progress", ResearchProgressStartTag, ResearchProgressEndTag),
            ("answer", ResearchAnswerStartTag, ResearchAnswerEndTag),
            ("usage", ResearchUsageStartTag, ResearchUsageEndTag)
        ];

        public static IEnumerable<BraveResearchTextSegment> Append(
            BraveResearchStreamState state,
            string text,
            string id,
            Dictionary<string, object>? providerMetadata)
        {
            if (!string.IsNullOrEmpty(text))
                state.Buffer.Append(text);

            return ParseAvailable(state, id, providerMetadata, flush: false);
        }

        public static IEnumerable<BraveResearchTextSegment> Flush(
            BraveResearchStreamState state,
            string id,
            Dictionary<string, object>? providerMetadata)
            => ParseAvailable(state, id, providerMetadata, flush: true);

        private static IEnumerable<BraveResearchTextSegment> ParseAvailable(
            BraveResearchStreamState state,
            string id,
            Dictionary<string, object>? providerMetadata,
            bool flush)
        {
            while (state.Buffer.Length > 0)
            {
                var current = state.Buffer.ToString();
                var match = FindNextStartTag(current);

                if (match is null)
                {
                    if (!flush && TryGetPotentialPartialResearchTagStart(current, out var keepStart))
                    {
                        if (keepStart > 0)
                        {
                            var prefix = current[..keepStart];
                            state.Buffer.Remove(0, keepStart);
                            yield return new BraveResearchTextSegment(null, prefix, id, providerMetadata);
                        }

                        yield break;
                    }

                    state.Buffer.Clear();
                    yield return new BraveResearchTextSegment(null, current, id, providerMetadata);
                    yield break;
                }

                var (tag, startTag, endTag, startIndex) = match.Value;
                if (startIndex > 0)
                {
                    var prefix = current[..startIndex];
                    state.Buffer.Remove(0, startIndex);
                    yield return new BraveResearchTextSegment(null, prefix, id, providerMetadata);
                    continue;
                }

                var payloadStart = startTag.Length;
                var endIndex = current.IndexOf(endTag, payloadStart, StringComparison.OrdinalIgnoreCase);
                if (endIndex < 0)
                {
                    if (flush)
                    {
                        state.Buffer.Clear();
                        yield return new BraveResearchTextSegment(null, current, id, providerMetadata);
                    }

                    yield break;
                }

                var payload = current[payloadStart..endIndex];
                var removeLength = endIndex + endTag.Length;
                state.Buffer.Remove(0, removeLength);
                yield return new BraveResearchTextSegment(tag, payload, id, providerMetadata);
            }
        }

        private static (string Tag, string StartTag, string EndTag, int StartIndex)? FindNextStartTag(string text)
        {
            (string Tag, string StartTag, string EndTag, int StartIndex)? best = null;

            foreach (var tag in Tags)
            {
                var index = text.IndexOf(tag.StartTag, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                    continue;

                if (best is null || index < best.Value.StartIndex)
                    best = (tag.Tag, tag.StartTag, tag.EndTag, index);
            }

            return best;
        }

        private static bool TryGetPotentialPartialResearchTagStart(string text, out int keepStart)
        {
            keepStart = -1;
            var lastOpen = text.LastIndexOf('<');
            if (lastOpen < 0)
                return false;

            var suffix = text[lastOpen..];
            if (Tags.Any(tag => tag.StartTag.StartsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            {
                keepStart = lastOpen;
                return true;
            }

            return false;
        }
    }

    private static bool TryConvertEnumControlTextDelta(UIMessagePart uiPart)
        => uiPart is TextDeltaUIMessageStreamPart textPart
            && !string.IsNullOrWhiteSpace(textPart.Delta)
            && (IsCompleteTaggedBlock(textPart.Delta, EnumStartTag, EnumStartEndTag)
                || IsCompleteTaggedBlock(textPart.Delta, EnumEndTag, EnumEndEndTag));

    private async IAsyncEnumerable<UIMessagePart> ConvertEnumEntityToUIPartsAsync(
        TextDeltaUIMessageStreamPart textPart,
        JsonElement entity,
        HashSet<string> emittedSourceIds,
        HashSet<string> emittedImageUrls,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var originalTokens = GetString(entity, "original_tokens");
        if (!string.IsNullOrEmpty(originalTokens))
        {
            yield return new TextDeltaUIMessageStreamPart
            {
                Id = textPart.Id,
                Delta = originalTokens,
                ProviderMetadata = textPart.ProviderMetadata
            };
        }

        if (TryCreateEntitySourcePart(entity, out var entitySourcePart)
            && emittedSourceIds.Add(entitySourcePart.SourceId))
        {
            yield return entitySourcePart;
        }

        foreach (var citationSourcePart in CreateEntityCitationSourceParts(entity))
        {
            if (emittedSourceIds.Add(citationSourcePart.SourceId))
                yield return citationSourcePart;
        }

        foreach (var imageUrl in EnumerateStringArray(entity, "images"))
        {
            if (!emittedImageUrls.Add(imageUrl))
                continue;

            var imagePart = await DownloadEntityImageFilePartAsync(entity, imageUrl, cancellationToken);
            if (imagePart is not null)
                yield return imagePart;
        }
    }

    private static bool TryConvertCitationTextDelta(UIMessagePart uiPart, out SourceUIPart sourcePart)
    {
        sourcePart = default!;

        if (uiPart is not TextDeltaUIMessageStreamPart textPart
            || string.IsNullOrWhiteSpace(textPart.Delta))
        {
            return false;
        }

        return TryParseCitationBlock(textPart.Delta, out sourcePart);
    }

    private static bool TryParseEnumItemBlock(string text, out JsonElement entity)
    {
        entity = default;

        if (!TryExtractCompleteTaggedPayload(text, EnumItemStartTag, EnumItemEndTag, out var payload))
            return false;

        if (string.IsNullOrWhiteSpace(payload))
            return false;

        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            entity = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseCitationBlock(string text, out SourceUIPart sourcePart)
    {
        sourcePart = default!;

        if (!TryExtractCompleteTaggedPayload(text, CitationStartTag, CitationEndTag, out var payload))
            return false;

        if (string.IsNullOrWhiteSpace(payload))
            return false;

        JsonElement citation;
        try
        {
            using var document = JsonDocument.Parse(payload);
            citation = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return false;
        }

        if (citation.ValueKind != JsonValueKind.Object)
            return false;

        var url = GetString(citation, "url");
        if (string.IsNullOrWhiteSpace(url))
            return false;

        var number = GetInt(citation, "number");
        var snippet = GetString(citation, "snippet");
        var title = GetString(citation, "title")
            ?? NormalizeTitle(snippet)
            ?? url;

        var metadata = new Dictionary<string, object>
        {
            ["raw"] = citation
        };

        AddIfNotNull(metadata, "number", number);
        AddIfNotNull(metadata, "start_index", GetInt(citation, "start_index"));
        AddIfNotNull(metadata, "end_index", GetInt(citation, "end_index"));
        AddIfNotNull(metadata, "favicon", GetString(citation, "favicon"));
        AddIfNotNull(metadata, "snippet", snippet);

        sourcePart = new SourceUIPart
        {
            SourceId = number is not null ? $"brave-citation-{number}" : url,
            Url = url,
            Title = title,
            ProviderMetadata = new Dictionary<string, Dictionary<string, object>>
            {
                ["brave"] = metadata
            }
        };

        return true;
    }

    private static bool TryCreateEntitySourcePart(JsonElement entity, out SourceUIPart sourcePart)
    {
        sourcePart = default!;

        var url = GetString(entity, "href");
        if (string.IsNullOrWhiteSpace(url))
            return false;

        var uuid = GetString(entity, "uuid");
        var title = GetString(entity, "name") ?? url;
        var metadata = CreateEntityMetadata(entity);

        sourcePart = new SourceUIPart
        {
            SourceId = !string.IsNullOrWhiteSpace(uuid) ? $"brave-entity-{uuid}" : url,
            Url = url,
            Title = title,
            ProviderMetadata = new Dictionary<string, Dictionary<string, object>>
            {
                ["brave"] = metadata
            }
        };

        return true;
    }

    private static IEnumerable<SourceUIPart> CreateEntityCitationSourceParts(JsonElement entity)
    {
        if (!TryGetProperty(entity, "citations", out var citations) || citations.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var citation in citations.EnumerateArray())
        {
            if (citation.ValueKind != JsonValueKind.Object)
                continue;

            var citationText = $"{CitationStartTag}{citation.GetRawText()}{CitationEndTag}";
            if (TryParseCitationBlock(citationText, out var sourcePart))
                yield return sourcePart;
        }
    }

    private async Task<FileUIPart?> DownloadEntityImageFilePartAsync(
        JsonElement entity,
        string imageUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, imageUrl);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length == 0)
                return null;

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (string.IsNullOrWhiteSpace(mediaType) || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                mediaType = GuessImageMediaTypeFromUrl(imageUrl) ?? MediaTypeNames.Image.Png;

            var metadata = CreateEntityImageMetadata(entity, imageUrl, mediaType);
            var filename = GetDownloadFileName(response, imageUrl, mediaType);

            return new FileUIPart
            {
                MediaType = mediaType,
                Url = Convert.ToBase64String(bytes),
                ProviderMetadata = new Dictionary<string, Dictionary<string, object>?>
                {
                    [GetIdentifier()] = metadata
                }
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, object> CreateEntityMetadata(JsonElement entity)
    {
        var metadata = new Dictionary<string, object>
        {
            ["raw"] = entity,
            ["kind"] = "entity"
        };

        AddIfNotNull(metadata, "uuid", GetString(entity, "uuid"));
        AddIfNotNull(metadata, "name", GetString(entity, "name"));
        AddIfNotNull(metadata, "href", GetString(entity, "href"));
        AddIfNotNull(metadata, "extra_text", GetString(entity, "extra_text"));
        AddIfNotNull(metadata, "original_tokens", GetString(entity, "original_tokens"));
        AddIfNotNull(metadata, "instance_of", GetRawClone(entity, "instance_of"));
        AddIfNotNull(metadata, "images", GetRawClone(entity, "images"));
        AddIfNotNull(metadata, "citations", GetRawClone(entity, "citations"));

        return metadata;
    }

    private static Dictionary<string, object> CreateEntityImageMetadata(JsonElement entity, string imageUrl, string mediaType)
    {
        var metadata = CreateEntityMetadata(entity);
        metadata["kind"] = "entity_image";
        metadata["origin_url"] = imageUrl;
        metadata["media_type"] = mediaType;
        return metadata;
    }

    private static bool TryExtractCompleteTaggedPayload(string text, string startTag, string endTag, out string payload)
    {
        payload = string.Empty;

        var start = text.IndexOf(startTag, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return false;

        var payloadStart = start + startTag.Length;
        var end = text.IndexOf(endTag, payloadStart, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
            return false;

        var before = text[..start];
        var after = text[(end + endTag.Length)..];
        if (!string.IsNullOrWhiteSpace(before) || !string.IsNullOrWhiteSpace(after))
            return false;

        payload = text[payloadStart..end];
        return true;
    }

    private static bool IsCompleteTaggedBlock(string text, string startTag, string endTag)
        => TryExtractCompleteTaggedPayload(text, startTag, endTag, out _);

    private static IEnumerable<string> EnumerateStringArray(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                continue;

            var text = item.GetString();
            if (!string.IsNullOrWhiteSpace(text))
                yield return text;
        }
    }

    private static JsonElement? GetRawClone(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
            return null;

        return value.Clone();
    }

    private static string? GetDownloadFileName(HttpResponseMessage response, string url, string mediaType)
    {
        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName;

        if (!string.IsNullOrWhiteSpace(fileName))
            return fileName.Trim('"');

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        fileName = Path.GetFileName(uri.LocalPath);
        if (!string.IsNullOrWhiteSpace(fileName))
            return fileName;

        return GuessImageFileExtension(mediaType) is { } extension
            ? $"brave-image{extension}"
            : null;
    }

    private static string? GuessImageMediaTypeFromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        return Path.GetExtension(uri.LocalPath).ToLowerInvariant() switch
        {
            ".png" => MediaTypeNames.Image.Png,
            ".jpg" or ".jpeg" => MediaTypeNames.Image.Jpeg,
            ".gif" => MediaTypeNames.Image.Gif,
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".svg" => "image/svg+xml",
            _ => null
        };
    }

    private static string? GuessImageFileExtension(string mediaType)
        => mediaType.ToLowerInvariant() switch
        {
            MediaTypeNames.Image.Png => ".png",
            MediaTypeNames.Image.Jpeg => ".jpg",
            "image/jpg" => ".jpg",
            MediaTypeNames.Image.Gif => ".gif",
            "image/webp" => ".webp",
            "image/bmp" => ".bmp",
            "image/svg+xml" => ".svg",
            _ => null
        };

    private static string? NormalizeTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 120 ? normalized : normalized[..120];
    }

    private static void AddIfNotNull(Dictionary<string, object> metadata, string key, object? value)
    {
        if (value is not null)
            metadata[key] = value;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            return null;

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static int? GetInt(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var parsed) => parsed,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }
}
