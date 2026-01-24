using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Common.Model;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Responses;

namespace AIHappey.Core.Providers.NLPCloud;

public partial class NLPCloudProvider
{
    private static string? ExtractText(JsonElement content)
        => ChatMessageContentExtensions.ToText(content);

    private static string? ExtractText(ResponseMessageContent content)
    {
        if (content.IsText)
            return content.Text;

        if (!content.IsParts || content.Parts is null)
            return null;

        var sb = new StringBuilder();
        foreach (var part in content.Parts.OfType<InputTextPart>())
        {
            if (!string.IsNullOrWhiteSpace(part.Text))
                sb.Append(part.Text);
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    private sealed record NLPCloudHistoryItem(string Input, string Response);

    private sealed record NLPCloudChatbotRequest(
        string Input,
        string? Context,
        IReadOnlyList<NLPCloudHistoryItem>? History,
        bool? Stream);

    private sealed record NLPCloudChatbotResponse(
        string Response,
        IReadOnlyList<NLPCloudHistoryItem>? History);

    private static NLPCloudChatbotRequest BuildChatbotRequest(
        string model,
        IReadOnlyList<(string Role, string? Content)> messages,
        bool? stream,
        string? contextOverride = null,
        IReadOnlyList<NLPCloudHistoryItem>? historyOverride = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(model);
        if (messages.Count == 0)
            throw new ArgumentException("No messages provided.", nameof(messages));

        var lastUser = messages.LastOrDefault(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(lastUser.Content))
            throw new ArgumentException("No user input provided.", nameof(messages));

        var context = contextOverride;
        if (string.IsNullOrWhiteSpace(context))
        {
            var contextParts = messages
                .Where(m => string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(m.Role, "developer", StringComparison.OrdinalIgnoreCase))
                .Select(m => m.Content)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();

            if (contextParts.Count > 0)
                context = string.Join("\n", contextParts!);
        }

        IReadOnlyList<NLPCloudHistoryItem>? history = historyOverride;
        if (history is null)
        {
            var list = new List<NLPCloudHistoryItem>();
            string? pendingUser = null;

            foreach (var message in messages)
            {
                if (string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
                {
                    pendingUser = message.Content;
                    continue;
                }

                if (string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(pendingUser) && !string.IsNullOrWhiteSpace(message.Content))
                        list.Add(new NLPCloudHistoryItem(pendingUser!, message.Content!));
                    pendingUser = null;
                }
            }

            if (list.Count > 0)
                history = list;
        }

        history ??= [];

        return new NLPCloudChatbotRequest(lastUser.Content!, context, history, stream);
    }

    private async Task<NLPCloudChatbotResponse> SendChatbotAsync(
        string model,
        NLPCloudChatbotRequest payload,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        var modelId = model;
        var relativeUrl = $"gpu/{modelId}/chatbot";

        var json = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);
        using var req = new HttpRequestMessage(HttpMethod.Post, relativeUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException($"NLPCloud API error: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var result = await JsonSerializer.DeserializeAsync<NLPCloudChatbotResponse>(
            stream,
            JsonSerializerOptions.Web,
            cancellationToken).ConfigureAwait(false);

        if (result is null)
            throw new InvalidOperationException("Empty NLPCloud response.");

        return result;
    }

    private async IAsyncEnumerable<string> StreamChatbotAsync(
        string model,
        NLPCloudChatbotRequest payload,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        var modelId = model;
        var relativeUrl = $"gpu/{modelId}/chatbot";

        var json = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);
        using var req = new HttpRequestMessage(HttpMethod.Post, relativeUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException($"NLPCloud API error: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        var buffer = new StringBuilder();
        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) break;
            if (line.Length == 0 || line.StartsWith(":", StringComparison.Ordinal))
                continue;

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                line = line["data:".Length..].Trim();

            if (string.IsNullOrEmpty(line))
                continue;

            if (line is "[DONE]" or "[done]")
                break;

            // 🔥 STRIP NLPCloud null-bytes hier
            line = line.Replace("\u0000", "");

            buffer.Append(line);
            var text = buffer.ToString();
            if (text.EndsWith("[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                var finalText = text[..^"[DONE]".Length];
                if (!string.IsNullOrEmpty(finalText))
                    yield return finalText;
                yield break;
            }

            yield return buffer.ToString();
            buffer.Clear();
        }
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var metadata = chatRequest.GetProviderMetadata<NLPCloudChatProviderMetadata>(GetIdentifier());

        var model = chatRequest.Model;
        var messages = chatRequest.Messages
            .Select(m => (m.Role.ToString(), m.Parts.OfType<TextUIPart>().Select(p => p.Text).DefaultIfEmpty().Aggregate(string.Concat)))
            .Select(m => (m.Item1, string.IsNullOrWhiteSpace(m.Item2) ? null : m.Item2))
            .ToList();

        var kind = GetModelKind(model, out var baseModel);
        switch (kind)
        {
            case NLPCloudModelKind.Paraphrasing:
                {
                    var text = BuildParaphrasingInput(messages);
                    var paraphraseStreamId = Guid.NewGuid().ToString("n");
                    yield return paraphraseStreamId.ToTextStartUIMessageStreamPart();

                    await foreach (var chunk in StreamParaphrasingAsync(baseModel, text, cancellationToken))
                    {
                        if (string.IsNullOrEmpty(chunk))
                            continue;

                        yield return new TextDeltaUIMessageStreamPart { Id = paraphraseStreamId, Delta = chunk };
                    }

                    yield return paraphraseStreamId.ToTextEndUIMessageStreamPart();
                    yield return "stop".ToFinishUIPart(chatRequest.Model, 0, 0, 0, chatRequest.Temperature);
                    yield break;
                }
            case NLPCloudModelKind.Summarization:
                {
                    var text = BuildSummarizationInput(messages);
                    var summaryStreamId = Guid.NewGuid().ToString("n");
                    yield return summaryStreamId.ToTextStartUIMessageStreamPart();

                    await foreach (var chunk in StreamSummarizationAsync(baseModel, text, cancellationToken))
                    {
                        if (string.IsNullOrEmpty(chunk))
                            continue;

                        yield return new TextDeltaUIMessageStreamPart { Id = summaryStreamId, Delta = chunk };
                    }

                    yield return summaryStreamId.ToTextEndUIMessageStreamPart();
                    yield return "stop".ToFinishUIPart(chatRequest.Model, 0, 0, 0, chatRequest.Temperature);
                    yield break;
                }
            case NLPCloudModelKind.IntentClassification:
                {
                    var text = BuildIntentClassificationInput(messages);
                    var intentStreamId = Guid.NewGuid().ToString("n");
                    yield return intentStreamId.ToTextStartUIMessageStreamPart();

                    await foreach (var chunk in StreamIntentClassificationAsync(baseModel, text, cancellationToken))
                    {
                        if (string.IsNullOrEmpty(chunk))
                            continue;

                        yield return new TextDeltaUIMessageStreamPart { Id = intentStreamId, Delta = chunk };
                    }

                    yield return intentStreamId.ToTextEndUIMessageStreamPart();
                    yield return "stop".ToFinishUIPart(chatRequest.Model, 0, 0, 0, chatRequest.Temperature);
                    yield break;
                }
            case NLPCloudModelKind.CodeGeneration:
                {
                    var instruction = BuildCodeGenerationInput(messages);
                    var codeStreamId = Guid.NewGuid().ToString("n");
                    yield return codeStreamId.ToTextStartUIMessageStreamPart();

                    await foreach (var chunk in StreamCodeGenerationAsync(baseModel, instruction, cancellationToken))
                    {
                        if (string.IsNullOrEmpty(chunk))
                            continue;

                        yield return new TextDeltaUIMessageStreamPart { Id = codeStreamId, Delta = chunk };
                    }

                    yield return codeStreamId.ToTextEndUIMessageStreamPart();
                    yield return "stop".ToFinishUIPart(chatRequest.Model, 0, 0, 0, chatRequest.Temperature);
                    yield break;
                }
            case NLPCloudModelKind.GrammarSpellingCorrection:
                {
                    var text = BuildGrammarSpellingCorrectionInput(messages);
                    var correctionStreamId = Guid.NewGuid().ToString("n");
                    yield return correctionStreamId.ToTextStartUIMessageStreamPart();

                    await foreach (var chunk in StreamGrammarSpellingCorrectionAsync(baseModel, text, cancellationToken))
                    {
                        if (string.IsNullOrEmpty(chunk))
                            continue;

                        yield return new TextDeltaUIMessageStreamPart { Id = correctionStreamId, Delta = chunk };
                    }

                    yield return correctionStreamId.ToTextEndUIMessageStreamPart();
                    yield return "stop".ToFinishUIPart(chatRequest.Model, 0, 0, 0, chatRequest.Temperature);
                    yield break;
                }
            case NLPCloudModelKind.KeywordsKeyphrasesExtraction:
                {
                    var text = BuildKeywordsKeyphrasesExtractionInput(messages);
                    var keywordStreamId = Guid.NewGuid().ToString("n");
                    yield return keywordStreamId.ToTextStartUIMessageStreamPart();

                    await foreach (var keyword in StreamKeywordsKeyphrasesExtractionAsync(baseModel, text, cancellationToken))
                    {
                        if (string.IsNullOrEmpty(keyword))
                            continue;

                        yield return new TextDeltaUIMessageStreamPart { Id = keywordStreamId, Delta = keyword };
                    }

                    yield return keywordStreamId.ToTextEndUIMessageStreamPart();
                    yield return "stop".ToFinishUIPart(chatRequest.Model, 0, 0, 0, chatRequest.Temperature);
                    yield break;
                }
            case NLPCloudModelKind.Translation:
                {
                    var text = BuildTranslationInput(messages);
                    var targetLanguage = GetTranslationTargetLanguageFromModel(model);
                    var translateStreamId = Guid.NewGuid().ToString("n");
                    yield return translateStreamId.ToTextStartUIMessageStreamPart();

                    await foreach (var chunk in StreamTranslationAsync(baseModel, text, targetLanguage, cancellationToken))
                    {
                        if (string.IsNullOrEmpty(chunk))
                            continue;

                        yield return new TextDeltaUIMessageStreamPart { Id = translateStreamId, Delta = chunk };
                    }

                    yield return translateStreamId.ToTextEndUIMessageStreamPart();
                    yield return "stop".ToFinishUIPart(chatRequest.Model, 0, 0, 0, chatRequest.Temperature);
                    yield break;
                }
            default:
                {
                    var payload = BuildChatbotRequest(
                        chatRequest.Model,
                        messages,
                        stream: true,
                        contextOverride: metadata?.Context,
                        historyOverride: metadata?.History);

                    var streamId = Guid.NewGuid().ToString("n");
                    yield return streamId.ToTextStartUIMessageStreamPart();

                    await foreach (var chunk in StreamChatbotAsync(chatRequest.Model, payload, cancellationToken))
                    {
                        if (string.IsNullOrEmpty(chunk))
                            continue;

                        yield return new TextDeltaUIMessageStreamPart { Id = streamId, Delta = chunk };
                    }

                    yield return streamId.ToTextEndUIMessageStreamPart();
                    yield return "stop".ToFinishUIPart(chatRequest.Model, 0, 0, 0, chatRequest.Temperature);
                    break;
                }
        }
    }

    private sealed class NLPCloudChatProviderMetadata
    {
        public string? Context { get; set; }
        public List<NLPCloudHistoryItem>? History { get; set; }
    }
}
