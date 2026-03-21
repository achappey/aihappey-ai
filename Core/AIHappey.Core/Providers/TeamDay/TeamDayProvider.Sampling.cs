using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.TeamDay;

public partial class TeamDayProvider
{

    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chatRequest);

        var model = NormalizeAgentModelId(chatRequest.GetModel());
        var metadata = ReadSamplingMetadata(chatRequest);
        var prompt = BuildPromptFromSamplingMessages(chatRequest.Messages);
        var result = await ExecuteAgentAsync(model, prompt, metadata, stream: false, cancellationToken);

        return new CreateMessageResult
        {
            Model = chatRequest.GetModel() ?? model,
            Role = Role.Assistant,
            StopReason = "stop",
            Content = [result.Text.ToTextContentBlock()],
            Meta = BuildSamplingMeta(result)
        };
    }


    private static JsonObject BuildSamplingMeta(TeamDayExecutionResult result)
    {
        var meta = new JsonObject
        {
            ["inputTokens"] = TryGetUsageInt(result.Usage, "prompt_tokens"),
            ["outputTokens"] = TryGetUsageInt(result.Usage, "completion_tokens"),
            ["totalTokens"] = TryGetUsageInt(result.Usage, "total_tokens"),
            ["executionId"] = result.ExecutionId
        };

        if (!string.IsNullOrWhiteSpace(result.ChatId))
            meta["chatId"] = result.ChatId;

        if (!string.IsNullOrWhiteSpace(result.SessionId))
            meta["sessionId"] = result.SessionId;

        return meta;
    }


    private static string BuildPromptFromSamplingMessages(IEnumerable<SamplingMessage>? messages)
    {
        var lines = new List<string>();

        foreach (var message in messages ?? [])
        {
            var role = message.Role switch
            {
                Role.Assistant => "assistant",
                Role.User => "user",
                _ => "user"
            };

            var text = message.ToText();
            if (!string.IsNullOrWhiteSpace(text))
                lines.Add($"{role}: {text}");
        }

        return string.Join("\n\n", lines);
    }


    private static TeamDayRequestMetadata ReadSamplingMetadata(CreateMessageRequestParams chatRequest)
    {
        if (chatRequest.Metadata is not JsonObject metadata)
            return new TeamDayRequestMetadata();

        if (metadata[nameof(TeamDay).ToLowerInvariant()] is JsonObject providerMetadata)
            return providerMetadata.Deserialize<TeamDayRequestMetadata>(Json) ?? new TeamDayRequestMetadata();

        return new TeamDayRequestMetadata
        {
            SpaceId = metadata["teamday:spaceId"]?.GetValue<string>(),
            SessionId = metadata["teamday:sessionId"]?.GetValue<string>(),
            ChatId = metadata["teamday:chatId"]?.GetValue<string>()
        };
    }

}
