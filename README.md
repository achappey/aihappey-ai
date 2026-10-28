# aihappey-ai

A multi-provider **.NET AI gateway** exposing normalized endpoints for models, media, skills, agents and MCP capabilities.

No smart routing fairy. No shiny admin portal. No hidden logic maze. No code execution.
Just provider capabilities flattened, normalized hard and exposed through open contracts.
Stateless. Boring on purpose.

Access 150k+ models and provider-native capabilities from your favorite client.

**Documentation:** https://docs.aihappey.com/gateway  
**API:** https://ai.aihappey.net

## Quick start

```bash
curl "https://ai.aihappey.net/v1/chat/completions" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $OPENAI_API_KEY" \
  -d '{
    "model": "openai/gpt-5.4-mini",
    "messages": [
      { "role": "user", "content": "Say hi" }
    ]
  }'
```

See the [API documentation](https://docs.aihappey.com/gateway) for endpoints, authentication, request schemas and examples.

## Run standalone on Windows

Run the gateway standalone on your own Windows machine. Download the latest standalone Windows x64 build:
[aihappey-windows-x64.zip](https://github.com/achappey/aihappey-ai/releases/download/latest/aihappey-windows-x64.zip)

Provider credentials are loaded from: `%LOCALAPPDATA%\aihappey\headers.json`  
Example: `{"X-OpenAI-Key":"sk-...","X-Anthropic-Key":"sk-ant-..."}`

Start `aihappey.Windows.exe` and use the same APIs locally at `http://localhost:5000`.

## Provider Support Matrix

Below is a capability matrix for all supported providers. 
Provider descriptions and links are available [here](https://chat.aihappey.com/providers).

The table below shows which endpoints each provider implements (✅), not yet implemented (❌), partially implemented (🟡) or for which an endpoint is not applicable to the provider (➖).

💬 [Chat](https://ai-sdk.dev/docs/reference/ai-sdk-ui/use-chat) | ✍️ [ChatCompletions](https://developers.openai.com/api/reference/resources/chat) | 📤 [Responses](https://developers.openai.com/api/reference/resources/responses) | 📨 [Messages](https://platform.claude.com/docs/en/api/messages) | 🖼️ [Images](https://ai-sdk.dev/docs/ai-sdk-core/image-generation) | 🎙️ [Transcriptions](https://ai-sdk.dev/docs/ai-sdk-core/transcription) | 🔊 [Speech](https://ai-sdk.dev/docs/ai-sdk-core/speech) | 🏆 [Rerank](https://ai-sdk.dev/docs/ai-sdk-core/reranking) | 🎬 [Video](https://ai-sdk.dev/docs/ai-sdk-core/video-generation) | 🧩 [Skills](https://developers.openai.com/api/reference/resources/skills) | 🧬 [Embeddings](https://developers.openai.com/api/reference/resources/embeddings) | ⚖️ [Decisions](https://developers.openai.com/api/reference/resources/embeddings) | 📡 [Realtime](https://ai-sdk.dev/docs/ai-sdk-core/realtime)


| Provider | [💬](https://ai-sdk.dev/docs/reference/ai-sdk-ui/use-chat) | [✍️](https://developers.openai.com/api/reference/resources/chat) | [📤](https://developers.openai.com/api/reference/resources/responses) | [📨](https://platform.claude.com/docs/en/api/messages) | [🖼️](https://ai-sdk.dev/docs/ai-sdk-core/image-generation) | [🎙️](https://ai-sdk.dev/docs/ai-sdk-core/transcription) | [🔊](https://ai-sdk.dev/docs/ai-sdk-core/speech) | [🏆](https://ai-sdk.dev/docs/ai-sdk-core/reranking) | [🎬](https://ai-sdk.dev/docs/ai-sdk-core/video-generation) | [🧩](https://developers.openai.com/api/reference/resources/skills) | [🧬](https://developers.openai.com/api/reference/resources/embeddings) | [⚖️](https://developers.openai.com/api/reference/resources/decisions) | [📡](https://ai-sdk.dev/docs/ai-sdk-core/realtime)
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 302AI | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ | ➖ | ➖ |
| AbacusAI | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| Abliberation | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AddisAI | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Aether | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ❌ | ➖ | ✅ |
| AgDev | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Agen | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Agent37 | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AgentAIGateway | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AgentContainer | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AgentDiscuss | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Agentics | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ |
| AgentPhone | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| AgentSky | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AgnesAI | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| AI21 | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AiApiWorld | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| AIBadgr | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| AIBramha | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AICC | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ❌ | ➖ | ✅ | ➖ | ➖ |
| Aichixia | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ |
| AICredits | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ |
| AIDuet | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AIgateway | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ |
| AIHorde | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AIHubMix | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ |
| AIMagicx | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ❌ | ➖ | ➖ |
| AIML | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ |
| AINative | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Ainvented | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AionLabs | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AIRouter | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AIsa | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ✅ | ➖ | ✅ |
| Aivara | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AkashML | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AKI | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Akumi | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| Alibaba | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ |
| AllToken | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| AlphaNeural | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| AmazonBedrock | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Ambient | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Amnt | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Antbase | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Antimatter | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Anthropic | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AnyRouter | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ | ✅ | ✅ | ➖ |
| AndyAPI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Apertis | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ |
| ApiAirforce | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ | ➖ | ✅ | ➖ | ✅ |
| APIPASS | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ❌ | ➖ | ➖ |
| APIpie | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| APIPod | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| APIyi | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ | ➖ | ✅ |
| Apekey | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AppNZ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ |
| ArceeAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ARKLabs | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ |
| ArkRoute | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ❌ | ➖ | ➖ |
| ArliAI | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ARWriter | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ASIOne | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AskARC | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AskCodi | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AssemblyAI | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Assisters | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ | ✅ |
| Astica | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Astria | ✅ | 🟡 | 🟡 | 🟡 | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ |
| Async | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| AtlasCloud | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| Audixa | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Augure | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Auriko | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Avian | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Azerion | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ |
| Azure | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ |
| Baidu | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| BananaPeel | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| BaseAPI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Baseten | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| BastionGPT | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| BazaarLink | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ❌ | ➖ | ❌ | ➖ | ➖ |
| BeastLabAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| BergetAI | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ |
| Bineric | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| BLACKBOX | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| BlackForestLabs | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| Blink | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ | ➖ | ✅ | ➖ | ✅ |
| BlockRun | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ❌ | ➖ | ❌ | ➖ | ➖ |
| BotVerse | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Brainiall | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ❌ | ➖ | ➖ | ➖ | ➖ |
| Bria | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| Brave | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| BrowserUse | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| BytePlus | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| ByteSpace | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Bytez | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ |
| Cailos | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| CallMissed | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ |
| CAMBAI | ✅ | ✅ | ✅ | ❌ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Camelai | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| CanopyWave | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| CanRouter | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ |
| CARouter | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ | ✅ |
| Cartesia | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| CaseDev | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ✅ | ✅ |
| Cencori | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ |
| Cerebras | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Celeris | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| ChainGPT | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ChainHub | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ❌ | ➖ | ➖ |
| ChatQT | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Clarifai | ✅ | ✅ | ❌ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| CheaperInference | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| CheapGrok | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Chutes | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ❌ | ➖ | ❌ |
| CircuitNotion | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Cirrascale | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ✅ | ➖ | ➖ | ➖ |
| Citadelis | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Clankie | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ClawHub | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| Clauddy | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Claudible | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ClawLite | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Cline | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Clod | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| CloudFerro | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| Cloudflare | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| CloudRift | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Codebase | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| CognitivessAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ❌ | ➖ | ➖ |
| Cohere | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ | ✅ |
| Coze | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| CometAPI | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ✅ | ➖ | ❌ | ➖ | ➖ |
| CommandCode | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Commonstack | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Concentrate | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| CondenseChat | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Connect0 | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Copilot | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Cortecs | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ |
| Cortex | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Crazyrouter | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ | ✅ | ❌ | ➖ | ✅ |
| Crusoe | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Cursor | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Daglo | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Dandolo | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Darkbloom | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Databricks | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| DataForSEO | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| DeAPI | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ |
| Decart | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| DedalusLabs | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ |
| Deepbricks | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| DeepInfra | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ |
| DeepL | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| DeepSeek | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Deepgram | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| Depaza | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| DevicAI | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| DigitalOcean | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| DocketRouter | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| DocsRouter | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Doubleword | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| Dust | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| EAGM | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Eachlabs | ❌ | ❌ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ | ❌ | ➖ | ➖ |
| Echo | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Ecoia | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| EdenAI | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Edgee | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ElectronHub | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ❌ | ➖ | ➖ |
| ElevenLabs | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Eliza | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ |
| Ellipsis | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| EmberCloud | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| EmbyAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| EmpirioLabsAI | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ |
| Entrim | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Epho | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| EuGPT | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Euqai | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| EUrouter | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ |
| EvidenceMD | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| EverypixelLabs | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ |
| EvoLinkAI | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ |
| Exa | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Featherless | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ✅ |
| Fal | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ❌ | ➖ | ❌ |
| FastRouter | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ |
| Fikra | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Finora | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Fireworks | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ | ✅ |
| FishAudio | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| FiveDock | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ |
| Flatseek | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Fortytwo | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Founden | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Foundry | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Foureverland | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Fred | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| FreeInference | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Freepik | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ |
| FreedomGPT | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| Friendli | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| FullAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| GateRouter | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| GeekAI | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ |
| GeneralCompute | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Getimg | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| GetGoAPI | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ |
| GigaChat | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Gladia | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Glio | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ |
| GMICloud | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| Google | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ✅ |
| GoogleTranslate | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| GooseAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| GonkaGate | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| GPTsAPI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| GPTProto | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ❌ | ➖ | ➖ |
| Gradium | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| GreenPT | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ | ✅ | ✅ | ➖ |
| GrooveDev | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| Groq | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| GTranslate | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| GWDG | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Haimaker | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Hanzo | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| HarnessRouter | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| HCompany | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Hedra | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ |
| Helicone | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| HelyxAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Herma | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Hetzner | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| HeyGen | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ |
| Hicap | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| HiNow | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ |
| HolySheepAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| HorayAI | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| HostYourAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ✅ |
| HuggingFace | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | 
| HumanBrowser | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| HumeAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Hush | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Hyperbolic | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Hyperbrowser | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| HyperRouter | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ |
| Hyperstack | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| iApp | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Ideogram | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| IGPT | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ILMU | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ |
| ImageRouter | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| Impossibl | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ |
| InceptionLabs | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| Inceptron | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Infercom | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ | ✅ |
| Inferencenet | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| InferLink | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Infermatic | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| Infomaniak | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ |
| Infraxa | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Infron | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ |
| Interfaze | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Inworld | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| IOnet | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| IONOS | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ✅ |
| Isaacus | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ | ✅ | ➖ |
| Ishi | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| IonRouter | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| JassieAI | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| JiekouAI | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ❌ | ➖ | ❌ | ➖ | ➖ |
| JigsawStack | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Jina | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ | ✅ |
| JKAIHub | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| JSON2Video | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| Jules | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Keln | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| Keyplex | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Kilo | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Kimrel | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Kirha | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| KissAPI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| KlingAI | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ |
| KnoxChat | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ❌ | ➖ | ➖ | ➖ |
| Krutrim | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LangbaseAgent | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LangbasePipe | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LaoZhang | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Lara | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Lazu | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LectoAI | ✅ | ❌ | 🟡 | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LelapaAI | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Leonardo | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LexiCo | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LibertAI | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ✅ |
| Lightcone | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LightOn | ✅ | ✅ | ❌ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Lilac | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Lingvanex | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ |
| Linkup | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LitAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LiteRouter | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LovelyRouter | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Llama | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Neuphonic | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Mammouth | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| PLaMo | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Narakeet | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LLM7 | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LLMAPI | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ |
| LLMBase | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LLMGateway | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ |
| LLMkiwi | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LLMLayer | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LLMStats | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LLMTR | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ❌ | ➖ | ❌ | ➖ | ➖ |
| LLMWise | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LMRouter | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Logfare | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ |
| LogicosLLMHub | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LongCat | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LOVO | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| LTX | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| LucidQuery | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LumaAI | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| Lumecoder | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Lumenfall | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| LuminoAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Lunos | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ |
| LXG2IT | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Lyceum | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| M8tes | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Magisterium | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| MancerAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| MangoBoost | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Manyfold | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Manus | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| MARA | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| MaritacaAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Martian | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| MatterAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Maxlayer | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ❌ | ➖ | ✅ |
| MegaLLM | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| MegaNova | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ |
| Melious | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ✅ |
| MemoryRouter | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Merge | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| MeshAPI | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ❌ | ➖ | ➖ |
| Messari | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Mia21 | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| MIAPI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| MIMICXAI | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ |
| MiniMax | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ |
| MintRouter | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| Mireye | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| MiroMind | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Mistral | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ |
| Mixedbread | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Mixlayer | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| MLJunction | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Modal | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ModelMax | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| ModelRouter | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ModelScope | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ModelsLab | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ |
| ModelSync | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| MoleAPI | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ |
| Monica | ✅ | ✅ | ❌ | ❌ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| MonsterGaming | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Moonshot | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Morph | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Morpheus | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| MuleRouter | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ❌ | ➖ | ❌ | ➖ | ➖ |
| MuleRun | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| MumeAI | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ | ➖ | ✅ |
| MurfAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| MyCoAI | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| MyRouter | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ | ➖ | ➖ |
| NagaAI | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ |
| NanoGPT | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ |
| Nataris | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| NavyAI | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ |
| NEARAI | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ |
| NLPCloud | ✅ | 🟡 | 🟡 | ❌ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Nebius | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| Neosantara | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ✅ |
| NetMind | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ✅ |
| NeuralRing | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Neuralwatt | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| NexosAI | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Nextbit | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Nexusify | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| NinjaChat | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| Nodion | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Noiz | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Nomic | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| NRPNautilus | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Nodebyt | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| NONKYCAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Notte | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| NousResearch | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Nouswise | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| NovAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Novita | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ |
| Nscale | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| NVIDIA | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Occludra | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OCRSkill | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Octagon | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OfoxAI | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OhMyGPT | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ |
| Ollama | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Omneity | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OmniaKey | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OneInfer | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ |
| OneKey | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| OODAAI | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ❌ | ➖ | ➖ |
| OnlyPixAI | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ |
| OPEAI | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| OpenAdapter | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ |
| OpenAI | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ✅ | ✅ | ✅ |
| OpenCodeGo | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OpenCodeZen | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| Openference | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| OpenGate | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OpenGateway | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OpenHands | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OpenLimits | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OpenRouter | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ |
| OpperAI | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ |
| OpusCode | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Oraicle | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OrbiGPU | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OrcaRouter | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ |
| OrqAgentRuntime | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OrqRouter | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ |
| Osiris | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OurToken | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OVHcloud | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Parallel | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ParalonCloud | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Parasail | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Paul | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| PawaAI | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ |
| PaxaLabs | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| PayPerQ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ |
| Perceptron | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Perplexity | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ✅ |
| PiAPI | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ |
| Picklyone | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ❌ | ➖ | ➖ |
| Picsart | ✅ | ✅ | ✅ | ✅ | 🟡 | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Pinecone | ❌ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ | ✅ |
| Pioneer | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Pixserp | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Plori | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Poe | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| Pollinations | ✅ | ❌ | ❌ | ❌ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Poolside | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Portkey | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ |
| PreAPI | ❌ | ❌ | ❌ | ❌ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ |
| Puter | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| PredictionGuard | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ | ✅ | ➖ |
| PrimeIntellect | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| PrunaAI | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| PublicAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ❌ | ➖ | ➖ | ➖ |
| Qiniu | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| Quantanova | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| QuiverAI | ✅ | ❌ | ❌ | ❌ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Radiance | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Radient | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Railwail | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| RampRouter | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| RaxAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ReByteModels | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ReByteTasks | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Recraft | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| RedPill | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ |
| RegoloAI | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ✅ |
| ReGraph | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| RekaAI | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Relace | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| RelaxAI | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ | ✅ |
| Renderful | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ | ➖ | ✅ | ➖ | ➖ |
| Replicate | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Requesty | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ |
| ResearchAgent | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ResembleAI | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Respeecher | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Reve | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| RewindAI | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ |
| Rime | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| RodiumAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| RoutePlex | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Router9 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| RouterPlus | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ | ✅ | ➖ |
| Routera | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| RouterLink | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Routeway | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Routmy | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ✅ |
| Routstr | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Roxy | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| RunAPI | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Runcrate | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ❌ | ➖ | ➖ |
| Runpod | ✅ | ✅ | ❌ | ❌ | ✅ | ❌ | ✅ | ✅ | ➖ | ➖ | ➖ |
| Runtimo | ✅ | ❌ | ❌ | ❌ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| Runtype | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Runware | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ |
| Runway | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ |
| SailResearch | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| SambaNova | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Samtal | ✅ | ✅ | ✅ | ✅ | ➖ | ❌ | ✅ | ➖ | ➖ | ➖ | ➖ |
| SandBase | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ |
| Sapiom | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Sargalay | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Sarvam | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Scaleway | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ | ✅ |
| ScalixWorld | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| SchatziAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ScrapeLLM | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| SEALION | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| Secrypt | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Segmind | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Select | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| SelinaAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Sennoric | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| SenseNova | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| SerenityEdge | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ | ✅ | ✅ | ➖ |
| Setapp | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ShadowOS | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Shakespeare | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ShannonAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ShareAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Shengsuanyun | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ShuttleAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| SiliconFlow | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ |
| SimpleLLM | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Simplismart | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| SkypoolToken | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Skyvern | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Sluis | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ❌ | ➖ | ➖ |
| SmallestAI | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| SmartAIPI | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Smooth | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Softref | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Soniox | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| SovereignEG | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| Sovinfra | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ |
| SovrGPT | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ |
| SpaceXAI | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ✅ |
| Speechactors | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Speechify | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Speechmatics | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| StabilityAI | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| StandardCompute | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| StealthGPT | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| StepFun | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| StreamLake | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| StrikeAI | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| SudoRouter | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ |
| SUFY | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| SUMMA | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| SunbirdAI | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| SunoAPI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| SUPA | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| SurferCloud | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| SUTRA | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Swarms | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Synexa | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ |
| Synthetic | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| Tapas | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Tavily | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| TEAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| TeamDay | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Telnyx | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ✅ | ➖ |
| Tembo | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| TencentHunyuan | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| TensorBlock | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| TensorX | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ |
| TerminalSkills | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| Tetrate | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Thalam | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ |
| Thaura | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| TheOldAPI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| TheRouterAI | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ |
| TextSynth | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Tiarina | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| TierUp | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| TigerCity | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| TikHubAI | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ❌ |
| Tilde MT | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Tinfoil | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| TinyFish | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ToAPIs | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ❌ | ➖ | ➖ |
| Together | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ |
| Token360 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ |
| TokenFlux | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| TokenHub | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| TokenLab | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ | ➖ | ➖ |
| TokenSpend | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| TrueFoundry | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ |
| TrustedRouter | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| TTSReader | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| TwelveLabs | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| Typecast | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| UltraSafe | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ➖ | ❌ | ➖ | ➖ | ➖ |
| Unbound | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| UncensoredChat | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| UncloseAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ❌ | ➖ | ➖ | ➖ | ➖ |
| UniAPI | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ❌ | ➖ | ➖ |
| UnrealSpeech | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Upstage | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| UVoiceAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Valyu | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| VanceAI | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| Vapi | ✅ | 🟡 | 🟡 | ❌ | ➖ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ |
| Venice | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ✅ | ➖ |
| Verbatik | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Vercel | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Verda | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| VIABLELab | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| VibeKit | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Vidu | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ |
| Vikasit | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Vivgrid | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ |
| VLMRun | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ |
| VoiceAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Vogent | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Volink | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| VoyageAI | ❌ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ | ✅ |
| Vultr | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ |
| Wafer | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| WAI | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Waslo | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| WatsonX | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| WAYSCloud | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ | ✅ |
| WaveSpeed | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| WebCrawlerAPI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| WebsearchAPI | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| WellSaid | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| WiseRouter | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Writer | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| XiaomiMIMO | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Xpersona | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Yandex | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| Yollomi | ✅ | ❌ | ❌ | ❌ | ✅ | ➖ | ➖ | ➖ | ❌ | ➖ | ➖ |
| YouCom | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| YourVoic | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Yutori | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Zai | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ |
| Zeabur | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Zebracat | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| Zenlayer | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ |
| ZenMux | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ✅ | ✅ | ➖ |
| ZeroEntropy | ❌ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ | ✅ |
| Zoom | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| ZyloAPI | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Zyphra | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |

### StrikeAI Decisions

Use `X-StrikeAI-Key` (or AzureAuth `AIServiceConfig.StrikeAI`) and model
`strikeai/strike-1`. Authenticated discovery calls `GET https://strike-ai.eu/v1/models`,
preserves every advertised model ID and context limit, and marks models as `decision`.
Both `/api/decisions` and `/v1/decisions` use native `POST /v1/systemone`;
there is no chat, generation, media, embeddings, or streaming API adapter.

Boolean questions map to Strike's `noul` probability of yes; choices accept 2-255
named text/null descriptions, and scores accept 2-10 ordered text levels. Each call
accepts at most 32 questions. Instructions must be text; boolean criteria, box
questions, SDK files, OpenAI images, reasoning, and output-token controls are
rejected rather than silently discarded. Legacy string/object/array state is preserved.
Set `providerOptions.strikeai.return_raw_probabilities` to a boolean to forward the
documented native option. The OpenAI adapter requests distributions explicitly and
preserves string versus boolean choices, names, order, and zero-based score levels.
`headers["Idempotency-Key"]` is forwarded; gateway credential headers are not.

Native billing (`cost_eur`, `cost_currency`), latency, retention information (when
returned), and the complete response remain available in `providerMetadata.strikeai`.
Upstream validation, payload/context limits, quota/rate limits, billing errors,
warming, and engine-unavailable errors retain their HTTP status and error body;
the adapter does not automatically retry billable decisions. Probabilities are
uncalibrated, not production-calibrated confidence guarantees.

Sources: [API reference](https://strike-ai.eu/console/api),
[quickstart](https://strike-ai.eu/docs/quickstart), and the
[official SDK](https://pypi.org/project/strike-ai/). Strike's published SDK documents
yes/no, choice, and score inference only. Its MCP tools wrap that same endpoint;
health, public stats/pricing/benchmarks, waitlist, trace ingestion, and retention
deletion have no matching gateway model contract and do not add gateway endpoints.
The API reference's generic `assets` object and `BoxAnswer` response union alone
do not establish image/box support for `strike-1`; no such capability is advertised.
