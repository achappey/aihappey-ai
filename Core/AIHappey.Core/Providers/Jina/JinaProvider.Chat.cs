using System.Text.Json;
using System.Text;
using System.Net.Mime;
using System.Text.Json.Nodes;
using AIHappey.Common.Model;
using System.Runtime.CompilerServices;
using System.Net.Http.Headers;
using AIHappey.Core.AI;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers;

namespace AIHappey.Core.Providers.Jina;

public partial class JinaProvider : IModelProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
          ChatRequest chatRequest,
          [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        var metadata = chatRequest.GetProviderMetadata<JinaProviderMetadata>(GetIdentifier());
        var payload = new
        {
            model = chatRequest.Model,
            stream = true,
            team_size = metadata?.TeamSize,
            no_direct_answer = metadata?.NoDirectAnswer,
            search_provider = metadata?.SearchProvider,
            language_code = metadata?.LanguageCode,
            bad_hostnames = metadata?.BadHostnames,
            boost_hostnames = metadata?.BoostHostnames,
            only_hostnames = metadata?.OnlyHostnames,
            max_returned_urls = metadata?.MaxReturnedUrls,
            reasoning_effort = metadata?.ReasoningEffort ?? "medium",
            messages = chatRequest.Messages.ToJinaMessages()
        };

        // --- 2️⃣ Send SSE request ---
        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            yield return $"Jina stream error: {(string.IsNullOrWhiteSpace(err) ? resp.ReasonPhrase : err)}"
                .ToErrorUIPart();
            yield break;
        }

        // --- 3️⃣ Stream tokens ---
        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        string? streamId = null;
        bool textStarted = false;
        string modelId = chatRequest.Model;
        string? reasoningId = null;
        bool reasoningStarted = false;

        int? inputTokens = null, outputTokens = null, totalTokens = null, reasoningTokens = null;
        var activeReasoning = new HashSet<string>();

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync();
            if (line is null) break;
            if (line.Length == 0) continue;
            if (line.StartsWith(":")) continue;
            if (!line.StartsWith("data: ")) continue;

            var jsonData = line["data: ".Length..].Trim();
            if (string.IsNullOrEmpty(jsonData) || jsonData == "[DONE]")
            {
                break;
            }

            var node = JsonNode.Parse(jsonData);
            var choices = node?["choices"]?.AsArray();
            if (choices is null || choices.Count == 0) continue;

            var choice = choices[0];
            var delta = choice?["delta"];
            var finishReason = choice?["finish_reason"]?.GetValue<string>();
            var deltaType = delta?["type"]?.GetValue<string>() ?? "text";
            var content = delta?["content"]?.GetValue<string>();

            if (string.IsNullOrEmpty(content) && string.IsNullOrEmpty(finishReason))
                continue;

            streamId ??= node?["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("n");

            // 🧠 Reasoning delta (type = "think")
            if (deltaType == "think" && !string.IsNullOrEmpty(content))
            {
                if (!reasoningStarted)
                {
                    reasoningId = Guid.NewGuid().ToString("n");
                    reasoningStarted = true;
                    yield return new ReasoningStartUIPart { Id = reasoningId };
                }

                yield return new ReasoningDeltaUIPart
                {
                    Id = reasoningId!,
                    Delta = content
                };
            }

            // 💬 Normal text delta
            else if (!string.IsNullOrEmpty(content))
            {
                if (!textStarted)
                {
                    yield return streamId.ToTextStartUIMessageStreamPart();
                    textStarted = true;
                }

                yield return new TextDeltaUIMessageStreamPart
                {
                    Id = streamId,
                    Delta = content
                };
            }

            // 🧾 Handle finish signals
            if (!string.IsNullOrEmpty(finishReason))
            {
                // when Jina sends </think> or thinking_end
                if (finishReason == "thinking_end" && reasoningStarted && reasoningId is not null)
                {
                    yield return new ReasoningEndUIPart { Id = reasoningId };
                    reasoningStarted = false;
                    reasoningId = null;
                }

                // when generation stops → we’ll close later anyway
                if (finishReason == "stop")
                {
                    // 🧮 Read token usage if available
                    var usage = node?["usage"];
                    if (usage != null)
                    {
                        inputTokens = usage["prompt_tokens"]?.GetValue<int?>();
                        outputTokens = usage["completion_tokens"]?.GetValue<int?>();
                        totalTokens = usage["total_tokens"]?.GetValue<int?>();
                    }
                }

                var annotations = choices[0]?["delta"]?["annotations"]?.AsArray();
                if (annotations is not null)
                {
                    foreach (var ann in annotations)
                    {
                        if (ann?["type"]?.GetValue<string>() != "url_citation")
                            continue;

                        var c = ann?["url_citation"];
                        if (c is null) continue;

                        var url = c["url"]?.GetValue<string>();
                        if (string.IsNullOrWhiteSpace(url)) continue;

                        yield return new SourceUIPart
                        {
                            Url = url!,
                            SourceId = url!,
                            Title = c["title"]?.GetValue<string>(),
                        };
                    }
                }
            }
        }

        // --- 5️⃣ Finalize streams ---
        if (reasoningStarted && reasoningId is not null)
            yield return new ReasoningEndUIPart { Id = reasoningId };

        if (textStarted && streamId is not null)
            yield return new TextEndUIMessageStreamPart { Id = streamId };

        // --- 6️⃣ Send final finish part ---
        yield return "stop".ToFinishUIPart(
            modelId,
            outputTokens ?? 0,
            inputTokens ?? 0,
            totalTokens ?? (inputTokens + outputTokens) ?? 0,
            chatRequest.Temperature,
            reasoningTokens);
    }
}