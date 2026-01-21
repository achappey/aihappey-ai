# aihappey-ai

A multi-provider **.NET AI backend** exposing key AI endpoints.

## Provider Support Matrix

The table below shows which endpoints each provider implements (✅), not yet implemented (❌) or for which an endpoint is not applicable to the provider (➖).

| Provider       | [Chat](https://ai-sdk.dev/docs/reference/ai-sdk-ui/use-chat) | [Rerank](https://ai-sdk.dev/docs/ai-sdk-core/reranking) | [Completions](https://platform.openai.com/docs/api-reference/chat) | [Responses](https://platform.openai.com/docs/api-reference/responses) | [Images](https://ai-sdk.dev/docs/ai-sdk-core/image-generation) | [Speech](https://ai-sdk.dev/docs/ai-sdk-core/speech) | [Transcriptions](https://ai-sdk.dev/docs/ai-sdk-core/transcription) | [Sampling](https://modelcontextprotocol.io/specification/draft/client/sampling) |
| -------------- | --------- | ----------- | ----------------- | ------------- | ---------------------- | ---------------- | ------------------------ | --------- |
| AI21           | ✅        | ➖          | ✅                | ❌            | ➖                     | ➖               | ➖                       | ❌        |
| AIML           | ✅        | ➖          | ❌                | 🟡            | ✅                     | ✅               | ✅                       | 🟡        |
| Alibaba        | ✅        | ➖          | ✅                | ❌            | ✅                     | ➖               | ➖                       | ❌        |
| Anthropic      | ✅        | ➖          | ❌                | ❌            | ➖                     | ➖               | ➖                       | ✅        |
| AssemblyAI     | ✅        | ➖          | ❌                | ❌            | ➖                     | ➖               | ✅                       | ❌        |
| AsyncAI        | ✅        | ➖          | ❌                | ✅            | ➖                     | ✅               | ➖                       | ✅        |
| Audixa         | ✅        | ➖          | ❌                | ❌            | ➖                     | ✅               | ➖                       | ✅        |
| Azure          | ✅        | ➖          | 🟡                | 🟡            | ➖                     | ✅               | ✅                       | 🟡        |
| Baseten        | ✅        | ➖          | ✅                | ❌            | ➖                     | ➖               | ➖                       | ❌        |
| CanopyWave     | ✅        | ➖          | ✅                | ❌            | ➖                     | ➖               | ➖                       | ❌        |
| Cerebras       | ✅        | ➖          | ✅                | ❌            | ➖                     | ➖               | ➖                       | ❌        |
| CloudRift      | ✅        | ➖          | ❌                | ❌            | ➖                     | ➖               | ➖                       | ❌        |
| Cohere         | ✅        | ✅          | ✅                | ❌            | ➖                     | ➖               | ➖                       | ❌        |
| ContextualAI   | ➖        | ✅          | ➖                | ➖            | ➖                     | ➖               | ➖                       | ➖        |
| DeepInfra      | ✅        | ✅          | ✅                | 🟡            | ✅                     | ✅               | ✅                       | 🟡        |
| DeepSeek       | ✅        | ➖          | ✅                | ❌            | ➖                     | ➖               | ➖                       | ❌        |
| Deepgram       | ✅        | ➖          | ❌                | 🟡            | ➖                     | ✅               | ✅                       | 🟡        |
| Echo           | ✅        | ➖          | ❌                | ❌            | ➖                     | ➖               | ➖                       | ✅        |
| ElevenLabs     | ✅        | ➖          | ❌                | 🟡            | ➖                     | ✅               | ✅                       | 🟡        |
| Fireworks      | ✅        | ✅          | ✅                | ✅            | ✅                     | ➖               | ✅                       | ❌        |
| Freepik        | ✅        | ➖          | ❌                | ❌            | ✅                     | ✅               | ➖                       | ❌        |
| Gladia         | ✅        | ➖          | ❌                | ❌            | ➖                     | ➖               | ✅                       | ❌        |
| Google         | ✅        | ➖          | ❌                | ❌            | ✅                     | ✅               | ✅                       | ✅        |
| Groq           | ✅        | ➖          | ❌                | ❌            | ➖                     | ✅               | ✅                       | ✅        |
| Hyperbolic     | ✅        | ➖          | ✅                | 🟡            | ✅                     | ✅               | ➖                       | 🟡        |
| Hyperstack     | ✅        | ➖          | ✅                | ❌            | ➖                     | ➖               | ➖                       | ❌        |
| Inferencenet   | ✅        | ➖          | ❌                | ❌            | ➖                     | ➖               | ➖                       | ❌        |
| Jina           | ✅        | ✅          | ❌                | ❌            | ➖                     | ➖               | ➖                       | ✅        |
| Lingvanex      | ✅        | ✅          | ✅                | ✅            | ➖                     | ➖               | ➖                       | ✅        |
| Mistral        | ✅        | ➖          | ❌                | ❌            | ✅                     | ➖               | ✅                       | ✅        |
| MiniMax        | ✅        | ➖          | ✅                | 🟡            | ✅                     | ✅               | ➖                       | 🟡        |
| MurfAI         | ✅        | ➖          | ✅                | ✅            | ➖                     | ✅               | ➖                       | ✅        |
| Nscale         | ✅        | ➖          | ✅                | ❌            | ✅                     | ➖               | ➖                       | ❌        |
| Nebius         | ✅        | ➖          | ✅                | ❌            | ✅                     | ➖               | ➖                       | ❌        |
| NVIDIA         | ✅        | ➖          | ✅                | ✅            | ➖                     | ➖               | ➖                       | ❌        |
| Novita         | ✅        | ✅          | ✅                | 🟡            | ✅                     | ✅               | ✅                       | 🟡        |
| OpenAI         | ✅        | ➖          | ✅                | ✅            | ✅                     | ✅               | ✅                       | ✅        |
| Perplexity     | ✅        | ➖          | ❌                | ❌            | ➖                     | ➖               | ➖                       | ✅        |
| Pollinations   | ✅        | ➖          | ❌                | ❌            | ✅                     | ➖               | ➖                       | ✅        |
| Replicate      | ✅        | ➖          | ❌                | ❌            | ✅                     | ✅               | ✅                       | ❌        |
| ResembleAI     | ✅        | ➖          | ❌                | 🟡            | ➖                     | ✅               | ✅                       | ❌        |
| Runware        | ✅        | ➖          | ❌                | ❌            | ✅                     | ➖               | ➖                       | ❌        |
| Runway         | ✅        | ➖          | ❌                | ❌            | ✅                     | ➖               | ➖                       | ❌        |
| SambaNova      | ✅        | ➖          | ✅                | ❌            | ➖                     | ➖               | ✅                       | ❌        |
| Sarvam         | ✅        | ➖          | ✅                | 🟡            | ➖                     | ✅               | ✅                       | 🟡        |
| Scaleway       | ✅        | ➖          | ✅                | ✅            | ➖                     | ➖               | ✅                       | ❌        |
| Speechify      | ✅        | ➖          | ❌                | 🟡            | ➖                     | ✅               | ➖                       | ✅        |
| Speechmatics   | ✅        | ➖          | ❌                | ❌            | ➖                     | ✅               | ❌                       | 🟡        |
| StabilityAI    | ✅        | ➖          | ❌                | 🟡            | ✅                     | ✅               | ➖                       | 🟡        |
| Telnyx         | ✅        | ➖          | ✅                | ❌            | ➖                     | ➖               | ✅                       | ❌        |
| Tinfoil        | ✅        | ➖          | ✅                | ❌            | ➖                     | ➖               | ➖                       | ❌        |
| Together       | ✅        | ✅          | ❌                | ❌            | ✅                     | ✅               | ✅                       | ✅        |
| TTSReader      | ✅        | ➖          | ❌                | ✅            | ➖                     | ✅               | ➖                       | ✅        |
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
  -H "X-OpenAI-Key: $API_KEY" \
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

#### GET /v1/models

```bash
curl "$BASE_URL/v1/models" \
  -H "X-OpenAI-Key: $API_KEY"
```

