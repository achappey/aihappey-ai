using AIHappey.Core.AI;

namespace AIHappey.HeaderAuth;

public class HeaderApiKeyResolver(IHttpContextAccessor http) : IApiKeyResolver
{
    private static readonly Dictionary<string, string> ProviderHeaders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["openai"] = "X-OpenAI-Key",
            ["mistral"] = "X-Mistral-Key",
            ["anthropic"] = "X-Anthropic-Key",
            ["google"] = "X-Google-Key",
            ["perplexity"] = "X-Perplexity-Key",
            ["cohere"] = "X-Cohere-Key",
            ["runway"] = "X-Runway-Key",
            ["aiml"] = "X-AIML-Key",
            ["jina"] = "X-Jina-Key",
            ["xai"] = "X-xAI-Key",
            ["scaleway"] = "X-Scaleway-Key",
            ["nscale"] = "X-Nscale-Key",
            ["cerebras"] = "X-Cerebras-Key",
            ["fireworks"] = "X-Fireworks-Key",
            ["sambanova"] = "X-SambaNova-Key",
            ["hyperbolic"] = "X-Hyperbolic-Key",
            ["zai"] = "X-Zai-Key",
            ["stabilityai"] = "X-StabilityAI-Key",
            ["groq"] = "X-Groq-Key",
            ["elevenlabs"] = "X-ElevenLabs-Key",
            ["novita"] = "X-Novita-Key",
            ["together"] = "X-Together-Key",
            ["telnyx"] = "X-Telnyx-Key",
            ["alibaba"] = "X-Alibaba-Key",
            ["nvidia"] = "X-NVIDIA-Key",
            ["nebius"] = "X-Nebius-Key",
            ["deepgram"] = "X-Deepgram-Key",
            ["runware"] = "X-Runware-Key",
            ["deepseek"] = "X-DeepSeek-Key",
            ["canopywave"] = "X-CanopyWave-Key",
            ["tinfoil"] = "X-Tinfoil-Key",
            ["deepinfra"] = "X-DeepInfra-Key",
            ["inferencenet"] = "X-Inferencenet-Key",
            ["cloudrift"] = "X-CloudRift-Key",
            ["asyncai"] = "X-AsyncAI-Key",
            ["replicate"] = "X-Replicate-Key",
            ["baseten"] = "X-Baseten-Key",
            ["speechify"] = "X-Speechify-Key",
            ["contextualai"] = "X-ContextualAI-Key",
            ["sarvam"] = "X-Sarvam-Key",
            ["voyageai"] = "X-VoyageAI-Key",
            ["minimax"] = "X-MiniMax-Key",
            ["assemblyai"] = "X-AssemblyAI-Key",
            ["resembleai"] = "X-ResembleAI-Key",
            ["ttsreader"] = "X-TTSReader-Key",
            ["speechmatics"] = "X-Speechmatics-Key",
        };

    public string? Resolve(string provider)
    {
        var ctx = http.HttpContext;
        if (ctx == null)
            return null;

        if (!ProviderHeaders.TryGetValue(provider, out var headerName))
            return null;

        // Try canonical name, then lowercase variant
        var headers = ctx.Request.Headers;
        return headers[headerName].FirstOrDefault()
            ?? headers[headerName.ToLowerInvariant()].FirstOrDefault();
    }
}
