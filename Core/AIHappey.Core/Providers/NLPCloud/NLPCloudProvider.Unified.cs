using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.NLPCloud;

public partial class NLPCloudProvider
{
    private static readonly JsonSerializerOptions NativeJson = JsonSerializerOptions.Web;

    private sealed record NativeRequest(
        string Endpoint, string ResultField, Dictionary<string, object?> Payload, bool Streaming, List<string> UnsupportedOptions);

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var plan = BuildNativeRequest(request, stream: false);
        using var response = await SendNativeAsync(plan, cancellationToken);
        var root = await ReadNativeResponseAsync(response, cancellationToken);
        return MapNativeResponse(request, plan, root);
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var plan = BuildNativeRequest(request, stream: true);
        var id = request.Id ?? Guid.NewGuid().ToString("N");
        using var response = await SendNativeAsync(plan, cancellationToken);
        AIResponse result;

        // Task endpoints are JSON-only. Some chatbot deployments also ignore stream.
        if (!plan.Streaming || response.Content.Headers.ContentType?.MediaType == "application/json")
        {
            var root = await ReadNativeResponseAsync(response, cancellationToken);
            result = MapNativeResponse(request, plan, root);
            foreach (var content in result.Output?.Items?.SelectMany(item => item.Content ?? []) ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (content is AITextContentPart text)
                {
                    yield return NativeEvent(id, "text-start", new AITextStartEventData(), result.Metadata);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (text.Text.Length > 0)
                        yield return NativeEvent(id, "text-delta", new AITextDeltaEventData { Delta = text.Text }, result.Metadata);
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return NativeEvent(id, "text-end", new AITextEndEventData
                    {
                        ProviderMetadata = TextProviderMetadata(result.Metadata)
                    }, result.Metadata);
                }
                else if (content is AIFileContentPart file)
                    yield return NativeEvent(id, "file", new AIFileEventData
                    {
                        Url = file.Data!.ToString()!, MediaType = file.MediaType!, Filename = file.Filename,
                        ProviderMetadata = new() { [GetIdentifier()] = new() { ["response"] = root } }
                    }, result.Metadata);
            }
        }
        else
        {
            var text = new StringBuilder();
            var metadata = NativeMetadata(plan);
            yield return NativeEvent(id, "text-start", new AITextStartEventData(), metadata);
            await foreach (var chunk in ReadChatbotStreamAsync(response, cancellationToken))
            {
                text.Append(chunk);
                yield return NativeEvent(id, "text-delta", new AITextDeltaEventData { Delta = chunk }, metadata);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (text.Length == 0)
                throw new InvalidOperationException("Empty NLPCloud chatbot stream.");
            metadata = NativeMetadata(plan, text.ToString(), "native");
            yield return NativeEvent(id, "text-end", new AITextEndEventData
            {
                ProviderMetadata = TextProviderMetadata(metadata)
            }, metadata);
            result = new AIResponse
            {
                ProviderId = GetIdentifier(), Model = request.Model, Status = "completed",
                Output = TextOutput(text.ToString()), Metadata = metadata
            };
        }

        cancellationToken.ThrowIfCancellationRequested();
        var usage = result.NormalizedUsage;
        var now = DateTimeOffset.UtcNow;
        yield return NativeEvent(id, "finish", new AIFinishEventData
        {
            FinishReason = "stop", Model = result.Model, CompletedAt = now.ToUnixTimeSeconds(),
            InputTokens = usage?.InputTokens, OutputTokens = usage?.OutputTokens, TotalTokens = usage?.TotalTokens,
            MessageMetadata = AIFinishMessageMetadata.Create(
                result.Model!, now, usage, usage?.OutputTokens, usage?.InputTokens, usage?.TotalTokens,
                request.Temperature, additionalProperties: result.Metadata)
        }, result.Metadata, result.Output);
    }

