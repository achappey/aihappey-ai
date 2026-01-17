# aihappey-ai

A multi-provider **.NET 9 AI backend** exposing key AI endpoints.

## Provider Support Matrix

The table below shows which endpoints each provider implements (✅), not yet implemented (❌) or for which an endpoint is not applicable to the provider (➖).

| Provider       | [Chat](https://ai-sdk.dev/docs/reference/ai-sdk-ui/use-chat) | [Rerank](https://ai-sdk.dev/docs/ai-sdk-core/reranking) | [Completions](https://platform.openai.com/docs/api-reference/chat) | [Responses](https://platform.openai.com/docs/api-reference/responses) | [Images](https://ai-sdk.dev/docs/ai-sdk-core/image-generation) | [Speech](https://ai-sdk.dev/docs/ai-sdk-core/speech) | [Transcriptions](https://ai-sdk.dev/docs/ai-sdk-core/transcription) | [Sampling](https://modelcontextprotocol.io/specification/draft/client/sampling) |
| -------------- | --------- | ----------- | ----------------- | ------------- | ---------------------- | ---------------- | ------------------------ | --------- |
| AIML           | ✅        | ➖          | ❌                | ❌            | ✅                     | ❌               | ✅                       | ❌        |
| Alibaba        | ✅        | ➖          | ✅                | ❌            | ✅                     | ➖               | ➖                       | ❌        |
| Anthropic      | ✅        | ➖          | ❌                | ❌            | ➖                     | ➖               | ➖                       | ✅        |
| AssemblyAI     | ✅        | ➖          | ❌                | ❌            | ➖                     | ➖               | ✅                       | ❌        |
| AsyncAI        | ✅        | ➖          | ❌                | ❌            | ➖                     | ✅               | ➖                       | ❌        |
| Audixa         | ✅        | ➖          | ❌                | ❌            | ➖                     | ✅               | ➖                       | ❌        |
| Azure          | ✅        | ➖          | ❌                | ❌            | ➖                     | ❌               | ✅                       | ❌        |
| Baseten        | ✅        | ➖          | ✅                | ❌            | ➖                     | ➖               | ➖                       | ❌        |
| CanopyWave     | ✅        | ➖          | ✅                | ❌            | ➖                     | ➖               | ➖                       | ❌        |
| Cerebras       | ✅        | ➖          | ✅                | ❌            | ➖                     | ➖               | ➖                       | ❌        |
| CloudRift      | ✅        | ➖          | ❌                | ❌            | ➖                     | ➖               | ➖                       | ❌        |
| Cohere         | ✅        | ✅          | ✅                | ❌            | ➖                     | ➖               | ➖                       | ❌        |
| ContextualAI   | ➖        | ✅          | ➖                | ➖            | ➖                     | ➖               | ➖                       | ➖        |
| DeepInfra      | ✅        | ✅          | ✅                | ❌            | ✅                     | ✅               | ❌                       | ❌        |
| DeepSeek       | ✅        | ➖          | ✅                | ❌            | ➖                     | ✅               | ✅                       | ❌        |
| Deepgram       | ✅        | ➖          | ❌                | 🟡            | ➖                     | ✅               | ✅                       | ❌        |
| Echo           | ✅        | ➖          | ❌                | ❌            | ➖                     | ➖               | ➖                       | ✅        |
| ElevenLabs     | ✅        | ➖          | ❌                | ❌            | ➖                     | ✅               | ✅                       | ❌        |
| Fireworks      | ✅        | ✅          | ✅                | ✅            | ✅                     | ➖               | ✅                       | ❌        |
| Gladia         | ✅        | ➖          | ❌                | ❌            | ➖                     | ➖               | ✅                       | ❌        |
| Google         | ✅        | ➖          | ❌                | ❌            | ✅                     | ✅               | ✅                       | ✅        |
| Groq           | ✅        | ➖          | ❌                | ❌            | ➖                     | ✅               | ✅                       | ✅        |
| Hyperbolic     | ✅        | ➖          | ✅                | ❌            | ✅                     | ➖               | ➖                       | ❌        |
| Hyperstack     | ✅        | ➖          | ✅                | ❌            | ➖                     | ➖               | ➖                       | ❌        |
| Inferencenet   | ✅        | ➖          | ❌                | ❌            | ➖                     | ➖               | ➖                       | ❌        |
| Jina           | ✅        | ✅          | ❌                | ❌            | ➖                     | ✅               | ✅                       | ❌        |
| Mistral        | ✅        | ➖          | ❌                | ❌            | ✅                     | ➖               | ✅                       | ✅        |
| MiniMax        | ✅        | ➖          | ✅                | 🟡            | ✅                     | ✅               | ➖                       | ❌        |
| Nscale         | ✅        | ➖          | ✅                | ❌            | ✅                     | ➖               | ➖                       | ❌        |
| Nebius         | ✅        | ➖          | ✅                | ❌            | ✅                     | ➖               | ➖                       | ❌        |
| NVIDIA         | ✅        | ➖          | ✅                | ✅            | ➖                     | ➖               | ➖                       | ❌        |
| Novita         | ✅        | ➖          | ✅                | 🟡            | ➖                     | ✅               | ✅                       | ❌        |
| OpenAI         | ✅        | ➖          | ✅                | ✅            | ✅                     | ✅               | ✅                       | ✅        |
| Perplexity     | ✅        | ➖          | ❌                | ❌            | ➖                     | ➖               | ➖                       | ✅        |
| Pollinations   | ✅        | ➖          | ❌                | ❌            | ✅                     | ➖               | ➖                       | ✅        |
| Replicate      | ✅        | ➖          | ❌                | ❌            | ✅                     | ✅               | ✅                       | ❌        |
| ResembleAI     | ✅        | ➖          | ❌                | 🟡            | ➖                     | ✅               | ✅                       | ❌        |
| Runware        | ✅        | ➖          | ❌                | ❌            | ✅                     | ➖               | ➖                       | ❌        |
| Runway         | ✅        | ➖          | ❌                | ❌            | ✅                     | ➖               | ➖                       | ❌        |
| SambaNova      | ✅        | ➖          | ✅                | ❌            | ➖                     | ➖               | ✅                       | ❌        |
| Sarvam         | ✅        | ➖          | ✅                | 🟡            | ➖                     | ✅               | ✅                       | ❌        |
| Scaleway       | ✅        | ➖          | ✅                | ✅            | ➖                     | ➖               | ✅                       | ❌        |
| Speechify      | ✅        | ➖          | ❌                | 🟡            | ➖                     | ✅               | ➖                       | ❌        |
| Speechmatics   | ✅        | ➖          | ❌                | ❌            | ➖                     | ✅               | ❌                       | ❌        |
| StabilityAI    | ✅        | ➖          | ❌                | 🟡            | ✅                     | ✅               | ➖                       | ❌        |
| Telnyx         | ✅        | ➖          | ✅                | ❌            | ➖                     | ➖               | ✅                       | ❌        |
| Tinfoil        | ✅        | ➖          | ✅                | ❌            | ➖                     | ➖               | ➖                       | ❌        |
| Together       | ✅        | ✅          | ❌                | ❌            | ✅                     | ✅               | ✅                       | ✅        |
| TTSReader      | ✅        | ➖          | ❌                | ❌            | ➖                     | ✅               | ➖                       | ❌        |
| Verda          | ✅        | ➖          | ❌                | ❌            | ✅                     | ➖               | ➖                       | ❌        |
| VoyageAI       | ➖        | ✅          | ➖                | ➖            | ➖                     | ➖               | ➖                       | ➖        |
| xAI            | ✅        | ➖          | ❌                | ✅            | ✅                     | ➖               | ➖                       | ✅        |
| Zai            | ✅        | ➖          | ✅                | ❌            | ➖                     | ➖               | ✅                       | ❌        |

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

### Example request

```bash
curl https://ai.aihappey.net/api/chat \
  -H "Content-Type: application/json" \
  -H "X-OpenAI-Key: <your-key>" \
  -d '{"model":"openai/gpt-5.2","messages":[{"role":"user","content":{ "type": "text", "text": "Hello"}}]}'
```

OpenAI compatible Chat Completions

```bash
curl https://ai.aihappey.net/chat/completions \
  -H "Content-Type: application/json" \
  -H "X-OpenAI-Key: <your-key>" \
  -d '{"model":"openai/gpt-5.2","messages":[{"role":"user","content":"Hello"}]}'
```

