using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Vercel.Mapping.Abstractions;

public interface IVercelChatRequestUnifiedMapper
{
    AIRequest ToUnifiedRequest(ChatRequest request, string providerId);

    ChatRequest ToChatRequest(AIRequest request);
}

