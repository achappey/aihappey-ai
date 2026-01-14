# aihappey-ai

A multi-provider **.NET 9 AI backend** exposing key AI endpoints.

## Provider Support Matrix

The table below shows which endpoints each provider implements (✅), does not implement (❌), partially implements (🟡 for /chat/completions when only streaming or only non-streaming is available), or for which an endpoint is not applicable to the provider or service category (➖).

| Provider       | [Chat](https://ai-sdk.dev/docs/reference/ai-sdk-ui/use-chat) | [Rerank](https://ai-sdk.dev/docs/reference/ai-sdk-core/rerank) | /chat/completions | /v1/responses | /v1/images/generations | /v1/audio/speech | /v1/audio/transcriptions | /sampling |
| -------------- | --------- | ----------- | ----------------- | ------------- | ---------------------- | ---------------- | ------------------------ | --------- |
| AIML           | ✅        | ➖          | ❌                | ❌            | ✅                     | ✅               | ✅                       | ❌        |
| Alibaba        | ✅        | ➖          | ❌                | ❌            | ✅                     | ✅               | ✅                       | ❌        |
| Anthropic      | ✅        | ➖          | ❌                | ❌            | ➖                     | ✅               | ✅                       | ✅        |
| AssemblyAI     | ✅        | ➖          | ❌                | ❌            | ➖                     | ➖               | ✅                       | ❌        |
| AsyncAI        | ✅        | ➖          | ❌                | ❌            | ✅                     | ✅               | ✅                       | ❌        |
| Azure          | ✅        | ➖          | ❌                | ❌            | ➖                     | ❌               | ✅                       | ❌        |
| Baseten        | ✅        | ➖          | 🟡                | ❌            | ➖                     | ➖               | ➖                       | ❌        |
| CanopyWave     | ✅        | ➖          | ❌                | ❌            | ➖                     | ➖               | ➖                       | ❌        |
| Cerebras       | ✅        | ➖          | ❌                | ❌            | ✅                     | ✅               | ✅                       | ❌        |
| CloudRift      | ✅        | ➖          | ❌                | ❌            | ➖                     | ✅               | ✅                       | ❌        |
| Cohere         | ✅        | ✅          | ❌                | ❌            | ➖                     | ✅               | ✅                       | ❌        |
| ContextualAI   | ✅        | ✅          | ❌                | ❌            | ✅                     | ✅               | ✅                       | ❌        |
| DeepInfra      | ✅        | ✅          | ❌                | ❌            | ✅                     | ✅               | ✅                       | ❌        |
| DeepSeek       | ✅        | ➖          | ❌                | ❌            | ➖                     | ✅               | ✅                       | ❌        |
| Deepgram       | ✅        | ➖          | ❌                | ❌            | ➖                     | ✅               | ✅                       | ❌        |
| Echo           | ✅        | ➖          | ❌                | ❌            | ➖                     | ➖               | ➖                       | ✅        |
| ElevenLabs     | ✅        | ➖          | ❌                | ❌            | ➖                     | ✅               | ✅                       | ❌        |
| Fireworks      | ✅        | ✅          | ❌                | ❌            | ✅                     | ➖               | ✅                       | ❌        |
| GoogleAI       | ✅        | ➖          | ❌                | ❌            | ✅                     | ✅               | ✅                       | ✅        |
| Groq           | ✅        | ➖          | ❌                | ❌            | ➖                     | ✅               | ✅                       | ✅        |
| Hyperbolic     | ✅        | ➖          | ❌                | ❌            | ✅                     | ➖               | ➖                       | ❌        |
| Inferencenet   | ✅        | ➖          | ❌                | ❌            | ➖                     | ➖               | ➖                       | ❌        |
| Jina           | ✅        | ✅          | ❌                | ❌            | ➖                     | ✅               | ✅                       | ❌        |
| Mistral        | ✅        | ➖          | ❌                | ❌            | ✅                     | ➖               | ✅                       | ✅        |
| MiniMax        | ✅        | ➖          | ❌                | ❌            | ✅                     | ✅               | ➖                       | ❌        |
| Nscale         | ✅        | ➖          | ❌                | ❌            | ✅                     | ➖               | ➖                       | ❌        |
| Nebius         | ✅        | ➖          | ❌                | ❌            | ✅                     | ✅               | ✅                       | ❌        |
| Nvidia         | ✅        | ➖          | ❌                | ❌            | ➖                     | ➖               | ➖                       | ❌        |
| Novita         | ✅        | ➖          | ❌                | ❌            | ➖                     | ✅               | ✅                       | ❌        |
| OpenAI         | ✅        | ➖          | ✅                | ❌            | ✅                     | ✅               | ✅                       | ✅        |
| Perplexity     | ✅        | ➖          | ❌                | ❌            | ➖                     | ➖               | ➖                       | ✅        |
| Pollinations   | ✅        | ➖          | ❌                | ❌            | ✅                     | ➖               | ➖                       | ✅        |
| Replicate      | ✅        | ➖          | ❌                | ❌            | ✅                     | ✅               | ✅                       | ❌        |
| ResembleAI     | ✅        | ➖          | ❌                | ❌            | ➖                     | ✅               | ✅                       | ❌        |
| Runware        | ✅        | ➖          | ❌                | ❌            | ✅                     | ➖               | ➖                       | ❌        |
| Runway         | ✅        | ➖          | ❌                | ❌            | ✅                     | ➖               | ➖                       | ❌        |
| SambaNova      | ✅        | ➖          | ❌                | ❌            | ➖                     | ➖               | ✅                       | ❌        |
| Sarvam         | ✅        | ➖          | ❌                | ❌            | ➖                     | ✅               | ✅                       | ❌        |
| Scaleway       | ✅        | ➖          | ❌                | ❌            | ➖                     | ➖               | ✅                       | ❌        |
| SpeechifyAI    | ✅        | ➖          | ❌                | ❌            | ➖                     | ✅               | ➖                       | ❌        |
| StabilityAI    | ✅        | ➖          | ❌                | ❌            | ✅                     | ✅               | ➖                       | ❌        |
| Telnyx         | ✅        | ➖          | ❌                | ❌            | ➖                     | ➖               | ✅                       | ❌        |
| Tinfoil        | ✅        | ➖          | ❌                | ❌            | ➖                     | ➖               | ➖                       | ❌        |
| Together       | ✅        | ✅          | ❌                | ❌            | ✅                     | ✅               | ✅                       | ✅        |
| VoyageAI       | ✅        | ✅          | ❌                | ❌            | ✅                     | ✅               | ✅                       | ❌        |
| XAI            | ✅        | ➖          | ❌                | ❌            | ✅                     | ➖               | ➖                       | ✅        |
| Zai            | ✅        | ➖          | ❌                | ❌            | ➖                     | ➖               | ✅                       | ❌        |

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

