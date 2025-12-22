using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers;
using AIHappey.Core.AI;
using ANT = Anthropic.SDK;

namespace AIHappey.Core.Providers.Anthropic.Extensions;

public static partial class AnthropicExtensions
{
    public static ANT.Messaging.MessageParameters ToMessageParameters(this ModelContextProtocol.Protocol.CreateMessageRequestParams chatRequest,
        IEnumerable<ANT.Common.Tool>? tools)
    {
        var model = chatRequest.GetModel() ?? ANT.Constants.AnthropicModels.Claude45Sonnet;
        var hasLargeContext = model.StartsWith("claude-3-7")
               || model.StartsWith("claude-opus-4")
               || model.StartsWith("claude-sonnet-4")
               || model.StartsWith("claude-haiku-4");

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
            MaxTokens = hasLargeContext ? 20000 : 4096,
            Model = model,
            Container = chatRequest.Metadata.ToContainer(),
            Thinking = chatRequest.Metadata.ToThinkingConfig(),
            System = !string.IsNullOrEmpty(chatRequest.SystemPrompt)
                        ? [new ANT.Messaging.SystemMessage(chatRequest.SystemPrompt)] : []
        };
    }


    public static ANT.Messaging.MessageParameters ToMessageParameters(this IEnumerable<ANT.Messaging.Message> messages,
        string model,
        IEnumerable<ANT.Common.Tool>? tools,
        IEnumerable<ANT.Messaging.SystemMessage>? systemInstructions = null)
    {
        var hasLargeContext = model.StartsWith("claude-3-7")
               || model.StartsWith("claude-opus-4")
               || model.StartsWith("claude-sonnet-4");


        return new()
        {
            Messages = [.. messages],
            Tools = tools?.ToList() ?? [],
            MaxTokens = hasLargeContext ? 20000 : 4096,
            Model = model,
            System = systemInstructions != null ? [.. systemInstructions] : []
        };
    }


    public static ANT.Messaging.MessageParameters ToMessageParameters(this Common.Model.ChatRequest chatRequest,
       IEnumerable<ANT.Messaging.Message> messages,
       string model,
       IEnumerable<ANT.Messaging.SystemMessage>? systemInstructions = null)
    {
        var metadata = chatRequest.GetProviderMetadata<AnthropicProviderMetadata>(AnthropicConstants.AnthropicIdentifier);
        var tools = chatRequest.Tools?.ToTools().WithDefaultTools(metadata) ?? [];

        return new()
        {
            Messages = [.. messages],
            Tools = [.. tools],
            Container = metadata?.CodeExecution != null
                && metadata?.Container?.Skills?.Any() == true ?
                metadata?.Container : null,
            MCPServers = [.. metadata?.MCPServers ?? []],
            MaxTokens = chatRequest.MaxTokens ??
                (metadata?.Thinking != null ?
                metadata.Thinking.BudgetTokens * 2 : 4096),
            Thinking = metadata?.Thinking,
            Model = model,
            System = systemInstructions != null ? [.. systemInstructions] : []
        };
    }

}
