using AIHappey.Core.AI;
using AIHappey.Core.Models;
using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using AIHappey.Common.Model;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Core.ModelProviders;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Responses.Extensions;
using System.Text.Json;
using System.Text;
using System.Net.Mime;
using System.Dynamic;
using AIHappey.Core.Providers.xAI;
using AIHappey.Responses;

namespace AIHappey.Core.Providers.Perplexity;

public class PerplexityProvider : IModelProvider
{
    private readonly string BASE_URL = "https://api.perplexity.ai/";

    public string GetIdentifier() => nameof(Perplexity).ToLowerInvariant();

    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    public PerplexityProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri(BASE_URL);
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Perplexity)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var model = chatRequest.GetModel();
        IEnumerable<Models.PerplexityMessage> inputItems = chatRequest.Messages.ToPerplexityMessages();
        var req = chatRequest.ToChatRequest(inputItems, chatRequest.SystemPrompt);

        var result = await _client.ChatCompletion(req, cancellationToken);
        var sources = result?.SearchResults?.DistinctBy(a => a.Url) ?? [];
        var mainText = result?.Choices?.FirstOrDefault()?.Message?.Content ?? string.Empty;
        var finished = result?.Choices?.FirstOrDefault()?.FinishReason ?? string.Empty;

        // Build source references as a string (e.g., as markdown links)
        var sourcesText = string.Join(
            Environment.NewLine,
            sources.Select(a => $"- [{a.Title}]({a.Url})")
        );

        var fullText = string.IsNullOrWhiteSpace(sourcesText)
           ? mainText
          : $"{mainText}{Environment.NewLine}{Environment.NewLine}Sources:{Environment.NewLine}{sourcesText}";

        // Create the content block with all content as text
        ContentBlock contentBlock = fullText.ToTextContentBlock();

        return new CreateMessageResult()
        {
            Model = result?.Model!,
            StopReason = finished,
            Content = [contentBlock],
            Role = ModelContextProtocol.Protocol.Role.Assistant,
            Meta = new System.Text.Json.Nodes.JsonObject()
            {
                ["inputTokens"] = result?.Usage?.PromptTokens,
                ["totalTokens"] = result?.Usage?.TotalTokens
            }
        };
    }

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return await Task.FromResult<IEnumerable<Model>>([]);


        ApplyAuthHeader();

        return await Task.FromResult<List<Model>>([.. PerplexityModels.SonarModels, .. PerplexityModels.AgentModels]);
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        if (PerplexityModels.AgentModels.Any(a => a.Id.EndsWith($"/{chatRequest.Model}")))
        {
            var tools = new List<dynamic>();

            foreach (var tool in chatRequest.Tools ?? [])
            {
                tools.Add(new
                {
                    type = "function",
                    name = tool.Name,
                    description = tool.Description,
                    parameters = tool.InputSchema
                });
            }

            dynamic payload = new ExpandoObject();
            payload.model = chatRequest.Model;
            payload.stream = true;
            payload.temperature = chatRequest.Temperature;
            payload.store = false;
            payload.input = chatRequest.Messages.BuildResponsesInput();
            payload.tools = tools;

            if (chatRequest.ResponseFormat != null)
            {
                payload.text = new
                {
                    format = chatRequest.ResponseFormat
                };
            }

            if (tools.Count > 0)
                payload.tool_choice = "auto";

            var json = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);
            var responseReq = new HttpRequestMessage(HttpMethod.Post, "v1/responses")
            {
                Content = new StringContent(
                    json,
                    Encoding.UTF8,
                    MediaTypeNames.Application.Json)
            };

            // ---------- 2) Send and read SSE ----------
            using var resp = await _client.SendAsync(
                responseReq,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(cancellationToken);
                throw new Exception(string.IsNullOrWhiteSpace(err) ? resp.ReasonPhrase : err);
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);

            await foreach (var part in OpenAIResponsesStreamReader.ReadAsync(
                stream,
                chatRequest,
                chatRequest.Model,
                ct: cancellationToken))
            {
                yield return part;
            }

            yield break;
        }


        var (messages, systemRole) = chatRequest.Messages.ToPerplexityMessages();
        var req = chatRequest.ToChatRequest(messages, systemRole);

        string? currentStreamId = null;

        HashSet<string> sources = [];

        await foreach (var chunk in _client.ChatCompletionStreaming(req, cancellationToken))
        {
            var firstChoice = chunk?.Choices?.FirstOrDefault();
            var content = firstChoice?.Delta?.Content
                ?? firstChoice?.Message?.Content;

            if (!string.IsNullOrEmpty(content))
            {
                if (currentStreamId is null && chunk?.Id is not null)
                {
                    currentStreamId = chunk.Id;

                    yield return currentStreamId.ToTextStartUIMessageStreamPart();
                }

                if (currentStreamId is not null)
                {
                    yield return new TextDeltaUIMessageStreamPart
                    {
                        Id = currentStreamId,
                        Delta = content
                    };
                }
            }

            if (currentStreamId is not null)
            {
                foreach (var searchResult in chunk?.SearchResults?.Where(t => !sources.Contains(t.Url)) ?? [])
                {
                    sources.Add(searchResult.Url);

                    yield return searchResult.ToSourceUIPart();
                }

                foreach (var searchResult in chunk?.Videos?.Where(t => !sources.Contains(t.Url)) ?? [])
                {
                    sources.Add(searchResult.Url);

                    yield return searchResult.ToSourceUIPart();
                }
            }

            if (!string.IsNullOrEmpty(firstChoice?.FinishReason))
            {
                if (currentStreamId is not null && chunk?.Id is not null)
                {
                    yield return currentStreamId.ToTextEndUIMessageStreamPart();

                    currentStreamId = null;
                }

                var outputTokens = chunk?.Usage?.CompletionTokens ?? 0;
                var inputTokens = chunk?.Usage?.PromptTokens ?? 0;
                var totalTokens = chunk?.Usage?.TotalTokens ?? 0;

                // Build the extra metadata only if needed
                Dictionary<string, object>? extraMetadata = null;
                var searchContextSize = chunk?.Usage?.SearchContextSize;
                var citationTokens = chunk?.Usage?.CitationTokens;

                // Only create the dictionary if there's at least one non-null value
                if (searchContextSize != null || citationTokens != null)
                {
                    extraMetadata = [];
                    if (searchContextSize != null)
                        extraMetadata["search_context_size"] = searchContextSize;
                    if (citationTokens != null)
                        extraMetadata["citation_tokens"] = citationTokens;
                }

                yield return firstChoice.FinishReason.ToFinishUIPart(
                    chatRequest.Model!,
                    outputTokens,
                    inputTokens,
                    totalTokens,
                    chatRequest.Temperature,
                    reasoningTokens: chunk?.Usage?.ReasoningTokens,
                    extraMetadata: extraMetadata
                );
            }
        }
    }

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest imageRequest, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        if (!PerplexityModels.AgentModels.Any(a => a.Id.EndsWith($"/{options.Model}")))
        {
            throw new NotImplementedException();
        }

        ApplyAuthHeader();

        return await _client.GetResponses(
                   options, ct: cancellationToken);
    }

    public IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        if (!PerplexityModels.AgentModels.Any(a => a.Id.EndsWith($"/{options.Model}")))
        {
            throw new NotImplementedException();
        }

        ApplyAuthHeader();

        return _client.GetResponsesUpdates(
           options,
           ct: cancellationToken);
    }

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

