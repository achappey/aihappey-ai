# aihappey-ai

A multi-provider **.NET AI inference gateway** exposing key AI endpoints.

No smart routing fairy. No shiny admin portal. No hidden logic maze.
Just providers flattened to capabilities, normalized hard, exposed through open contracts.
Stateless. Boring on purpose.

Access 170k+ models from your favorite client.

## Provider Support Matrix

The table below shows which endpoints each provider implements (✅), not yet implemented (❌), partially implemented (🟡) or for which an endpoint is not applicable to the provider (➖).

| Provider       | [Chat](https://ai-sdk.dev/docs/reference/ai-sdk-ui/use-chat) | [Completions](https://platform.openai.com/docs/api-reference/chat) | [Responses](https://platform.openai.com/docs/api-reference/responses) | [Messages](https://platform.claude.com/docs/en/api/messages) | [Sampling](https://modelcontextprotocol.io/specification/draft/client/sampling) | [Images](https://ai-sdk.dev/docs/ai-sdk-core/image-generation) | [Transcriptions](https://ai-sdk.dev/docs/ai-sdk-core/transcription) | [Speech](https://ai-sdk.dev/docs/ai-sdk-core/speech) | [Rerank](https://ai-sdk.dev/docs/ai-sdk-core/reranking) | Video | [Skills](https://agentskills.io) |
| -------------- | --------- | ----------------- | ------------- | ------------- | --------- | ---------------------- | ------------------------ | ---------------- | ----------- | ----------- | ----------- |
| 302AI          | ✅        | ✅                | ❌            | ❌            | 🟡        | ✅                     | ✅                       | ✅               | ❌          | ✅          | ➖          |
| Abliberation   | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Aether         | ✅        | ✅                | ❌            | ❌            | ✅        | ✅                     | ✅                       | ➖               | ➖          | ➖          | ➖          |
| Agabeyogluai   | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| AgentAIGateway | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Agentics       | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| AI21           | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| AiApiWorld     | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| AIBadgr        | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| AIBramha       | ✅        | ✅                | ❌            | ❌            | ❌        | ❌                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| AICC           | ✅        | ✅                | ✅            | ✅            | ❌        | ✅                     | ➖                       | ❌               | ➖          | ✅          | ➖          |
| Aichixia       | ✅        | ✅                | ❌            | ✅            | ❌        | ❌                     | ❌                       | ❌               | ➖          | ➖          | ➖          |
| AICredits      | ✅        | ✅                | ❌            | ❌            | ❌        | ❌                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| AIDuet         | ✅        | ✅                | ❌            | ✅            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| AIForHire      | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| AIHorde        | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| AIHubMix       | ✅        | ✅                | ❌            | ✅            | ❌        | ❌                     | ❌                       | ❌               | ➖          | ❌          | ➖          |
| AIMagicx       | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| AINative       | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| AIRouter       | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| AIML           | ✅        | ❌                | 🟡            | ❌            | 🟡        | ✅                     | ✅                       | ✅               | ➖          | ➖          | ➖          |
| AIsa           | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| AionLabs       | ✅        | ✅                | ✅            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| AkashML        | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| AKI            | ✅        | ✅                | ❌            | ❌            | ✅        | ✅                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Alibaba        | ✅        | ✅                | ✅            | ❌            | ✅        | ✅                     | ✅                       | ➖               | ➖          | ✅          | ➖          |
| AlphaNeural    | ✅        | ✅                | ❌            | ❌            | ❌        | ❌                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| AmazonBedrock  | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Ambient        | ✅        | ✅                | ❌            | ✅            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Anannas        | ✅        | ✅                | ✅            | ✅            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Answira        | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Anthropic      | ✅        | ✅                | ✅            | ✅            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Apertis        | ✅        | ✅                | ✅            | ✅            | ❌        | ❌                     | ➖                       | ❌               | ❌          | ❌          | ➖          |
| AndyAPI        | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| API1SBS        | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| ApiAirforce    | ✅        | ✅                | ❌            | ✅            | ❌        | ✅                     | ➖                       | ✅               | ➖          | ✅          | ➖          |
| APIFree        | ✅        | ✅                | ❌            | ❌            | ❌        | ❌                     | ➖                       | ❌               | ➖          | ❌          | ➖          |
| APIpie         | ✅        | ✅                | ❌            | ❌            | 🟡        | ✅                     | ✅                       | ✅               | ➖          | ❌          | ➖          |
| APIPod         | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| APIyi          | ✅        | ✅                | ❌            | ❌            | ❌        | ❌                     | ❌                       | ❌               | ❌          | ❌          | ➖          |
| Apekey         | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| AppLingo       | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| ArceeAI        | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| ARKLabs        | ✅        | ✅                | ❌            | ❌            | ✅        | ✅                     | ✅                       | ✅               | ➖          | ➖          | ➖          |
| ArkRoute       | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| ArliAI         | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| ARWriter       | ✅        | ✅                | ❌            | ❌            | ✅        | ❌                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| ASIOne         | ✅        | ❌                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| AskARC         | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| AskCodi        | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| AssemblyAI     | ✅        | ✅                | ❌            | ❌            | 🟡        | ➖                     | ✅                       | ➖               | ➖          | ➖          | ➖          |
| Assisters      | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Astica         | ✅        | ❌                | ❌            | ❌            | ❌        | ✅                     | ➖                       | ✅               | ➖          | ➖          | ➖          |
| AsyncAI        | ✅        | ❌                | ✅            | ❌            | ✅        | ➖                     | ➖                       | ✅               | ➖          | ➖          | ➖          |
| AtlasCloud     | ✅        | ✅                | ❌            | ❌            | 🟡        | ✅                     | ➖                       | ➖               | ➖          | ✅          | ➖          |
| ATXP           | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Augure         | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Audixa         | ✅        | ❌                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ✅               | ➖          | ➖          | ➖          |
| Avian          | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Azerion        | ✅        | ✅                | ❌            | ❌            | 🟡        | ✅                     | ➖                       | ➖               | ➖          | ✅          | ➖          |
| Azure          | ✅        | 🟡                | 🟡            | ❌            | 🟡        | ➖                     | ✅                       | ✅               | ➖          | ➖          | ✅          |
| Baidu          | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| BaseAPI        | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Baseten        | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| BayStone       | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| BazaarLink     | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| BergetAI       | ✅        | ✅                | ❌            | ❌            | 🟡        | ➖                     | ✅                       | ➖               | ✅          | ➖          | ➖          |
| Bineric        | ✅        | ✅                | ❌            | ❌            | 🟡        | ➖                     | ❌                       | ✅               | ➖          | ➖          | ➖          |
| BLACKBOX       | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| BlackForestLabs| ✅        | ❌                | ❌            | ❌            | ✅        | ✅                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| BlazeRail      | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Bleep          | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Blink          | ✅        | ✅                | ❌            | ❌            | ❌        | ✅                     | ➖                       | ✅               | ➖          | ✅          | ➖          |
| BlockRun       | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| BotVerse       | ✅        | ✅                | ✅            | ✅            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Bria           | ✅        | ❌                | ❌            | ❌            | ✅        | ✅                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| BrowserUse     | ✅        | ✅                | ✅            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Brainiall      | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Brave          | ✅        | ✅                | ✅            | ✅            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| BytePlus       | ✅        | ✅                | ✅            | ❌            | 🟡        | ✅                     | ➖                       | ➖               | ➖          | ✅          | ➖          |
| Bytez          | ✅        | ✅                | ❌            | ❌            | ✅        | ✅                     | ➖                       | ✅               | ➖          | ✅          | ➖          |
| Cailos         | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| CairoCoder     | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| CAMBAI         | ✅        | ✅                | ✅            | ❌            | ✅        | ➖                     | ✅                       | ✅               | ➖          | ➖          | ➖          |
| CanopyWave     | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Cartesia       | ✅        | ❌                | ❌            | ❌            | 🟡        | ➖                     | ✅                       | ✅               | ➖          | ➖          | ➖          |
| CaseDev        | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ✅          |
| Cerebras       | ✅        | ✅                | ✅            | ✅            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| ChainGPT       | ✅        | ✅                | ❌            | ❌            | ✅        | ❌                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| ChainHub       | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| CheapestInf... | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| CheapGrok      | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Chutes         | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Cirrascale     | ✅        | ✅                | 🟡            | ❌            | 🟡        | ✅                     | ➖                       | ➖               | ✅          | ➖          | ➖          |
| Citadelis      | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Clankie        | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| ClawPlaza      | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| ClawSwitch     | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Clauddy        | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Claudible      | ❌        | ❌                | ❌            | ✅            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Cline          | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Clod           | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| CloudFerro     | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| CloudRift      | ✅        | ❌                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| CodexForMe     | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Codzen         | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Cohere         | ✅        | ✅                | ✅            | ✅            | ✅        | ➖                     | ➖                       | ➖               | ✅          | ➖          | ➖          |
| CometAPI       | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ✅               | ➖          | ➖          | ➖          |
| Commonstack    | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Concentrate    | ✅        | ✅                | ✅            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| ContextualAI   | ❌        | ❌                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ✅          | ➖          | ➖          |
| Cortecs        | ✅        | ✅                | ✅            | ❌            | ✅        | ➖                     | ✅                       | ➖               | ➖          | ➖          | ➖          |
| Cortex         | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Crazyrouter    | ✅        | ✅                | ❌            | ✅            | ❌        | ❌                     | ❌                       | ❌               | ❌          | ❌          | ➖          |
| Daglo          | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ✅                       | ✅               | ➖          | ➖          | ➖          |
| Dandolo        | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Databricks     | ✅        | ✅                | ✅            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| DataForSEO     | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| DreamGen       | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| DeAPI          | ✅        | ❌                | ❌            | ❌            | 🟡        | ✅                     | ✅                       | ✅               | ➖          | ✅          | ➖          |
| Decart         | ✅        | ❌                | ❌            | ❌            | 🟡        | ✅                     | ➖                       | ➖               | ➖          | ✅          | ➖          |
| DedalusLabs    | ✅        | ✅                | ❌            | ❌            | ❌        | ❌                     | ❌                       | ❌               | ➖          | ➖          | ➖          |
| Deepbricks     | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| DeepInfra      | ✅        | ✅                | 🟡            | ❌            | 🟡        | ✅                     | ✅                       | ✅               | ✅          | ➖          | ➖          |
| DeepL          | ✅        | ✅                | ✅            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| DeepSeek       | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Deepgram       | ✅        | ❌                | 🟡            | ❌            | 🟡        | ➖                     | ✅                       | ✅               | ➖          | ➖          | ➖          |
| DigitalOcean   | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| DistributeAI   | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| DocsRouter     | ✅        | ✅                | ✅            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Dubrify        | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| EAGM           | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Eachlabs       | ❌        | ❌                | ❌            | ❌            | ❌        | ❌                     | ➖                       | ➖               | ➖          | ❌          | ➖          |
| Edgee          | ✅        | ✅                | ❌            | ✅            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Echo           | ✅        | ❌                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| EdenAI         | ✅        | ✅                | ✅            | ✅            | ✅        | ❌                     | ❌                       | ❌               | ➖          | ❌          | ➖          |
| Eliza          | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| EmberCloud     | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| EmbraceableAI  | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| ElectronHub    | ✅        | ✅                | ✅            | ✅            | ❌        | ❌                     | ➖                       | ❌               | ➖          | ❌          | ➖          |
| ElkAPI         | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| ElevenLabs     | ✅        | ❌                | 🟡            | ❌            | 🟡        | ➖                     | ✅                       | ✅               | ➖          | ➖          | ➖          |
| EmbyAI         | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| EuGPT          | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Euqai          | ✅        | ✅                | ❌            | ❌            | ✅        | ✅                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| EUrouter       | ✅        | ✅                | ✅            | ✅            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| EzAI           | ✅        | ✅                | ✅            | ✅            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| EverypixelLabs | ✅        | ❌                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ✅               | ➖          | ➖          | ➖          |
| EvoLinkAI      | ✅        | ✅                | ❌            | ✅            | ❌        | ❌                     | ❌                       | ❌               | ➖          | ❌          | ➖          |
| Exa            | ✅        | ✅                | ✅            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Featherless    | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Fal            | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| FastRouter     | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Finora         | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| FishAudio      | ✅        | ❌                | ❌            | ❌            | ❌        | ➖                     | ✅                       | ✅               | ➖          | ➖          | ➖          |
| Fireworks      | ✅        | ✅                | ✅            | ✅            | 🟡        | ✅                     | ✅                       | ➖               | ✅          | ➖          | ➖          |
| FiveDock       | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Forefront      | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| ForgeByLANA    | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Fortytwo       | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Foureverland   | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Freepik        | ✅        | ❌                | ❌            | ❌            | ✅        | ✅                     | ➖                       | ✅               | ➖          | ✅          | ➖          |
| FreedomGPT     | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Friendli       | ✅        | ❌                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| FullAI         | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| GateMind       | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| GateRouter     | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Gatewayz       | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| GeekAI         | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| GetGoAPI       | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Ghostbot       | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| GitHub         | ✅        | ✅                | ✅            | ✅            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Glama          | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Gladia         | ✅        | ❌                | ❌            | ❌            | ❌        | ➖                     | ✅                       | ➖               | ➖          | ➖          | ➖          |
| Glio           | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| GMICloud       | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Google         | ✅        | ✅                | ✅            | ✅            | ✅        | ✅                     | ✅                       | ✅               | ➖          | ✅          | ➖          |
| GoogleTranslate| ✅        | ❌                | 🟡            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| GooseAI        | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| GonkaGate      | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| GPTsAPI        | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| GPTProto       | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Gradium        | ✅        | ❌                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ✅               | ➖          | ➖          | ➖          |
| GreenPT        | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| GrooveDev      | ➖        | ➖                | ➖            | ➖            | ➖        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ✅          |
| Groq           | ✅        | ✅                | ✅            | ✅            | ✅        | ➖                     | ✅                       | ✅               | ➖          | ➖          | ➖          |
| GTranslate     | ✅        | ✅                | ✅            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Haimaker       | ✅        | ✅                | ❌            | ❌            | ❌        | ✅                     | ✅                       | ➖               | ➖          | ✅          | ➖          |
| Hanzo          | ✅        | ✅                | ❌            | ❌            | ❌        | ❌                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Helicone       | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| HeyGen         | ✅        | ❌                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ✅               | ➖          | ➖          | ➖          |
| Hicap          | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| HolySheepAI    | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| HorayAI        | ✅        | ✅                | ❌            | ❌            | ✅        | ✅                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| HuggingFace    | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Hyperbolic     | ✅        | ✅                | 🟡            | ❌            | 🟡        | ✅                     | ➖                       | ✅               | ➖          | ➖          | ➖          |
| Hyperstack     | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| iApp           | ✅        | ✅                | ❌            | ❌            | ❌        | ✅                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Ideogram       | ✅        | ❌                | ❌            | ❌            | ✅        | ✅                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| IGPT           | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| ImageRouter    | ✅        | ❌                | 🟡            | ❌            | ❌        | ✅                     | ➖                       | ➖               | ➖          | ✅          | ➖          |
| InceptionLabs  | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Infercom       | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Inferencenet   | ✅        | ❌                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Inferencesh    | ✅        | ✅                | ❌            | ❌            | ❌        | ❌                     | ➖                       | ❌               | ➖          | ❌          | ➖          |
| InferLink      | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Inflection     | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Infomaniak     | ✅        | ✅                | ❌            | ❌            | ❌        | ✅                     | ✅                       | ➖               | ✅          | ➖          | ➖          |
| Infraxa        | ✅        | ✅                | ❌            | ❌            | ❌        | ❌                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Infron         | ✅        | ✅                | ✅            | ✅            | ✅        | ❌                     | ➖                       | ➖               | ❌          | ❌          | ➖          |
| Inworld        | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ✅               | ➖          | ➖          | ➖          |
| IOnet          | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| IONOS          | ✅        | ✅                | ❌            | ❌            | ✅        | ✅                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Ishi           | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| IonRouter      | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| JassieAI       | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Jatevo         | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| JiekouAI       | ✅        | ✅                | ❌            | ❌            | ❌        | ❌                     | ➖                       | ➖               | ❌          | ❌          | ➖          |
| JigsawStack    | ✅        | ✅                | ✅            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Jina           | ✅        | ✅                | ✅            | ✅            | ✅        | ➖                     | ➖                       | ➖               | ✅          | ➖          | ➖          |
| JSON2Video     | ✅        | ❌                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ✅          | ➖          |
| JKAIHub        | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Kilo           | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Key4U          | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Keyplex        | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| KimiK2         | ✅        | ✅                | ❌            | ✅            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Kirha          | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| KittenStack    | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| KissAPI        | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| KnoxChat       | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| KlingAI        | ✅        | ❌                | 🟡            | ❌            | ✅        | ✅                     | ➖                       | ✅               | ➖          | ✅          | ➖          |
| Kugu           | ✅        | ❌                | 🟡            | ❌            | ✅        | ➖                     | ➖                       | ✅               | ➖          | ➖          | ➖          |
| LangDB         | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| LaoZhang       | ✅        | ✅                | ❌            | ❌            | ❌        | ❌                     | ❌                       | ❌               | ➖          | ❌          | ➖          |
| Lacesse        | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Lava           | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| LectoAI        | ✅        | ❌                | 🟡            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| LEAPERone      | ✅        | ✅                | ✅            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| LemonData      | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| LitAI          | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| LiteRouter     | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Lingvanex      | ✅        | ✅                | ✅            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ✅          | ➖          | ➖          |
| Lexi           | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Llama          | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| LLM7           | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| LLMAPI         | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| LLMCloud       | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| LLMGateway     | ✅        | ✅                | ❌            | ✅            | ✅        | ✅                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| LLMHubIFS      | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| LLMkiwi        | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| LLMLayer       | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| LogicosLLMHub  | ✅        | ✅                | ❌            | ❌            | ❌        | ❌                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| LLMWise        | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| LMRouter       | ✅        | ✅                | ✅            | ✅            | ❌        | ❌                     | ❌                       | ❌               | ➖          | ➖          | ➖          |
| LongCat        | ✅        | ✅                | ❌            | ✅            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| LOVO           | ✅        | ❌                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ✅               | ➖          | ➖          | ➖          |
| LumaAI         | ✅        | ❌                | ❌            | ❌            | ❌        | ✅                     | ➖                       | ➖               | ➖          | ✅          | ➖          |
| Lumecoder      | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Lumenfall      | ✅        | ✅                | ❌            | ❌            | ❌        | ✅                     | ➖                       | ➖               | ➖          | ✅          | ➖          |
| Lunos          | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| LXG2IT         | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Magisterium    | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| MancerAI       | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| MaritacaAI     | ✅        | ✅                | ✅            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Martian        | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| MatterAI       | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| MegaLLM        | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| MegaNova       | ✅        | ✅                | ❌            | ❌            | ✅        | ✅                     | ➖                       | ➖               | ❌          | ❌          | ➖          |
| MemoryRouter   | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Messari        | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Mia21          | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Microsoft      | ✅        | ✅                | ✅            | ✅            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| MIMICXAI       | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| MiniMax        | ✅        | ✅                | 🟡            | ❌            | 🟡        | ✅                     | ➖                       | ✅               | ➖          | ✅          | ➖          |
| Mistral        | ✅        | ✅                | ✅            | ✅            | ✅        | ✅                     | ✅                       | ❌               | ➖          | ➖          | ➖          |
| Modal          | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| ModelMax       | ✅        | ✅                | ❌            | ✅            | ❌        | ❌                     | ➖                       | ➖               | ➖          | ❌          | ➖          |
| ModelSync      | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| ModelBridge    | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| ModelRouter    | ✅        | ✅                | ✅            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| ModelsLab      | ✅        | ❌                | ❌            | ❌            | ✅        | ✅                     | ➖                       | ✅               | ➖          | ✅          | ➖          |
| ModernMT       | ✅        | ❌                | 🟡            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| MoleAPI        | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Moltkey        | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Monica         | ✅        | ✅                | ❌            | ❌            | ✅        | ✅                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Moonshot       | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Morph          | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Morpheus       | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ✅               | ➖          | ➖          | ➖          |
| MuleRun        | ✅        | ✅                | ✅            | ✅            | ❌        | ❌                     | ➖                       | ➖               | ➖          | ❌          | ➖          |
| MultiverseAI   | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| MumeAI         | ✅        | ✅                | ✅            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| MurfAI         | ✅        | ✅                | ✅            | ❌            | ✅        | ➖                     | ➖                       | ✅               | ➖          | ➖          | ➖          |
| NagaAI         | ✅        | ✅                | ✅            | ✅            | ❌        | ❌                     | ❌                       | ❌               | ➖          | ➖          | ➖          |
| NavyAI         | ✅        | ✅                | ❌            | ❌            | ❌        | ❌                     | ❌                       | ❌               | ➖          | ❌          | ➖          |
| NanoGPT        | ✅        | ✅                | ❌            | ❌            | ❌        | ❌                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Nataris        | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| NEARAI         | ✅        | ✅                | ❌            | ❌            | ❌        | ❌                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| NLPCloud       | ✅        | 🟡                | 🟡            | ❌            | 🟡        | ➖                     | ✅                       | ✅               | ➖          | ➖          | ➖          |
| NRPNautilus    | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Nscale         | ✅        | ✅                | ❌            | ❌            | ✅        | ✅                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Nebius         | ✅        | ✅                | ❌            | ❌            | ✅        | ✅                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| NebulaBlock    | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Neuralwatt     | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Neosantara     | ✅        | ✅                | ✅            | ❌            | ✅        | ❌                     | ❌                       | ➖               | ➖          | ➖          | ➖          |
| NetMind        | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Nextbit        | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Nexusify       | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| NinjaChat      | ✅        | ✅                | ❌            | ❌            | ❌        | ❌                     | ➖                       | ➖               | ➖          | ❌          | ➖          |
| Noiz           | ✅        | ❌                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ✅               | ➖          | ➖          | ➖          |
| NVIDIA         | ✅        | ✅                | ✅            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Nodebyt        | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| NONKYCAI       | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| NousResearch   | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Nouswise       | ✅        | ✅                | ✅            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| NovAI          | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Novita         | ✅        | ✅                | 🟡            | ❌            | 🟡        | ✅                     | ✅                       | ✅               | ✅          | ➖          | ➖          |
| OCRSkill       | ✅        | ✅                | ✅            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Octagon        | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| OfoxAI         | ✅        | ✅                | ❌            | ❌            | ❌        | ✅                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| OhMyGPT        | ✅        | ✅                | ❌            | ❌            | ❌        | ❌                     | ❌                       | ❌               | ➖          | ➖          | ➖          |
| Ollama         | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| OmniaKey       | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| OneInfer       | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| OneKey         | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| OODAAI         | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| OPEAI          | ✅        | ✅                | ❌            | ❌            | ❌        | ❌                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| OpenAI         | ✅        | ✅                | ✅            | ✅            | ✅        | ✅                     | ✅                       | ✅               | ➖          | ✅          | ✅          |
| OpenCode       | ✅        | ✅                | ✅            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| OpenGateway    | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| OpenLimits     | ✅        | ✅                | ✅            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| OpenPipe       | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| OpenRouter     | ✅        | ✅                | ✅            | ✅            | ✅        | ❌                     | ❌                       | ➖               | ➖          | ➖          | ➖          |
| OpenSourceAIHub| ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| OpperAI        | ✅        | ✅                | ✅            | ✅            | ❌        | ➖                     | ➖                       | ➖               | ✅          | ➖          | ➖          |
| OpusCode       | ✅        | ❌                | ❌            | ✅            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Oraicle        | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| OrbGPU         | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| OrqAgentRuntime| ✅        | ✅                | ✅            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| OrqRouter      | ✅        | ✅                | ✅            | ❌            | ✅        | ❌                     | ❌                       | ❌               | ❌          | ➖          | ➖          |
| OVHcloud       | ✅        | ✅                | ❌            | ❌            | 🟡        | ✅                     | ✅                       | ✅               | ➖          | ➖          | ➖          |
| OXOAPI         | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| PacketAI       | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Parallel       | ✅        | ✅                | ✅            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| ParalonCloud   | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Parasail       | ✅        | ✅                | ❌            | ❌            | ✅        | ✅                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Paul           | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| PayPerQ        | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Pellet         | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Perceptron     | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Perplexity     | ✅        | ✅                | ✅            | ✅            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| PiAPI          | ✅        | ✅                | ❌            | ❌            | ❌        | ❌                     | ➖                       | ➖               | ➖          | ❌          | ➖          |
| Picsart        | ✅        | ❌                | ❌            | ❌            | ✅        | 🟡                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Pinecone       | ❌        | ❌                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ✅          | ➖          | ➖          |
| PixelDojo      | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| PixCode        | ✅        | ✅                | ✅            | ❌            | ❌        | ➖                     | ➖                       | ✅               | ➖          | ✅          | ➖          |
| PixIA          | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Poe            | ✅        | ✅                | ✅            | ✅            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Pollinations   | ✅        | ❌                | ❌            | ❌            | ✅        | ✅                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Portkey        | ✅        | ✅                | ✅            | ❌            | ✅        | ✅                     | ✅                       | ✅               | ➖          | ➖          | ➖          |
| PreAPI         | ❌        | ❌                | ❌            | ❌            | ❌        | ✅                     | ➖                       | ✅               | ➖          | ✅          | ➖          |
| Puter          | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Prakasa        | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| PrimeIntellect | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Privatemode    | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ✅                       | ➖               | ➖          | ➖          | ➖          |
| PublicAI       | ✅        | ✅                | ✅            | ✅            | ✅        | ➖                     | ➖                       | ➖               | ❌          | ➖          | ➖          |
| Qiniu          | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| QuiverAI       | ✅        | ✅                | ✅            | ❌            | ✅        | ✅                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Radiance       | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Radient        | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Railwail       | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| RaxAI          | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Recraft        | ✅        | ❌                | ❌            | ❌            | ✅        | ✅                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| RedPill        | ✅        | ✅                | ❌            | ❌            | ❌        | ✅                     | ❌                       | ❌               | ➖          | ➖          | ➖          |
| ReGraph        | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| RegoloAI       | ✅        | ✅                | ❌            | ❌            | ❌        | ✅                     | ✅                       | ➖               | ✅          | ➖          | ➖          |
| RekaAI         | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ✅                       | ➖               | ➖          | ➖          | ➖          |
| Relace         | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| RelaxAI        | ✅        | ✅                | ❌            | ✅            | ❌        | ➖                     | ✅                       | ➖               | ➖          | ➖          | ➖          |
| Renderful      | ❌        | ❌                | ❌            | ❌            | ❌        | ❌                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Replicate      | ✅        | ❌                | ❌            | ❌            | ❌        | ✅                     | ✅                       | ✅               | ➖          | ➖          | ➖          |
| Requesty       | ✅        | ✅                | ❌            | ✅            | ✅        | ✅                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| ResembleAI     | ✅        | ❌                | 🟡            | ❌            | ❌        | ➖                     | ✅                       | ✅               | ➖          | ➖          | ➖          |
| Reve           | ✅        | ❌                | ❌            | ❌            | ✅        | ✅                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Rime           | ✅        | ❌                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ✅               | ➖          | ➖          | ➖          |
| RoutePlex      | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Routeway       | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Routstr        | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Routmy         | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| RunAPI         | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Runpod         | ✅        | ✅                | ❌            | ❌            | 🟡        | ✅                     | ❌                       | ✅               | ✅          | ➖          | ➖          |
| Runtimo        | ✅        | ❌                | ❌            | ❌            | ❌        | ✅                     | ➖                       | ➖               | ➖          | ✅          | ➖          |
| Runware        | ✅        | ❌                | ❌            | ❌            | ✅        | ✅                     | ➖                       | ➖               | ➖          | ✅          | ➖          |
| Runway         | ✅        | ❌                | ❌            | ❌            | 🟡        | ✅                     | ➖                       | ✅               | ➖          | ✅          | ➖          |
| SambaNova      | ✅        | ✅                | ❌            | ❌            | 🟡        | ➖                     | ✅                       | ➖               | ➖          | ➖          | ➖          |
| Sapiom         | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Sargalay       | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Sarvam         | ✅        | ✅                | 🟡            | ❌            | 🟡        | ➖                     | ✅                       | ✅               | ➖          | ➖          | ➖          |
| SawtIA         | ✅        | ❌                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ✅               | ➖          | ➖          | ➖          |
| Scaleway       | ✅        | ✅                | ✅            | ✅            | ✅        | ➖                     | ✅                       | ➖               | ✅          | ➖          | ➖          |
| SchatziAI      | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| SelinaAI       | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Setapp         | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Shakespeare    | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| ShannonAI      | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Shengsuanyun   | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| ShuttleAI      | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| SiliconFlow    | ✅        | ✅                | 🟡            | ❌            | 🟡        | ✅                     | ✅                       | ✅               | ✅          | ✅          | ➖          |
| SimpleLLM      | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Simplismart    | ✅        | ✅                | ❌            | ❌            | ❌        | ❌                     | ❌                       | ➖               | ➖          | ➖          | ➖          |
| SEALION        | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Segmind        | ✅        | ❌                | ❌            | ❌            | ✅        | ✅                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| SkillBoss      | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| SmallestAI     | ✅        | 🟡                | 🟡            | ❌            | 🟡        | ➖                     | ✅                       | ✅               | ➖          | ➖          | ➖          |
| SmartAIPI      | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Smooth         | ✅        | ✅                | ✅            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Speechactors   | ✅        | ❌                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ✅               | ➖          | ➖          | ➖          |
| Speechify      | ✅        | ❌                | 🟡            | ❌            | ✅        | ➖                     | ➖                       | ✅               | ➖          | ➖          | ➖          |
| Speechmatics   | ✅        | ❌                | ❌            | ❌            | 🟡        | ➖                     | ✅                       | ✅               | ➖          | ➖          | ➖          |
| StabilityAI    | ✅        | ❌                | 🟡            | ❌            | ✅        | ✅                     | ➖                       | ✅               | ➖          | ➖          | ➖          |
| StealthGPT     | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| StepFun        | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ✅               | ➖          | ➖          | ➖          |
| Straico        | ✅        | ✅                | ❌            | ❌            | ❌        | ✅                     | ➖                       | ✅               | ➖          | ➖          | ➖          |
| StreamLake     | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| SurferCloud    | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| SudoRouter     | ✅        | ✅                | ✅            | ✅            | ❌        | ❌                     | ➖                       | ➖               | ➖          | ❌          | ➖          |
| SunoAPI        | ✅        | ❌                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ✅               | ➖          | ➖          | ➖          |
| SUPA           | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| SUFY           | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Supertone      | ✅        | ❌                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ✅               | ➖          | ➖          | ➖          |
| Swarms         | ✅        | ✅                | ✅            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| SwitchpointAI  | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Syllogy        | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Synexa         | ✅        | ✅                | ✅            | ❌            | ✅        | ✅                     | ✅                       | ➖               | ➖          | ✅          | ➖          |
| Synthetic      | ✅        | ✅                | ❌            | ✅            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Tapas          | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Tavily         | ✅        | ✅                | ✅            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| TEAI           | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| TeamDay        | ✅        | ✅                | ✅            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Telnyx         | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ✅                       | ➖               | ➖          | ➖          | ➖          |
| TencentHunyuan | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| TensorBlock    | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Tensorix       | ✅        | ✅                | ❌            | ✅            | ❌        | ➖                     | ✅                       | ✅               | ➖          | ➖          | ➖          |
| Tetrate        | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Thaura         | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| TheRouterAI    | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| TheOldAPI      | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| TextSynth      | ✅        | ✅                | ❌            | ❌            | ❌        | ✅                     | ✅                       | ✅               | ➖          | ➖          | ➖          |
| TigerCity      | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| TikHubAI       | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Tinfoil        | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| ToAPIs         | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Together       | ✅        | ✅                | ✅            | ✅            | ✅        | ✅                     | ✅                       | ✅               | ✅          | ✅          | ➖          |
| TokenFlux      | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| TrueFoundry    | ✅        | ✅                | ✅            | ❌            | ❌        | ❌                     | ❌                       | ❌               | ❌          | ➖          | ➖          |
| TTSReader      | ✅        | ❌                | ✅            | ❌            | ✅        | ➖                     | ➖                       | ✅               | ➖          | ➖          | ➖          |
| Typecast       | ✅        | ❌                | 🟡            | ❌            | ✅        | ➖                     | ➖                       | ✅               | ➖          | ➖          | ➖          |
| UniAPI         | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| UltraSafe      | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| UncensoredChat | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| UncloseAI      | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| UnrealSpeech   | ✅        | ❌                | 🟡            | ❌            | ✅        | ➖                     | ➖                       | ✅               | ➖          | ➖          | ➖          |
| Unbound        | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Upstage        | ✅        | ✅                | ❌            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| UplinkAPI      | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| UVoiceAI       | ✅        | ❌                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ✅               | ➖          | ➖          | ➖          |
| Valyu          | ✅        | ✅                | ✅            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Vapi           | ✅        | 🟡                | 🟡            | ❌            | 🟡        | ➖                     | ❌                       | ❌               | ➖          | ➖          | ➖          |
| Venice         | ✅        | ✅                | ❌            | ❌            | 🟡        | ✅                     | ✅                       | ✅               | ➖          | ✅          | ➖          |
| Verbatik       | ✅        | ❌                | 🟡            | ❌            | ✅        | ➖                     | ➖                       | ✅               | ➖          | ➖          | ➖          |
| Verda          | ✅        | ❌                | ❌            | ❌            | ✅        | ✅                     | ✅                       | ➖               | ➖          | ➖          | ➖          |
| VIABLELab      | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| VLMRun         | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| VibeCodeCheap  | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| VibeKit        | ✅        | ✅                | ✅            | ✅            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Vidu           | ✅        | ❌                | ❌            | ❌            | ✅        | ✅                     | ➖                       | ✅               | ➖          | ✅          | ➖          |
| Vivgrid        | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| VoiceAI        | ✅        | ❌                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ✅               | ➖          | ➖          | ➖          |
| VoidAI         | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Vogent         | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| VoyageAI       | ❌        | ❌                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ✅          | ➖          | ➖          |
| Vultr          | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| WAI            | ✅        | ✅                | ✅            | ❌            | ✅        | ✅                     | ➖                       | ➖               | ➖          | ❌          | ➖          |
| WebsearchAPI   | ✅        | ✅                | ✅            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| WesenAI        | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ❌                       | ❌               | ➖          | ➖          | ➖          |
| WidnAI         | ✅        | ✅                | 🟡            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| WisdomGate     | ✅        | ✅                | ❌            | ❌            | ❌        | ✅                     | ➖                       | ➖               | ➖          | ✅          | ➖          |
| WiseRouter     | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| World3         | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Writer         | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| xAI            | ✅        | ✅                | ✅            | ✅            | ✅        | ✅                     | ✅                       | ✅               | ➖          | ✅          | ➖          |
| XiaomiMIMO     | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Yollomi        | ✅        | ❌                | ❌            | ❌            | ❌        | ❌                     | ➖                       | ➖               | ➖          | ❌          | ➖          |
| YouCom         | ✅        | ✅                | ✅            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| YouGetAI       | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| YourVoic       | ✅        | ❌                | 🟡            | ❌            | 🟡        | ➖                     | ✅                       | ✅               | ➖          | ➖          | ➖          |
| YYClaw         | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Zai            | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ✅                       | ➖               | ➖          | ✅          | ➖          |
| Zeabur         | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Zenlayer       | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| ZenMux         | ✅        | ✅                | ✅            | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| ZyloAPI        | ✅        | ✅                | ❌            | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          | ➖          |
| Zyphra         | ✅        | ❌                | 🟡            | ❌            | ✅        | ➖                     | ➖                       | ✅               | ➖          | ➖          | ➖          |

## Run locally

### Prerequisites

- **.NET 10 SDK**

### Run HeaderAuth sample

```bash
dotnet run --project Samples/AIHappey.HeaderAuth/AIHappey.HeaderAuth.csproj
```

### Run AzureAuth sample

```bash
dotnet run --project Samples/AIHappey.AzureAuth/AIHappey.AzureAuth.csproj
```

### Example requests

Set a base URL and API key once:

```bash
BASE_URL="https://ai.aihappey.net"
API_KEY="<your-key>"
```

#### POST /api/chat (AI SDK UI stream)

Minimal text message (UI message parts):

```bash
curl "$BASE_URL/api/chat" \
  -H "Content-Type: application/json" \
  -H "X-OpenAI-Key: $API_KEY" \
  -d '{
    "id": "chat-1",
    "model": "openai/gpt-4o-mini",
    "messages": [
      {
        "id": "msg-1",
        "role": "user",
        "parts": [
          {"type": "text", "text": "Hello from AIHappey"}
        ]
      }
    ]
  }'
```

Tool call-capable request (tool schema + toolChoice):

```bash
curl "$BASE_URL/api/chat" \
  -H "Content-Type: application/json" \
  -H "X-OpenAI-Key: $API_KEY" \
  -d '{
    "id": "chat-2",
    "model": "openai/gpt-4o-mini",
    "toolChoice": "auto",
    "maxToolCalls": 1,
    "tools": [
      {
        "name": "get_weather",
        "description": "Get the current weather for a city",
        "inputSchema": {
          "type": "object",
          "properties": {"city": {"type": "string"}},
          "required": ["city"]
        }
      }
    ],
    "messages": [
      {
        "id": "msg-2",
        "role": "user",
        "parts": [
          {"type": "text", "text": "What is the weather in Amsterdam?"}
        ]
      }
    ]
  }'
```

#### POST /v1/chat/completions (OpenAI-compatible)

Non-streaming:

```bash
curl "$BASE_URL/v1/chat/completions" \
  -H "Content-Type: application/json" \
  -H "X-OpenAI-Key: $API_KEY" \
  -d '{
    "model": "openai/gpt-4o-mini",
    "messages": [
      {"role": "user", "content": "Say hi"}
    ]
  }'
```

Streaming:

```bash
curl "$BASE_URL/v1/chat/completions" \
  -H "Content-Type: application/json" \
  -H "X-OpenAI-Key: $API_KEY" \
  -d '{
    "model": "openai/gpt-4o-mini",
    "stream": true,
    "messages": [
      {"role": "user", "content": "Stream a short response"}
    ]
  }'
```

#### POST /v1/responses (OpenAI-compatible)

Non-streaming:

```bash
curl "$BASE_URL/v1/responses" \
  -H "Content-Type: application/json" \
  -H "X-OpenAI-Key: $API_KEY" \
  -d '{
    "model": "openai/gpt-4o-mini",
    "input": "List 3 creative project names"
  }'
```

Streaming:

```bash
curl "$BASE_URL/v1/responses" \
  -H "Content-Type: application/json" \
  -H "X-OpenAI-Key: $API_KEY" \
  -d '{
    "model": "openai/gpt-4o-mini",
    "stream": true,
    "input": "Stream a 2-sentence summary about AIHappey"
  }'
```

#### GET /v1/models

List models

```bash
curl "$BASE_URL/v1/models" \
  -H "X-OpenAI-Key: $API_KEY"
```

#### GET /v1/skills

List skills

```bash
curl "$BASE_URL/v1/skills" \
  -H "X-OpenAI-Key: $API_KEY"
```

#### POST /api/rerank

Use a reranking-capable model (example uses Cohere):

```bash
curl "$BASE_URL/api/rerank" \
  -H "Content-Type: application/json" \
  -H "X-Cohere-Key: $API_KEY" \
  -d '{
    "model": "cohere/rerank-english-v3.0",
    "query": "best pizza in Amsterdam",
    "topN": 3,
    "documents": {
      "type": "text",
      "values": [
        "Try authentic Neapolitan pizza downtown.",
        "A cozy spot with wood-fired crusts.",
        "Grab a quick slice near the station."
      ]
    }
  }'
```

#### POST /v1/images/generations

```bash
curl "$BASE_URL/v1/images/generations" \
  -H "Content-Type: application/json" \
  -H "X-OpenAI-Key: $API_KEY" \
  -d '{
    "model": "openai/gpt-image-1",
    "prompt": "A minimal studio apartment in Scandinavian style",
    "size": "1024x1024",
    "n": 1
  }'
```

#### POST /v1/audio/speech

```bash
curl "$BASE_URL/v1/audio/speech" \
  -H "Content-Type: application/json" \
  -H "X-OpenAI-Key: $API_KEY" \
  -d '{
    "model": "openai/tts-1",
    "voice": "alloy",
    "outputFormat": "mp3",
    "text": "AIHappey makes it easy to route across providers."
  }' > speech.json

jq -r '.audio.base64' speech.json | base64 --decode > speech.mp3
```

#### POST /v1/audio/transcriptions

```bash
curl "$BASE_URL/v1/audio/transcriptions" \
  -H "Content-Type: application/json" \
  -H "X-OpenAI-Key: $API_KEY" \
  -d '{
    "model": "openai/whisper-1",
    "mediaType": "audio/mpeg",
    "audio": "data:audio/mpeg;base64,<base64-audio>"
  }'
```

#### POST /sampling (Model Context Protocol)

```bash
curl "$BASE_URL/sampling" \
  -H "Content-Type: application/json" \
  -H "X-OpenAI-Key: $API_KEY" \
  -d '{
    "messages": [
      {"role": "user", "content": "Give me a one-line summary."}
    ],
    "modelPreferences": {
      "hints": [
        {"name": "openai/gpt-4o-mini"}
      ]
    }
  }'
```

#### POST /v1/realtime/client_secrets

```bash
curl "$BASE_URL/v1/realtime/client_secrets" \
  -H "Content-Type: application/json" \
  -H "X-OpenAI-Key: $API_KEY" \
  -d '{
    "model": "openai/gpt-4o-realtime-preview",
    "providerOptions": {
      "openai": {
        "session": {
          "instructions": "You are a concise assistant."
        }
      }
    }
  }'
```

#### POST /v1/videos

```bash
curl "$BASE_URL/v1/videos" \
  -H "Content-Type: application/json" \
  -H "X-OpenAI-Key: $API_KEY" \
  -d '{
    "model": "openai/sora-2",
    "prompt": "Drone shot in a minimal studio apartment in Scandinavian style",
    "resolution": "720x1280",
    "n": 1
  }'
```

## Core MCP servers (Model Context Protocol)

aihappey-ai exposes a set of **core MCP servers** (streamable HTTP) that give MCP clients serious power: discover models/providers, generate media, rerank content and mint realtime tokens.

Discovery (recommended):

- **MCP registry**: `GET $BASE_URL/v0.1/servers`

Core MCP server URLs (use the same `$BASE_URL` as above):

- **AI Models** — `POST $BASE_URL/ai-models` — Tools: `ai_models_list`
- **AI Providers** — `POST $BASE_URL/ai-providers` — Tools: `ai_provider_metadata_get_schema`, `ai_providers_list`, `ai_provider_get_models`
- **AI Images** — `POST $BASE_URL/ai-images` — Tools: `ai_images_generate`
- **AI Speech** — `POST $BASE_URL/ai-speech` — Tools: `ai_speech_generate`
- **AI Transcriptions** — `POST $BASE_URL/ai-transcriptions` — Tools: `ai_audio_transcriptions_create`
- **AI Realtime** — `POST $BASE_URL/ai-realtime` — Tools: `ai_realtime_token_get`
- **AI Rerank** — `POST $BASE_URL/ai-rerank` — Tools: `ai_rerank_texts`, `ai_rerank_urls`
