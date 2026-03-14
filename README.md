# aihappey-ai

A multi-provider **.NET AI inference gateway** exposing key AI endpoints.

No smart routing fairy. No shiny admin portal. No hidden logic maze.
Just providers flattened to capabilities, normalized hard, exposed through open contracts.
Stateless. Boring on purpose.

Access 140k+ models from your favorite client.

## Provider Support Matrix

The table below shows which endpoints each provider implements (✅), not yet implemented (❌), partially implemented (🟡) or for which an endpoint is not applicable to the provider (➖).

| Provider       | [Chat](https://ai-sdk.dev/docs/reference/ai-sdk-ui/use-chat) | [Completions](https://platform.openai.com/docs/api-reference/chat) | [Responses](https://platform.openai.com/docs/api-reference/responses) | [Sampling](https://modelcontextprotocol.io/specification/draft/client/sampling) | [Images](https://ai-sdk.dev/docs/ai-sdk-core/image-generation) | [Transcriptions](https://ai-sdk.dev/docs/ai-sdk-core/transcription) | [Speech](https://ai-sdk.dev/docs/ai-sdk-core/speech) | [Rerank](https://ai-sdk.dev/docs/ai-sdk-core/reranking) | Video |
| -------------- | --------- | ----------------- | ------------- | --------- | ---------------------- | ------------------------ | ---------------- | ----------- | ----------- |
| 302AI          | ✅        | ✅                | ❌            | 🟡        | ✅                     | ✅                       | ✅               | ❌          | ✅          |
| Abliberation   | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Aether         | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Agabeyogluai   | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| AgentAIGateway | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Agentics       | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| AI21           | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| AIBadgr        | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| AICredits      | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| AIBramha       | ✅        | ✅                | ❌            | ❌        | ❌                     | ➖                       | ➖               | ➖          | ➖          |
| AICC           | ✅        | ✅                | ❌            | ❌        | ✅                     | ➖                       | ❌               | ➖          | ✅          |
| AINative       | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| AIForHire      | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| AIHubMix       | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| AIRouter       | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| AISieuRe       | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| AIML           | ✅        | ❌                | 🟡            | 🟡        | ✅                     | ✅                       | ✅               | ➖          | ➖          |
| AIsa           | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| AiMo           | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| AionLabs       | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| AKI            | ✅        | ✅                | ❌            | ✅        | ✅                     | ➖                       | ➖               | ➖          | ➖          |
| Alibaba        | ✅        | ✅                | ✅            | ✅        | ✅                     | ✅                       | ➖               | ➖          | ✅          |
| AlphaNeural    | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| AmazonBedrock  | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Anannas        | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Answira        | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Anthropic      | ✅        | ❌                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Apertis        | ✅        | ✅                | ❌            | ❌        | ❌                     | ➖                       | ❌               | ➖          | ❌          |
| AndyAPI        | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| ApiAirforce    | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| APIFree        | ✅        | ✅                | ❌            | ❌        | ❌                     | ➖                       | ❌               | ➖          | ❌          |
| APIpie         | ✅        | ✅                | ❌            | 🟡        | ✅                     | ✅                       | ✅               | ➖          | ❌          |
| APIPod         | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| APIyi          | ✅        | ✅                | ❌            | ❌        | ❌                     | ❌                       | ❌               | ❌          | ❌          |
| Apekey         | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| ArceeAI        | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| ARKLabs        | ✅        | ✅                | ❌            | ✅        | ✅                     | ✅                       | ✅               | ➖          | ➖          |
| ArliAI         | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| ARWriter       | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| ASIOne         | ✅        | ❌                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| AskARC         | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| AskCodi        | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| AssemblyAI     | ✅        | ✅                | ❌            | 🟡        | ➖                     | ✅                       | ➖               | ➖          | ➖          |
| Assisters      | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Astica         | ✅        | ❌                | ❌            | ❌        | ✅                     | ➖                       | ✅               | ➖          | ➖          |
| AsyncAI        | ✅        | ❌                | ✅            | ✅        | ➖                     | ➖                       | ✅               | ➖          | ➖          |
| AtlasCloud     | ✅        | ✅                | ❌            | 🟡        | ✅                     | ➖                       | ➖               | ➖          | ✅          |
| ATXP           | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Audixa         | ✅        | ❌                | ❌            | ✅        | ➖                     | ➖                       | ✅               | ➖          | ➖          |
| Avian          | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Azerion        | ✅        | ✅                | ❌            | 🟡        | ✅                     | ➖                       | ➖               | ➖          | ✅          |
| Azure          | ✅        | 🟡                | 🟡            | 🟡        | ➖                     | ✅                       | ✅               | ➖          | ➖          |
| BazaarLink     | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Baseten        | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| BergetAI       | ✅        | ✅                | ❌            | 🟡        | ➖                     | ✅                       | ➖               | ✅          | ➖          |
| Bineric        | ✅        | ✅                | ❌            | 🟡        | ➖                     | ❌                       | ✅               | ➖          | ➖          |
| Blackbox       | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| BlackForestLabs| ✅        | ❌                | ❌            | ✅        | ✅                     | ➖                       | ➖               | ➖          | ➖          |
| BlazeRail      | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| BlockRun       | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Bria           | ✅        | ❌                | ❌            | ✅        | ✅                     | ➖                       | ➖               | ➖          | ➖          |
| BrowserUse     | ✅        | ✅                | ✅            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Brainiall      | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| BytePlus       | ✅        | ✅                | ✅            | 🟡        | ✅                     | ➖                       | ➖               | ➖          | ✅          |
| Bytez          | ✅        | ✅                | ❌            | ✅        | ✅                     | ➖                       | ✅               | ➖          | ✅          |
| CAMBAI         | ✅        | ✅                | ✅            | ✅        | ➖                     | ✅                       | ✅               | ➖          | ➖          |
| CanopyWave     | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Cartesia       | ✅        | ❌                | ❌            | 🟡        | ➖                     | ✅                       | ✅               | ➖          | ➖          |
| CaseDev        | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Cerebras       | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| ChainGPT       | ✅        | ✅                | ❌            | ✅        | ❌                     | ➖                       | ➖               | ➖          | ➖          |
| ChainHub       | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| CheapestInf... | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Cirrascale     | ✅        | ✅                | 🟡            | 🟡        | ✅                     | ➖                       | ➖               | ✅          | ➖          |
| Clod           | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Claudible      | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Cline          | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| ClawSwitch     | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| CloudFerro     | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| CloudRift      | ✅        | ❌                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Cohere         | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ✅          | ➖          |
| CometAPI       | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ✅               | ➖          | ➖          |
| ContextualAI   | ❌        | ❌                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ✅          | ➖          |
| Cortecs        | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Corriente      | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Crazyrouter    | ✅        | ✅                | ❌            | ❌        | ❌                     | ❌                       | ❌               | ❌          | ❌          |
| Daglo          | ✅        | ✅                | ❌            | ❌        | ➖                     | ✅                       | ✅               | ➖          | ➖          |
| Dandolo        | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Databricks     | ✅        | ✅                | ✅            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| DataForSEO     | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| DreamGen       | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| DeAPI          | ✅        | ❌                | ❌            | 🟡        | ✅                     | ✅                       | ✅               | ➖          | ✅          |
| Decart         | ✅        | ❌                | ❌            | 🟡        | ✅                     | ➖                       | ➖               | ➖          | ✅          |
| DedalusLabs    | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Deepbricks     | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| DeepInfra      | ✅        | ✅                | 🟡            | 🟡        | ✅                     | ✅                       | ✅               | ✅          | ➖          |
| DeepL          | ✅        | ✅                | ✅            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| DeepSeek       | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Deepgram       | ✅        | ❌                | 🟡            | 🟡        | ➖                     | ✅                       | ✅               | ➖          | ➖          |
| DigitalOcean   | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| DocsRouter     | ✅        | ✅                | ✅            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Dubrify        | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| EAGM           | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Eachlabs       | ❌        | ❌                | ❌            | ❌        | ❌                     | ➖                       | ➖               | ➖          | ❌          |
| Edgee          | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Echo           | ✅        | ❌                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| EdenAI         | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Eliza          | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| EmbraceableAI  | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| ElectronHub    | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| ElkAPI         | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| ElevenLabs     | ✅        | ❌                | 🟡            | 🟡        | ➖                     | ✅                       | ✅               | ➖          | ➖          |
| Euqai          | ✅        | ✅                | ❌            | ✅        | ✅                     | ➖                       | ➖               | ➖          | ➖          |
| EUrouter       | ✅        | ✅                | ✅            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| EzAI           | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| EverypixelLabs | ✅        | ❌                | ❌            | ❌        | ➖                     | ➖                       | ✅               | ➖          | ➖          |
| EvoLinkAI      | ✅        | ✅                | ❌            | ❌        | ❌                     | ❌                       | ❌               | ➖          | ❌          |
| Exa            | ✅        | ✅                | ✅            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Featherless    | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| FishAudio      | ✅        | ❌                | ❌            | ❌        | ➖                     | ✅                       | ✅               | ➖          | ➖          |
| Fireworks      | ✅        | ✅                | ✅            | 🟡        | ✅                     | ✅                       | ➖               | ✅          | ➖          |
| Fal            | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Forefront      | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Freepik        | ✅        | ❌                | ❌            | ✅        | ✅                     | ➖                       | ✅               | ➖          | ✅          |
| FreedomGPT     | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Friendli       | ✅        | ❌                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| FullAI         | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| FastRouter     | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Ghostbot       | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| GetGoAPI       | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| GitHub         | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Glama          | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Gladia         | ✅        | ❌                | ❌            | ❌        | ➖                     | ✅                       | ➖               | ➖          | ➖          |
| Glio           | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| GMICloud       | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Google         | ✅        | ❌                | ❌            | ✅        | ✅                     | ✅                       | ✅               | ➖          | ✅          |
| GoogleTranslate| ✅        | ❌                | 🟡            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| GooseAI        | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| GonkaGate      | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| GPTProto       | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Gradium        | ✅        | ❌                | ❌            | ❌        | ➖                     | ➖                       | ✅               | ➖          | ➖          |
| Groq           | ✅        | ❌                | ❌            | ✅        | ➖                     | ✅                       | ✅               | ➖          | ➖          |
| GTranslate     | ✅        | ✅                | ✅            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Haimaker       | ✅        | ✅                | ❌            | ❌        | ✅                     | ✅                       | ➖               | ➖          | ✅          |
| Hanzo          | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Helicone       | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| HeyGen         | ✅        | ❌                | ❌            | ❌        | ➖                     | ➖                       | ✅               | ➖          | ➖          |
| Hicap          | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| HorayAI        | ✅        | ✅                | ❌            | ✅        | ✅                     | ➖                       | ➖               | ➖          | ➖          |
| HuggingFace    | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Hyperbolic     | ✅        | ✅                | 🟡            | 🟡        | ✅                     | ➖                       | ✅               | ➖          | ➖          |
| Hyperstack     | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Ideogram       | ✅        | ❌                | ❌            | ✅        | ✅                     | ➖                       | ➖               | ➖          | ➖          |
| InceptionLabs  | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Inferencenet   | ✅        | ❌                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Inferencesh    | ✅        | ✅                | ❌            | ❌        | ❌                     | ➖                       | ❌               | ➖          | ❌          |
| InferLink      | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Infomaniak     | ✅        | ✅                | ❌            | ❌        | ✅                     | ✅                       | ➖               | ✅          | ➖          |
| Infraxa        | ✅        | ✅                | ❌            | ❌        | ❌                     | ➖                       | ➖               | ➖          | ➖          |
| Infron         | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Inworld        | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ✅               | ➖          | ➖          |
| IOnet          | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| IONOS          | ✅        | ✅                | ❌            | ✅        | ✅                     | ➖                       | ➖               | ➖          | ➖          |
| Ishi           | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| IonRouter      | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Jatevo         | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| JiekouAI       | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| JigsawStack    | ✅        | ✅                | ✅            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Jina           | ✅        | ❌                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ✅          | ➖          |
| JSON2Video     | ✅        | ❌                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ✅          |
| JKAIHub        | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Kilo           | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Keyplex        | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| KimiK2         | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Kirha          | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| KittenStack    | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| KissAPI        | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| KnoxChat       | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| KlingAI        | ✅        | ❌                | 🟡            | ✅        | ✅                     | ➖                       | ✅               | ➖          | ✅          |
| Kugu           | ✅        | ❌                | 🟡            | ✅        | ➖                     | ➖                       | ✅               | ➖          | ➖          |
| LaoZhang       | ✅        | ✅                | ❌            | ❌        | ❌                     | ❌                       | ❌               | ➖          | ❌          |
| Lava           | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| LectoAI        | ✅        | ❌                | 🟡            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| LemonData      | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| LiteRouter     | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Lingvanex      | ✅        | ✅                | ✅            | ✅        | ➖                     | ➖                       | ➖               | ✅          | ➖          |
| Lexi           | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| LLM7           | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| LLMAPI         | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| LLMCloud       | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| LLMGateway     | ✅        | ✅                | ❌            | ✅        | ✅                     | ➖                       | ➖               | ➖          | ➖          |
| LLMHubIFS      | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| LLMkiwi        | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| LLMLayer       | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| LogicosLLMHub  | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| LLMPath        | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| LLMWise        | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| LongCat        | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| LOVO           | ✅        | ❌                | ❌            | ❌        | ➖                     | ➖                       | ✅               | ➖          | ➖          |
| LumaAI         | ✅        | ❌                | ❌            | ❌        | ✅                     | ➖                       | ➖               | ➖          | ✅          |
| Lumenfall      | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Lunos          | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| MancerAI       | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Mangaba        | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| MaritacaAI     | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| MatterAI       | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| MaximoAI       | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| MegaLLM        | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| MegaNova       | ✅        | ✅                | ❌            | ✅        | ✅                     | ➖                       | ➖               | ❌          | ❌          |
| Mia21          | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Mistral        | ✅        | ❌                | ❌            | ✅        | ✅                     | ✅                       | ➖               | ➖          | ➖          |
| MiniMax        | ✅        | ✅                | 🟡            | 🟡        | ✅                     | ➖                       | ✅               | ➖          | ✅          |
| Modal          | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| ModelMax       | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| ModelSync      | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| ModelRouter    | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| ModelsLab      | ✅        | ❌                | ❌            | ✅        | ✅                     | ➖                       | ✅               | ➖          | ✅          |
| ModernMT       | ✅        | ❌                | 🟡            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Monica         | ✅        | ✅                | ❌            | ✅        | ✅                     | ➖                       | ➖               | ➖          | ➖          |
| Moonshot       | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Morpheus       | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ✅               | ➖          | ➖          |
| MultiverseAI   | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| MurfAI         | ✅        | ✅                | ✅            | ✅        | ➖                     | ➖                       | ✅               | ➖          | ➖          |
| NagaAI         | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| NavyAI         | ✅        | ✅                | ❌            | ❌        | ❌                     | ❌                       | ❌               | ➖          | ❌          |
| NanoGPT        | ✅        | ✅                | ❌            | ❌        | ❌                     | ➖                       | ➖               | ➖          | ➖          |
| NEARAI         | ✅        | ✅                | ❌            | ❌        | ❌                     | ➖                       | ➖               | ➖          | ➖          |
| NLPCloud       | ✅        | 🟡                | 🟡            | 🟡        | ➖                     | ✅                       | ✅               | ➖          | ➖          |
| NRPNautilus    | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Nscale         | ✅        | ✅                | ❌            | ✅        | ✅                     | ➖                       | ➖               | ➖          | ➖          |
| Nebius         | ✅        | ✅                | ❌            | ✅        | ✅                     | ➖                       | ➖               | ➖          | ➖          |
| Neuralwatt     | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Neosantara     | ✅        | ✅                | ✅            | ✅        | ❌                     | ❌                       | ➖               | ➖          | ➖          |
| NetMind        | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Nextbit        | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Nexusify       | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Noiz           | ✅        | ❌                | ❌            | ❌        | ➖                     | ➖                       | ✅               | ➖          | ➖          |
| NVIDIA         | ✅        | ✅                | ✅            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| NousResearch   | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| NovAI          | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Novita         | ✅        | ✅                | 🟡            | 🟡        | ✅                     | ✅                       | ✅               | ✅          | ➖          |
| OCRSkill       | ✅        | ✅                | ✅            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| OfoxAI         | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| OhMyGPT        | ✅        | ✅                | ❌            | ❌        | ❌                     | ❌                       | ❌               | ➖          | ➖          |
| OneKey         | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| OPEAI          | ✅        | ✅                | ❌            | ❌        | ❌                     | ➖                       | ➖               | ➖          | ➖          |
| OpenAI         | ✅        | ✅                | ✅            | ✅        | ✅                     | ✅                       | ✅               | ➖          | ✅          |
| OpenAIHK       | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| OpenCode       | ✅        | ✅                | ✅            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| OpenLimits     | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| OpenRouter     | ✅        | ❌                | ❌            | ❌        | ❌                     | ❌                       | ➖               | ➖          | ➖          |
| OpperAI        | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ✅          | ➖          |
| Orq            | ✅        | ❌                | ❌            | ❌        | ❌                     | ❌                       | ❌               | ❌          | ➖          |
| OVHcloud       | ✅        | ✅                | ❌            | 🟡        | ✅                     | ✅                       | ✅               | ➖          | ➖          |
| PacketAI       | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Parallel       | ✅        | ✅                | ✅            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| ParalonCloud   | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Parasail       | ✅        | ✅                | ❌            | ✅        | ✅                     | ➖                       | ➖               | ➖          | ➖          |
| PayPerQ        | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Perplexity     | ✅        | 🟡                | 🟡            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Picsart        | ✅        | ❌                | ❌            | ✅        | 🟡                     | ➖                       | ➖               | ➖          | ➖          |
| Pinecone       | ❌        | ❌                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ✅          | ➖          |
| PixelDojo      | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Poe            | ✅        | ✅                | ✅            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Pollinations   | ✅        | ❌                | ❌            | ✅        | ✅                     | ➖                       | ➖               | ➖          | ➖          |
| Portkey        | ✅        | ✅                | ✅            | ✅        | ✅                     | ✅                       | ✅               | ➖          | ➖          |
| PreAPI         | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Puter          | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Prakasa        | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| PrimeIntellect | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| PublicAI       | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ❌          | ➖          |
| Qiniu          | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| QuiverAI       | ✅        | ✅                | ✅            | ✅        | ✅                     | ➖                       | ➖               | ➖          | ➖          |
| Railwail       | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Recraft        | ✅        | ❌                | ❌            | ✅        | ✅                     | ➖                       | ➖               | ➖          | ➖          |
| RedPill        | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| ReGraph        | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| RegoloAI       | ✅        | ✅                | ❌            | ❌        | ✅                     | ✅                       | ➖               | ✅          | ➖          |
| RekaAI         | ✅        | ✅                | ❌            | ❌        | ➖                     | ✅                       | ➖               | ➖          | ➖          |
| RelaxAI        | ✅        | ✅                | ❌            | ❌        | ➖                     | ✅                       | ➖               | ➖          | ➖          |
| Renderful      | ❌        | ❌                | ❌            | ❌        | ❌                     | ➖                       | ➖               | ➖          | ➖          |
| Replicate      | ✅        | ❌                | ❌            | ❌        | ✅                     | ✅                       | ✅               | ➖          | ➖          |
| Requesty       | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| ResembleAI     | ✅        | ❌                | 🟡            | ❌        | ➖                     | ✅                       | ✅               | ➖          | ➖          |
| Reve           | ✅        | ❌                | ❌            | ✅        | ✅                     | ➖                       | ➖               | ➖          | ➖          |
| Rime           | ✅        | ❌                | ❌            | ❌        | ➖                     | ➖                       | ✅               | ➖          | ➖          |
| RoutePlex      | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Routeway       | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Routstr        | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Routmy         | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Runpod         | ✅        | ✅                | ❌            | 🟡        | ✅                     | ❌                       | ✅               | ✅          | ➖          |
| Runware        | ✅        | ❌                | ❌            | ✅        | ✅                     | ➖                       | ➖               | ➖          | ✅          |
| Runway         | ✅        | ❌                | ❌            | 🟡        | ✅                     | ➖                       | ✅               | ➖          | ✅          |
| SambaNova      | ✅        | ✅                | ❌            | 🟡        | ➖                     | ✅                       | ➖               | ➖          | ➖          |
| Sargalay       | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Sarvam         | ✅        | ✅                | 🟡            | 🟡        | ➖                     | ✅                       | ✅               | ➖          | ➖          |
| Scaleway       | ✅        | ✅                | ✅            | ❌        | ➖                     | ✅                       | ➖               | ✅          | ➖          |
| Setapp         | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Shakespeare    | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| ShuttleAI      | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| SiliconFlow    | ✅        | ✅                | 🟡            | 🟡        | ✅                     | ✅                       | ✅               | ✅          | ✅          |
| Simplismart    | ✅        | ✅                | ❌            | ❌        | ❌                     | ❌                       | ➖               | ➖          | ➖          |
| SEALION        | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Segmind        | ✅        | ❌                | ❌            | ✅        | ✅                     | ➖                       | ➖               | ➖          | ➖          |
| SkillBoss      | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| SmallestAI     | ✅        | 🟡                | 🟡            | 🟡        | ➖                     | ✅                       | ✅               | ➖          | ➖          |
| SmartAIPI      | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Smooth         | ✅        | ✅                | ✅            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Speechactors   | ✅        | ❌                | ❌            | ✅        | ➖                     | ➖                       | ✅               | ➖          | ➖          |
| Speechify      | ✅        | ❌                | 🟡            | ✅        | ➖                     | ➖                       | ✅               | ➖          | ➖          |
| Speechmatics   | ✅        | ❌                | ❌            | 🟡        | ➖                     | ✅                       | ✅               | ➖          | ➖          |
| StabilityAI    | ✅        | ❌                | 🟡            | ✅        | ✅                     | ➖                       | ✅               | ➖          | ➖          |
| StepFun        | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ✅               | ➖          | ➖          |
| Straico        | ✅        | ✅                | ❌            | ❌        | ✅                     | ➖                       | ✅               | ➖          | ➖          |
| SovereignAPI   | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| SurferCloud    | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Sudo           | ✅        | ✅                | ✅            | ✅        | ✅                     | ➖                       | ➖               | ➖          | ➖          |
| SunoAPI        | ✅        | ❌                | ❌            | ✅        | ➖                     | ➖                       | ✅               | ➖          | ➖          |
| SUPA           | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| SUFY           | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Supertone      | ✅        | ❌                | ❌            | ❌        | ➖                     | ➖                       | ✅               | ➖          | ➖          |
| Swarms         | ✅        | ✅                | ✅            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Syllogy        | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Synexa         | ✅        | ✅                | ✅            | ✅        | ✅                     | ✅                       | ➖               | ➖          | ✅          |
| Synthetic      | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Tavily         | ✅        | ✅                | ✅            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Telnyx         | ✅        | ✅                | ❌            | ❌        | ➖                     | ✅                       | ➖               | ➖          | ➖          |
| TencentHunyuan | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Tetrate        | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Thaura         | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| TextSynth      | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| TigerCity      | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| TikHubAI       | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Tinfoil        | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| ToAPIs         | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Together       | ✅        | ❌                | ❌            | ✅        | ✅                     | ✅                       | ✅               | ✅          | ✅          |
| TokenFlux      | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| TrueFoundry    | ✅        | ✅                | ✅            | ❌        | ❌                     | ❌                       | ❌               | ❌          | ➖          |
| TTSReader      | ✅        | ❌                | ✅            | ✅        | ➖                     | ➖                       | ✅               | ➖          | ➖          |
| Typecast       | ✅        | ❌                | 🟡            | ✅        | ➖                     | ➖                       | ✅               | ➖          | ➖          |
| UniAPI         | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| UltraSafe      | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| UnrealSpeech   | ✅        | ❌                | 🟡            | ✅        | ➖                     | ➖                       | ✅               | ➖          | ➖          |
| Unbound        | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Upstage        | ✅        | ✅                | ❌            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| UplinkAPI      | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| UVoiceAI       | ✅        | ❌                | ❌            | ❌        | ➖                     | ➖                       | ✅               | ➖          | ➖          |
| Valyu          | ✅        | ✅                | ✅            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Vapi           | ✅        | 🟡                | 🟡            | 🟡        | ➖                     | ❌                       | ❌               | ➖          | ➖          |
| Venice         | ✅        | ✅                | ❌            | 🟡        | ✅                     | ✅                       | ✅               | ➖          | ✅          |
| Verbatik       | ✅        | ❌                | 🟡            | ✅        | ➖                     | ➖                       | ✅               | ➖          | ➖          |
| Verda          | ✅        | ❌                | ❌            | ✅        | ✅                     | ➖                       | ➖               | ➖          | ➖          |
| VIABLELab      | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| VLMRun         | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| VibeCodeCheap  | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Vidu           | ✅        | ❌                | ❌            | ✅        | ✅                     | ➖                       | ✅               | ➖          | ✅          |
| VoiceAI        | ✅        | ❌                | ❌            | ❌        | ➖                     | ➖                       | ✅               | ➖          | ➖          |
| VoidAI         | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Vogent         | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| VoyageAI       | ❌        | ❌                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ✅          | ➖          |
| Vultr          | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| WAI            | ✅        | ✅                | ✅            | ✅        | ✅                     | ➖                       | ➖               | ➖          | ❌          |
| WebsearchAPI   | ✅        | ✅                | ✅            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| WidnAI         | ✅        | ✅                | 🟡            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| WisdomGate     | ✅        | ✅                | ❌            | ❌        | ✅                     | ➖                       | ➖               | ➖          | ✅          |
| World3         | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Writer         | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| xAI            | ✅        | ❌                | ✅            | ✅        | ✅                     | ➖                       | ➖               | ➖          | ✅          |
| YouGetAI       | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| YourVoic       | ✅        | ❌                | 🟡            | 🟡        | ➖                     | ✅                       | ✅               | ➖          | ➖          |
| Zai            | ✅        | ✅                | ❌            | ❌        | ➖                     | ✅                       | ➖               | ➖          | ✅          |
| Zeabur         | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Zenlayer       | ✅        | ✅                | ❌            | ❌        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| ZenMux         | ✅        | ✅                | ✅            | ✅        | ➖                     | ➖                       | ➖               | ➖          | ➖          |
| Zyphra         | ✅        | ❌                | 🟡            | ✅        | ➖                     | ➖                       | ✅               | ➖          | ➖          |

## Run locally

### Prerequisites

- **.NET 9 SDK**

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

#### POST /chat/completions (OpenAI-compatible)

Non-streaming:

```bash
curl "$BASE_URL/chat/completions" \
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
curl "$BASE_URL/chat/completions" \
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

#### POST /responses (OpenAI-compatible)

Non-streaming:

```bash
curl "$BASE_URL/responses" \
  -H "Content-Type: application/json" \
  -H "X-OpenAI-Key: $API_KEY" \
  -d '{
    "model": "openai/gpt-4o-mini",
    "input": "List 3 creative project names"
  }'
```

Streaming:

```bash
curl "$BASE_URL/responses" \
  -H "Content-Type: application/json" \
  -H "X-OpenAI-Key: $API_KEY" \
  -d '{
    "model": "openai/gpt-4o-mini",
    "stream": true,
    "input": "Stream a 2-sentence summary about AIHappey"
  }'
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

#### GET /v1/models

```bash
curl "$BASE_URL/v1/models" \
  -H "X-OpenAI-Key: $API_KEY"
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