    private NativeRequest BuildNativeRequest(AIRequest request, bool stream)
    {
        var toolChoice = request.ToolChoice is null or JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined }
            ? default : JsonSerializer.SerializeToElement(request.ToolChoice, NativeJson);
        var noToolChoice = toolChoice.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            || toolChoice.ValueKind == JsonValueKind.String && toolChoice.GetString() is "none" or "auto"
            || toolChoice.ValueKind == JsonValueKind.Object && toolChoice.TryGetProperty("type", out var choiceType)
                && choiceType.ValueKind == JsonValueKind.String && choiceType.GetString() is "none" or "auto";
        if (request.Tools is { Count: > 0 } || !noToolChoice)
            throw new NotSupportedException("NLPCloud native endpoints do not support tools or tool choice.");
        var format = request.ResponseFormat is null or JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined }
            ? default : JsonSerializer.SerializeToElement(request.ResponseFormat, NativeJson);
        if (format.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            && !(format.ValueKind == JsonValueKind.Object && format.TryGetProperty("type", out var formatType)
                && formatType.ValueKind == JsonValueKind.String && formatType.GetString() == "text"))
            throw new NotSupportedException("NLPCloud native endpoints do not support structured response formats.");

        var model = NormalizeNativeModel(request.Model);
        var options = NativeOptions(request.Metadata);
        var payload = new Dictionary<string, object?>();
        string endpoint;
        string field;
        bool gpu;
        var streaming = false;

        if (model.Equals("whisper/asr", StringComparison.OrdinalIgnoreCase))
        {
            var file = request.Input?.Items?
                .LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))?.Content?
                .OfType<AIFileContentPart>().LastOrDefault()
                ?? throw new ArgumentException("NLPCloud ASR requires an audio or video file in the last user message.");
            if (file.MediaType is { Length: > 0 } mediaType
                && !mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
                && !mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("NLPCloud ASR supports audio and video files only.");
            SetNativeAudio(payload, file.Data);
            endpoint = model;
            field = "text";
            gpu = true;
        }
        else
        {
            var messages = NativeMessages(request);
            var text = BuildSummarizationInput(messages);
            if (model.Equals("speech-t5", StringComparison.OrdinalIgnoreCase))
            {
                payload["text"] = text;
                payload["voice"] = "woman";
                endpoint = $"{model}/speech-synthesis";
                field = "url";
                gpu = true;
            }
            else
            {
                var kind = GetModelKind(model, out var baseModel);
                (var operation, field) = kind switch
                {
                    NLPCloudModelKind.Paraphrasing => ("paraphrasing", "paraphrased_text"),
                    NLPCloudModelKind.Summarization => ("summarization", "summary_text"),
                    NLPCloudModelKind.IntentClassification => ("intent-classification", "intent"),
                    NLPCloudModelKind.CodeGeneration => ("code-generation", "generated_code"),
                    NLPCloudModelKind.GrammarSpellingCorrection => ("gs-correction", "correction"),
                    NLPCloudModelKind.KeywordsKeyphrasesExtraction => ("kw-kp-extraction", "keywords_and_keyphrases"),
                    NLPCloudModelKind.Translation => ("translation", "translation_text"),
                    _ => ("chatbot", "response")
                };
                endpoint = $"{baseModel}/{operation}";
                gpu = kind != NLPCloudModelKind.Translation;
                if (kind == NLPCloudModelKind.Chatbot)
                {
                    var chatbot = BuildChatbotRequest(messages);
                    payload["input"] = chatbot.Input;
                    payload["context"] = chatbot.Context;
                    payload["history"] = chatbot.History;
                    streaming = stream;
                }
                else
                    payload[kind == NLPCloudModelKind.CodeGeneration ? "instruction" : "text"] = text;

                if (kind == NLPCloudModelKind.Translation)
                {
                    payload["target"] = GetTranslationTargetLanguageFromModel(model);
                    payload["source"] = string.Empty;
                }
            }
        }

        var inputFields = payload.Where(pair => pair.Key is "input" or "text" or "instruction" or "target" or "url" or "encoded_file")
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var pair in options)
        {
            if (pair.Key is not ("gpu" or "lang" or "stream"))
                payload[pair.Key] = pair.Value;
        }
        foreach (var pair in inputFields)
            payload[pair.Key] = pair.Value;
        if (model.Equals("whisper/asr", StringComparison.OrdinalIgnoreCase))
            payload.Remove(inputFields.ContainsKey("url") ? "encoded_file" : "url");
        if (field == "response")
            payload["stream"] = streaming;
        if (options.TryGetValue("gpu", out var gpuOption))
            gpu = gpuOption.Deserialize<bool>(NativeJson);
        var lang = options.TryGetValue("lang", out var langOption) ? langOption.Deserialize<string>(NativeJson) : null;
        if (lang is "en" or "eng_Latn")
            lang = null;
        endpoint = (gpu ? "gpu/" : "") + (string.IsNullOrWhiteSpace(lang) ? "" : Uri.EscapeDataString(lang) + "/") + endpoint;
        var unsupported = new List<string>();
        if (request.Temperature is not null) unsupported.Add("temperature");
        if (request.TopP is not null) unsupported.Add("top_p");
        if (request.MaxOutputTokens is not null) unsupported.Add("max_output_tokens");
        if (request.MaxToolCalls is not null) unsupported.Add("max_tool_calls");
        if (request.ParallelToolCalls is not null) unsupported.Add("parallel_tool_calls");
        return new NativeRequest(endpoint, field, payload, streaming, unsupported);
    }

    private static List<(string Role, string? Content)> NativeMessages(AIRequest request)
    {
        var messages = new List<(string Role, string? Content)>();
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            messages.Add(("system", request.Instructions));
        foreach (var item in request.Input?.Items ?? [])
        {
            if ((item.Content ?? []).Any(part => part is not AITextContentPart))
                throw new NotSupportedException("NLPCloud language endpoints support text input only.");
            messages.Add((item.Role ?? "user", string.Join("\n", (item.Content ?? []).OfType<AITextContentPart>().Select(part => part.Text))));
        }
        if (request.Input?.Items is not { Count: > 0 } && !string.IsNullOrWhiteSpace(request.Input?.Text))
            messages.Add(("user", request.Input.Text));
        return messages;
    }

    private static string NormalizeNativeModel(string? model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        model = model.Trim();
        const string prefix = "nlpcloud/";
        return model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? model[prefix.Length..] : model;
    }

    private Dictionary<string, JsonElement> NativeOptions(Dictionary<string, object?>? metadata)
    {
        if (metadata is null)
            return [];
        var root = JsonSerializer.SerializeToElement(metadata, NativeJson);
        if (!root.TryGetProperty(GetIdentifier(), out var native)
            && !(root.TryGetProperty("providerOptions", out var wrapper)
                && wrapper.ValueKind == JsonValueKind.Object && wrapper.TryGetProperty(GetIdentifier(), out native)))
            return [];
        if (native.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return [];
        if (native.ValueKind != JsonValueKind.Object)
            throw new JsonException("NLPCloud provider metadata must be a JSON object.");
        return native.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
    }

    private static void SetNativeAudio(Dictionary<string, object?> payload, object? audio)
    {
        var value = audio switch
        {
            byte[] bytes => Convert.ToBase64String(bytes),
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
            string text => text,
            Uri uri => uri.OriginalString,
            _ => throw new ArgumentException("NLPCloud ASR requires a URL, base64 string or audio bytes.")
        };
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Audio payload is empty.");
        if (Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Scheme is "http" or "https")
            payload["url"] = value;
        else
        {
            if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var marker = value.IndexOf("base64,", StringComparison.OrdinalIgnoreCase);
                if (marker < 0)
                    throw new ArgumentException("NLPCloud inline audio must be base64 encoded.");
                value = value[(marker + "base64,".Length)..];
            }
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Audio base64 payload is empty.");
            payload["encoded_file"] = value;
        }
    }

    private async Task<HttpResponseMessage> SendNativeAsync(NativeRequest plan, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var message = new HttpRequestMessage(HttpMethod.Post, plan.Endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(plan.Payload, NativeJson), Encoding.UTF8, "application/json")
        };
        var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            using (response)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException($"NLPCloud API error ({(int)response.StatusCode}): {error}", null, response.StatusCode);
            }
        }
        return response;
    }

    private static async Task<JsonElement> ReadNativeResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("NLPCloud response must be a JSON object.");
        if (root.TryGetProperty("error", out var error))
            throw new InvalidOperationException($"NLPCloud API error: {error.GetRawText()}");
        return root.Clone();
    }

    private AIResponse MapNativeResponse(AIRequest request, NativeRequest plan, JsonElement root)
    {
        if (!root.TryGetProperty(plan.ResultField, out var value))
            throw new InvalidOperationException($"NLPCloud response missing {plan.ResultField}: {root.GetRawText()}");
        AIOutput output;
        if (plan.ResultField == "keywords_and_keyphrases")
        {
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0
                || value.EnumerateArray().Any(keyword => keyword.ValueKind != JsonValueKind.String))
                throw new InvalidOperationException("Invalid NLPCloud keywords and keyphrases response.");
            output = TextOutput(string.Join("\n", value.EnumerateArray()
                .Select(keyword => keyword.GetString()).Where(keyword => !string.IsNullOrWhiteSpace(keyword))
                .Select(keyword => "- " + keyword)));
        }
        else
        {
            if (value.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException($"NLPCloud {plan.ResultField} must be a string.");
            var text = value.GetString()!;
            if (plan.ResultField is not ("response" or "text") && string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException($"Empty NLPCloud {plan.ResultField} response.");
            if (plan.ResultField == "url")
            {
                output = new AIOutput { Items = [new AIOutputItem
                {
                    Role = "assistant", Content = [new AIFileContentPart { Type = "file", Data = text, MediaType = "audio/wav" }]
                }] };
            }
            else
            {
                if (plan.ResultField == "generated_code" && !text.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    var fence = new string('`', Math.Max(3, Regex.Matches(text, "`+").Select(match => match.Length).DefaultIfEmpty(0).Max() + 1));
                    text = $"{fence}\n{text}\n{fence}";
                }
                output = TextOutput(text);
            }
        }

        var metadata = NativeMetadata(plan, root, "buffered");
        // Reuse the existing usage mapper without routing execution through chat completions.
        var usage = AIHappey.ChatCompletions.Mapping.ChatCompletionsUnifiedMapper.ToUnifiedResponse(root, GetIdentifier()).Usage;
        return new AIResponse
        {
            ProviderId = GetIdentifier(), Model = request.Model, Status = "completed",
            Output = output, Usage = usage, Metadata = metadata
        };
    }

    private Dictionary<string, object?> NativeMetadata(NativeRequest plan, object? response = null, string? streaming = null)
        => new() { [GetIdentifier()] = new Dictionary<string, object?>
        {
            ["request"] = JsonSerializer.SerializeToElement(plan.Payload, NativeJson),
            ["response"] = response,
            ["endpoint"] = plan.Endpoint,
            ["streaming"] = streaming,
            ["unsupported_options"] = plan.UnsupportedOptions
        } };

    private Dictionary<string, object> TextProviderMetadata(Dictionary<string, object?>? metadata)
        => new() { [GetIdentifier()] = metadata![GetIdentifier()]! };

    private AIStreamEvent NativeEvent(string id, string type, object data, Dictionary<string, object?>? metadata, AIOutput? output = null)
        => new()
        {
            ProviderId = GetIdentifier(), Metadata = metadata,
            Event = new AIEventEnvelope
            {
                Id = id, Type = type, Timestamp = DateTimeOffset.UtcNow, Data = data, Metadata = metadata, Output = output
            }
        };

    private static AIOutput TextOutput(string text)
        => new() { Items = [new AIOutputItem
        {
            Role = "assistant",
            Content = [new AITextContentPart { Type = "text", Text = text }],
            Metadata = new() { ["chatcompletions.choice.finish_reason"] = "stop" }
        }] };

    private static async IAsyncEnumerable<string> ReadChatbotStreamAsync(
        HttpResponseMessage response, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    continue;
                var text = line[5..];
                if (text.StartsWith(' '))
                    text = text[1..];
                text = text.Replace("\0", "");
                var done = text.IndexOf("[DONE]", StringComparison.OrdinalIgnoreCase);
                if (done >= 0)
                {
                    if (done > 0)
                        yield return text[..done];
                    yield break;
                }
                if (text.Length > 0)
                    yield return text;
            }
            cancellationToken.ThrowIfCancellationRequested();
            yield break;
        }
        // NLPCloud's legacy stream is plain text with NUL separators, not JSON SSE.
        var buffer = new char[1024];
        var pending = new StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            pending.Append(new string(buffer, 0, count).Replace("\0", ""));
            var text = pending.ToString();
            var done = text.IndexOf("[DONE]", StringComparison.OrdinalIgnoreCase);
            if (done >= 0)
            {
                if (done > 0)
                    yield return text[..done];
                yield break;
            }
            // Keep a possible terminator prefix across transport reads.
            var keep = 0;
            for (var length = 1; length < "[DONE]".Length && length <= text.Length; length++)
                if ("[DONE]".StartsWith(text[^length..], StringComparison.OrdinalIgnoreCase))
                    keep = length;
            if (text.Length > keep)
            {
                yield return text[..(text.Length - keep)];
                pending.Clear();
                if (keep > 0)
                    pending.Append(text[^keep..]);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (pending.Length > 0)
            yield return pending.ToString();
    }
}
