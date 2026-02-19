using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using AIHappey.Common.Model;
using AIHappey.Vercel.Models;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.Runpod;

public partial class RunpodProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    public RunpodProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.runpod.ai/v2/");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Runpod)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
        => await this.ListModels(_keyResolver.Resolve(GetIdentifier()));

    public string GetIdentifier() => nameof(Runpod).ToLowerInvariant();

    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        var model = await this.GetModel(chatRequest.GetModel(), cancellationToken);

        return (model?.Type) switch
        {
            "speech" => await this.SpeechSamplingAsync(chatRequest,
                                    cancellationToken: cancellationToken),
            "image" => await this.ImageSamplingAsync(chatRequest,
                                    cancellationToken: cancellationToken),
            "language" => await this.ChatCompletionsSamplingAsync(chatRequest,
                                    cancellationToken: cancellationToken),
            _ => throw new NotImplementedException(),
        };
    }

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
        => RunpodSpeechRequest(request, cancellationToken);

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(options);

        var model = NormalizeRunpodModelId(options.Model);
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(options));

        var messages = ToRunpodMessages(options).ToList();
        if (messages.Count == 0)
            throw new InvalidOperationException("No textual input found for Runpod native responses request.");

        using var doc = await RunSyncAdaptiveAsync(
            model: model,
            messages: messages,
            temperature: options.Temperature,
            maxTokens: options.MaxOutputTokens,
            topP: options.TopP is null ? null : (float?)options.TopP,
            topK: null,
            cancellationToken: cancellationToken);

        var root = doc.RootElement;
        var id = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == System.Text.Json.JsonValueKind.String
            ? idEl.GetString() ?? Guid.NewGuid().ToString("n")
            : Guid.NewGuid().ToString("n");

        var (text, promptTokens, completionTokens) = ExtractRunpodTextAndUsage(root);
        return BuildResponseResultFromRunpod(id, model, text, promptTokens, completionTokens, options);
    }

    public async IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(
        ResponseRequest options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(options);

        var model = NormalizeRunpodModelId(options.Model);
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(options));

        var messages = ToRunpodMessages(options).ToList();
        if (messages.Count == 0)
            throw new InvalidOperationException("No textual input found for Runpod native responses request.");

        var (jobId, completedDoc) = await SubmitRunAsync(
            model: model,
            messages: messages,
            temperature: options.Temperature,
            maxTokens: options.MaxOutputTokens,
            topP: options.TopP is null ? null : (float?)options.TopP,
            topK: null,
            cancellationToken: cancellationToken);

        using var finalDoc = completedDoc ?? await WaitForRunCompletionAsync(model, jobId, cancellationToken);

        var root = finalDoc.RootElement;
        var id = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == System.Text.Json.JsonValueKind.String
            ? idEl.GetString() ?? Guid.NewGuid().ToString("n")
            : Guid.NewGuid().ToString("n");

        var (text, promptTokens, completionTokens) = ExtractRunpodTextAndUsage(root);
        var result = BuildResponseResultFromRunpod(id, model, text, promptTokens, completionTokens, options);

        yield return new ResponseCreated
        {
            SequenceNumber = 1,
            Response = result
        };

        yield return new ResponseInProgress
        {
            SequenceNumber = 2,
            Response = new ResponseResult
            {
                Id = result.Id,
                Object = result.Object,
                CreatedAt = result.CreatedAt,
                CompletedAt = result.CompletedAt,
                Status = "in_progress",
                ParallelToolCalls = result.ParallelToolCalls,
                Model = result.Model,
                Temperature = result.Temperature,
                Output = result.Output,
                Usage = result.Usage,
                Text = result.Text,
                ToolChoice = result.ToolChoice,
                Tools = result.Tools,
                Reasoning = result.Reasoning,
                Store = result.Store,
                MaxOutputTokens = result.MaxOutputTokens,
                Error = result.Error,
                Metadata = result.Metadata
            }
        };

        yield return new ResponseOutputTextDelta
        {
            SequenceNumber = 3,
            ItemId = $"msg_{result.Id}",
            Outputindex = 0,
            ContentIndex = 0,
            Delta = text
        };

        yield return new ResponseOutputTextDone
        {
            SequenceNumber = 4,
            ItemId = $"msg_{result.Id}",
            Outputindex = 0,
            ContentIndex = 0,
            Text = text
        };

        yield return new ResponseCompleted
        {
            SequenceNumber = 5,
            Response = result
        };
    }

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
        => throw new NotSupportedException();

}
