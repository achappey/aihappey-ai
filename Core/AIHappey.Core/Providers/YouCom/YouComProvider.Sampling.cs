using System.Text;
using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.YouCom;

public partial class YouComProvider
{
    private async Task<CreateMessageResult> SamplingCoreAsync(
        CreateMessageRequestParams chatRequest,
        CancellationToken cancellationToken)
    {
        var model = chatRequest.GetModel() ?? throw new ArgumentException("Model missing", nameof(chatRequest));
        var prompt = BuildPromptFromSamplingMessages(chatRequest.Messages);

        if (string.IsNullOrWhiteSpace(prompt))
        {
            return new CreateMessageResult
            {
                Model = model,
                Role = Role.Assistant,
                StopReason = "error",
                Content = ["You.com requires non-empty sampling input.".ToTextContentBlock()]
            };
        }

        if (IsResearchModel(model))
        {
            var result = await ExecuteResearchAsync(model, prompt, null, null, cancellationToken);
            return ToSamplingResult(result);
        }

        if (!IsAgentModel(model))
            throw new NotSupportedException($"Unsupported You.com sampling model '{model}'.");

        var text = new StringBuilder();
        var sources = new Dictionary<string, YouComSourceInfo>(StringComparer.OrdinalIgnoreCase);
        var finishReason = "error";
        long? runtimeMs = null;

        await foreach (var evt in StreamAgentEventsAsync(model, prompt, null, toolHint: false, new YouComRequestMetadata(), cancellationToken))
        {
            if (!string.IsNullOrWhiteSpace(evt.Delta))
                text.Append(evt.Delta);

            foreach (var source in evt.Sources ?? [])
                sources[source.Url] = source;

            if (evt.Type == "response.done")
            {
                finishReason = evt.Finished == false ? "error" : "stop";
                runtimeMs = evt.RuntimeMs;
            }
        }

        return ToSamplingResult(new YouComExecutionResult
        {
            Id = Guid.NewGuid().ToString("n"),
            Model = model,
            Endpoint = "agents.runs",
            Text = text.ToString(),
            Sources = [.. sources.Values],
            FinishReason = finishReason,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            RuntimeMs = runtimeMs,
            Error = finishReason == "stop" ? null : "You.com agent run did not finish successfully."
        });
    }
}
