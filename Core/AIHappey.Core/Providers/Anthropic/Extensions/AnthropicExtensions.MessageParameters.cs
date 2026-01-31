using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Anthropic;
using AIHappey.Core.AI;
using ANT = Anthropic.SDK;

namespace AIHappey.Core.Providers.Anthropic.Extensions;

public static partial class AnthropicExtensions
{
    public static ANT.Messaging.MessageParameters ToMessageParameters(this ModelContextProtocol.Protocol.CreateMessageRequestParams chatRequest,
        IEnumerable<ANT.Common.Tool>? tools)
    {
        var model = chatRequest.GetModel() ?? ANT.Constants.AnthropicModels.Claude45Sonnet;
        var searchTool = chatRequest.Metadata.ToWebSearchTool();
        var codeExecutionTool = chatRequest.Metadata.ToCodeExecution();
        List<ANT.Common.Tool> allTools = tools?.ToList() ?? [];

        if (searchTool != null)
        {
            allTools.Add(searchTool);
        }

        if (codeExecutionTool != null)
        {
            allTools.Add(codeExecutionTool);
        }

        return new()
        {
            Messages = [.. chatRequest.Messages.ToMessages()],
            Tools = allTools?.ToList() ?? [],
            MaxTokens = chatRequest.MaxTokens,
            Model = model,
            Container = chatRequest.Metadata.ToContainer(),
            Thinking = chatRequest.Metadata.ToThinkingConfig(),
            System = !string.IsNullOrEmpty(chatRequest.SystemPrompt)
                        ? [new ANT.Messaging.SystemMessage(chatRequest.SystemPrompt)] : []
        };
    }

    public static ANT.Messaging.MessageParameters ToMessageParameters(this Common.Model.ChatRequest chatRequest,
       IEnumerable<ANT.Messaging.Message> messages,
       string model,
       IEnumerable<ANT.Messaging.SystemMessage>? systemInstructions = null)
    {
        var metadata = chatRequest.GetProviderMetadata<AnthropicProviderMetadata>(AnthropicConstants.AnthropicIdentifier);
        var tools = chatRequest.Tools?.ToTools().WithDefaultTools(metadata) ?? [];

        ANT.Messaging.MessageParameters result = new()
        {
            Messages = [.. messages],
            Tools = [.. tools],
            Container = metadata?.CodeExecution != null
                && metadata?.Container?.Skills?.Count > 0 ?
                metadata?.Container : null,
            MCPServers = [.. metadata?.MCPServers ?? []],
            Thinking = metadata?.Thinking,
            Model = model,
            System = systemInstructions != null ? [.. systemInstructions] : []
        };

        if (chatRequest.MaxOutputTokens is not null)
        {
            result.MaxTokens = chatRequest.MaxOutputTokens.Value;
        }

        return result;
    }

}
