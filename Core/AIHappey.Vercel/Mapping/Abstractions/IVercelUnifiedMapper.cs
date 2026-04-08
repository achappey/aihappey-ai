using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Vercel.Mapping.Abstractions;

public interface IVercelUnifiedMapper
{
    AIRequest ToUnifiedRequest(ChatRequest request, string providerId);

    ChatRequest ToChatRequest(AIRequest request);

    AIInputItem ToUnifiedInputItem(UIMessage message);

    UIMessage ToUIMessage(AIOutputItem item, string? id = null);

    AIEventEnvelope ToUnifiedEvent(UIMessagePart part);

    UIMessagePart ToUIMessagePart(AIEventEnvelope envelope);
}

