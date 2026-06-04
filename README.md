# aihappey-ai

A multi-provider **.NET AI gateway** exposing normalized endpoints for models, media, skills, agents and MCP capabilities.

No smart routing fairy. No shiny admin portal. No hidden logic maze.
Just provider capabilities flattened, normalized hard and exposed through open contracts.
Stateless. Boring on purpose.

Access 160k+ models and provider-native capabilities from your favorite client.

## Endpoints

| Contract | Endpoint |
| --- | --- |
| AI SDK chat stream | `POST /api/chat` |
| OpenAI chat completions | `POST /v1/chat/completions` |
| OpenAI Responses | `POST /v1/responses` |
| Claude-style messages | `POST /v1/messages` |
| Models | `GET /v1/models` |
| Skills | `GET /v1/skills` |
| Rerank | `POST /api/rerank` |
| Images | `POST /v1/images/generations` |
| Speech | `POST /v1/audio/speech` |
| Transcriptions | `POST /v1/audio/transcriptions` |
| Realtime tokens | `POST /v1/realtime/client_secrets` |
| Video | `POST /v1/videos` |
| MCP server discovery | `GET /v0.1/servers` |

## Request model

Requests use provider-prefixed model ids and provider-specific API keys.

Example model ids:

```txt
openai/gpt-5.4-mini
anthropic/claude-sonnet-4-5
google/gemini-2.5-pro
groq/llama-3.3-70b-versatile
openrouter/openai/gpt-5.4-mini
zai/glm-4.6
```

Example provider headers:

```txt
X-OpenAI-Key: <key>
X-Anthropic-Key: <key>
X-Google-Key: <key>
X-Groq-Key: <key>
X-OpenRouter-Key: <key>
X-ZAI-Key: <key>
```

## Quick start

```bash
BASE_URL="https://ai.aihappey.net"
API_KEY="<your-provider-key>"
```

```bash
curl "$BASE_URL/v1/chat/completions" \
  -H "Content-Type: application/json" \
  -H "X-OpenAI-Key: $API_KEY" \
  -d '{
    "model": "openai/gpt-5.4-mini",
    "messages": [
      { "role": "user", "content": "Say hi" }
    ]
  }'
```

## Examples

### POST /api/chat

AI SDK UI compatible chat stream.

Minimal text message:

```bash
curl "$BASE_URL/api/chat" \
  -H "Content-Type: application/json" \
  -H "X-OpenAI-Key: $API_KEY" \
  -d '{
    "id": "chat-1",
    "model": "openai/gpt-5.4-mini",
    "messages": [
      {
        "id": "msg-1",
        "role": "user",
        "parts": [
          { "type": "text", "text": "Hello from AIHappey" }
        ]
      }
    ]
  }'
```

Tool-call capable request:

```bash
curl "$BASE_URL/api/chat" \
  -H "Content-Type: application/json" \
  -H "X-OpenAI-Key: $API_KEY" \
  -d '{
    "id": "chat-2",
    "model": "openai/gpt-5.4-mini",
    "toolChoice": "auto",
    "maxToolCalls": 1,
    "tools": [
      {
        "name": "get_weather",
        "description": "Get the current weather for a city",
        "inputSchema": {
          "type": "object",
          "properties": {
            "city": { "type": "string" }
          },
          "required": ["city"]
        }
      }
    ],
    "messages": [
      {
        "id": "msg-2",
        "role": "user",
        "parts": [
          { "type": "text", "text": "What is the weather in Amsterdam?" }
        ]
      }
    ]
  }'
```

### POST /v1/chat/completions

OpenAI-compatible chat completions.

Non-streaming:

```bash
curl "$BASE_URL/v1/chat/completions" \
  -H "Content-Type: application/json" \
  -H "X-OpenAI-Key: $API_KEY" \
  -d '{
    "model": "openai/gpt-5.4-mini",
    "messages": [
      { "role": "user", "content": "Say hi" }
    ]
  }'
```

Streaming:

```bash
curl "$BASE_URL/v1/chat/completions" \
  -H "Content-Type: application/json" \
  -H "X-OpenAI-Key: $API_KEY" \
  -d '{
    "model": "openai/gpt-5.4-mini",
    "stream": true,
    "messages": [
      { "role": "user", "content": "Stream a short response" }
    ]
  }'
```

### POST /v1/responses

OpenAI Responses-compatible endpoint.

Non-streaming:

```bash
curl "$BASE_URL/v1/responses" \
  -H "Content-Type: application/json" \
  -H "X-OpenAI-Key: $API_KEY" \
  -d '{
    "model": "openai/gpt-5.4-mini",
    "input": "List 3 creative project names"
  }'
```

Streaming:

```bash
curl "$BASE_URL/v1/responses" \
  -H "Content-Type: application/json" \
  -H "X-OpenAI-Key: $API_KEY" \
  -d '{
    "model": "openai/gpt-5.4-mini",
    "stream": true,
    "input": "Stream a 2-sentence summary about AIHappey"
  }'
```

### GET /v1/models

List available models.

```bash
curl "$BASE_URL/v1/models" \
  -H "X-OpenAI-Key: $API_KEY"
```

### GET /v1/skills

List available skills.

```bash
curl "$BASE_URL/v1/skills" \
  -H "X-OpenAI-Key: $API_KEY"
```

### POST /api/rerank

Use a reranking-capable model.

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

### POST /v1/images/generations

Generate images.

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

### POST /v1/audio/speech

Generate speech.

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

### POST /v1/audio/transcriptions

Transcribe audio.

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

### POST /v1/realtime/client_secrets

Mint realtime client secrets.

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

### POST /v1/videos

Generate videos.

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

## Managed agents

Some providers expose managed agent runtimes instead of only raw model inference.

`aihappey-ai` treats those as provider capabilities too. The goal is the same: provider-specific agent APIs stay behind normalized gateway contracts where possible.

Examples include provider-native agent surfaces such as Anthropic-managed agents, Z.AI agents and other hosted agent runtimes.

## Core MCP servers

`aihappey-ai` exposes core MCP servers over streamable HTTP.

Discovery:

```bash
curl "$BASE_URL/v0.1/servers"
```

Core MCP server URLs use the same `$BASE_URL`.

| Server | URL | Tools |
| --- | --- | --- |
| AI Models | `POST /ai-models` | `ai_models_list` |
| AI Providers | `POST /ai-providers` | `ai_provider_metadata_get_schema`, `ai_providers_list`, `ai_provider_get_models` |
| AI Images | `POST /ai-images` | `ai_images_generate` |
| AI Speech | `POST /ai-speech` | `ai_speech_generate` |
| AI Transcriptions | `POST /ai-transcriptions` | `ai_audio_transcriptions_create` |
| AI Realtime | `POST /ai-realtime` | `ai_realtime_token_get` |
| AI Rerank | `POST /ai-rerank` | `ai_rerank_texts`, `ai_rerank_urls` |
| AI WebSearch | `POST /ai-websearch` | `web_search_google`, `web_search_execute`, `web_search_academic` |


## Provider Support Matrix

The table below shows which endpoints each provider implements (✅), not yet implemented (❌), partially implemented (🟡) or for which an endpoint is not applicable to the provider (➖).

| Provider | [Chat](https://ai-sdk.dev/docs/reference/ai-sdk-ui/use-chat) | [Completions](https://platform.openai.com/docs/api-reference/chat) | [Responses](https://platform.openai.com/docs/api-reference/responses) | [Messages](https://platform.claude.com/docs/en/api/messages) | [Images](https://ai-sdk.dev/docs/ai-sdk-core/image-generation) | [Transcriptions](https://ai-sdk.dev/docs/ai-sdk-core/transcription) | [Speech](https://ai-sdk.dev/docs/ai-sdk-core/speech) | [Rerank](https://ai-sdk.dev/docs/ai-sdk-core/reranking) | Video | [Skills](https://agentskills.io) |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 302AI | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ | ➖ |
| Abliberation | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Aether | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ |
| Agabeyogluai | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AgentAIGateway | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Agentics | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ |
| AgentPhone | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AgnesAI | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ |
| AI21 | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AiApiWorld | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AIBadgr | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AIBramha | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AICC | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ❌ | ➖ | ✅ | ➖ |
| Aichixia | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ |
| AICredits | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AIDuet | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AIgateway | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ |
| AIHorde | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AIHubMix | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ❌ | ➖ |
| AIMagicx | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AINative | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AIRouter | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AIML | ✅ | ❌ | 🟡 | ❌ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ |
| AIsa | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AionLabs | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AkashML | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AKI | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Alibaba | ✅ | ✅ | ✅ | ❌ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ |
| AllToken | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ❌ | ➖ |
| AlphaNeural | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AmazonBedrock | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Ambient | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Anannas | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AnLinkAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Antbase | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Anthropic | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AnyRouter | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Apertis | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ❌ | ❌ | ❌ | ➖ |
| AndyAPI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ApiAirforce | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ | ➖ | ✅ | ➖ |
| APIFree | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ❌ | ➖ | ❌ | ➖ |
| APIPASS | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ❌ | ➖ |
| APIpie | ✅ | ✅ | ❌ | ❌ | ✅ | ✅ | ✅ | ➖ | ❌ | ➖ |
| APIPod | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| APIyi | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ | ➖ |
| Apekey | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ArceeAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ARKLabs | ✅ | ✅ | ❌ | ❌ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ |
| ArkRoute | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ❌ | ➖ |
| ArliAI | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ARWriter | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ASIOne | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AskARC | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AskCodi | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AssemblyAI | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Assisters | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| Astica | ✅ | ❌ | ❌ | ❌ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ |
| AsyncAI | ✅ | ❌ | ✅ | ❌ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ |
| AtlasCloud | ✅ | ✅ | ❌ | ❌ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ |
| ATXP | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Augure | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Audixa | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ |
| Auriko | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Avian | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Azerion | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ |
| Azure | ✅ | 🟡 | 🟡 | ❌ | ➖ | ✅ | ✅ | ➖ | ➖ | ✅ |
| Baidu | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| BaseAPI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Baseten | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| BastionGPT | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| BazaarLink | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ❌ | ➖ | ❌ | ➖ |
| BergetAI | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ |
| Bineric | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ |
| BLACKBOX | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| BlackForestLabs | ✅ | ❌ | ❌ | ❌ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Blink | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ |
| BlockRun | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ❌ | ➖ | ❌ | ➖ |
| BotVerse | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Bria | ✅ | ❌ | ❌ | ❌ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| BrowserUse | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Brainiall | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ❌ | ➖ | ➖ | ➖ |
| Brave | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| BytePlus | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ |
| ByteSpace | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Bytez | ✅ | ✅ | ❌ | ❌ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ |
| Cailos | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| CairoCoder | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| CallMissed | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ |
| CAMBAI | ✅ | ✅ | ✅ | ❌ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ |
| CanopyWave | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Cartesia | ✅ | ❌ | ❌ | ❌ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ |
| CaseDev | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| Cerebras | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ChainGPT | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ChainHub | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ❌ | ➖ |
| CheapGrok | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Chutes | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Cirrascale | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ✅ | ➖ | ➖ |
| Citadelis | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Clankie | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ClawHub | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| ClawLite | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Clauddy | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Claudible | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Cline | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Clod | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| CloudFerro | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| CloudRift | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| CodingPlanX | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ❌ | ➖ |
| Codzen | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ❌ | ➖ |
| Cohere | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ |
| CometAPI | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ |
| CommandCode | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Commonstack | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Concentrate | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ContextualAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| Cortecs | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Cortex | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Crazyrouter | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ | ➖ |
| CrofAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Daglo | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ |
| Dandolo | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Databricks | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| DataForSEO | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| DreamGen | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| DeAPI | ✅ | ❌ | ❌ | ❌ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ |
| Decart | ✅ | ❌ | ❌ | ❌ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ |
| DedalusLabs | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ |
| Deepbricks | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| DeepInfra | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ |
| DeepL | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| DeepSeek | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Deepgram | ✅ | ❌ | 🟡 | ❌ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ |
| DigitalOcean | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ❌ | ➖ | ➖ | ➖ |
| DistributeAI | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| DocsRouter | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Doubleword | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Dubrify | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| EAGM | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Eachlabs | ❌ | ❌ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ | ❌ | ➖ |
| Edgee | ✅ | ✅ | ❌ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Echo | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| EdenAI | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ❌ | ➖ |
| Eliza | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ |
| EmberCloud | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| EmbraceableAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ElectronHub | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ❌ | ➖ | ❌ | ➖ |
| ElevenLabs | ✅ | ❌ | 🟡 | ❌ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ |
| EmbyAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| EuGPT | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Euqai | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| EUrouter | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| EzAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| EverypixelLabs | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ |
| EvoLinkAI | ✅ | ✅ | ❌ | ✅ | ❌ | ❌ | ❌ | ➖ | ❌ | ➖ |
| Exa | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Featherless | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Fal | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| FastRouter | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ❌ | ➖ |
| Finora | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| FishAudio | ✅ | ❌ | ❌ | ❌ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ |
| Fireworks | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ |
| FiveDock | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ |
| Forefront | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Fortytwo | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Foureverland | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Fred | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| FreeInference | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Freepik | ✅ | ❌ | ❌ | ❌ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ |
| FreedomGPT | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ❌ | ➖ |
| FreeLLMAPIKeys | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Friendli | ✅ | ✅ | ✅ | ✅ | ➖ | ❌ | ➖ | ➖ | ➖ | ➖ |
| FullAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| GateMind | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| GateRouter | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| GeekAI | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ | ➖ | ➖ |
| GeneralCompute | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| GetGoAPI | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ |
| GitHub | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Glama | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Gladia | ✅ | ❌ | ❌ | ❌ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Glio | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| GMICloud | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| Google | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ |
| GoogleTranslate | ✅ | ❌ | 🟡 | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| GooseAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| GonkaGate | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| GPTsAPI | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| GPTProto | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ❌ | ➖ |
| Gradium | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ |
| GreenPT | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ |
| GrooveDev | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| Groq | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ |
| GTranslate | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Haimaker | ✅ | ✅ | ❌ | ❌ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ |
| Hanzo | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Helicone | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| HelyxAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Herma | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| HeyGen | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ |
| Hicap | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ |
| HolySheepAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| HorayAI | ✅ | ✅ | ❌ | ❌ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| HuggingFace | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Hyperbolic | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ |
| Hyperbrowser | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| HyperRouter | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ |
| Hyperstack | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| iApp | ✅ | ✅ | ❌ | ❌ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Ideogram | ✅ | ❌ | ❌ | ❌ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| IGPT | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ImageRouter | ✅ | ❌ | 🟡 | ❌ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ |
| InceptionLabs | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Inceptron | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Infercom | ✅ | ✅ | ✅ | ✅ | ➖ | ❌ | ➖ | ➖ | ➖ | ➖ |
| Inferencenet | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Inferencesh | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ❌ | ➖ | ❌ | ➖ |
| Inferencespace | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| InferLink | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Inflection | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Infomaniak | ✅ | ✅ | ❌ | ❌ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ |
| Infraxa | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Infron | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ |
| Inworld | ✅ | ✅ | ✅ | ✅ | ➖ | ❌ | ✅ | ➖ | ➖ | ➖ |
| IOnet | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| IONOS | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Ishi | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| IonRouter | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| JassieAI | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| JiekouAI | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ❌ | ❌ | ➖ |
| JigsawStack | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Jina | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| JKAIHub | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| JSON2Video | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| Jules | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Kilo | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Key4U | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| KeyMeAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Keyplex | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Kimrel | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Kirha | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| KittenStack | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| KissAPI | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| KnoxChat | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ❌ | ➖ | ➖ |
| KlingAI | ✅ | ❌ | 🟡 | ❌ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ |
| LangbaseAgent | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LangbasePipe | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LaoZhang | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ | ➖ | ❌ | ➖ |
| LectoAI | ✅ | ❌ | 🟡 | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LEAPERone | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LibertAI | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Lilac | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Lingvanex | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| Linkup | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LitAI | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LiteRouter | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LexiCo | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Llama | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LLM7 | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LLMAPI | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ❌ | ➖ |
| LLMBase | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LLMCloud | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LLMGateway | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ |
| LLMHubIFS | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LLMkiwi | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LLMLayer | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LLMStats | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LLMTR | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ❌ | ➖ | ❌ | ➖ |
| LLMWise | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LMRouter | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ |
| LogicosLLMHub | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LongCat | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LOVO | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ |
| LumaAI | ✅ | ❌ | ❌ | ❌ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ |
| Lumecoder | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Lumenfall | ✅ | ✅ | ❌ | ❌ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ |
| LuminoAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Lunos | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ |
| LXG2IT | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Magisterium | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| MancerAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| MARA | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| MaritacaAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Martian | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| MatterAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| MegaLLM | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| MegaNova | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ❌ | ➖ | ❌ | ➖ |
| MemoryRouter | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Merge | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Messari | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Mia21 | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| MIAPI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Microsoft | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| MIMICXAI | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| MiniMax | ✅ | ✅ | 🟡 | ❌ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ |
| MiroMind | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Mistral | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ |
| Mixlayer | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Modal | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ModelMax | ✅ | ✅ | ❌ | ✅ | ❌ | ➖ | ➖ | ➖ | ❌ | ➖ |
| ModelSync | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ModelBridge | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ModelRouter | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ModelsLab | ✅ | ❌ | ❌ | ❌ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ |
| ModernMT | ✅ | ❌ | 🟡 | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| MoleAPI | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Moltkey | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Monica | ✅ | ✅ | ❌ | ❌ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Moonshot | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Morph | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Morpheus | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ |
| MuleRouter | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ❌ | ➖ | ❌ | ➖ |
| MuleRun | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ❌ | ➖ |
| MultiverseAI | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| MumeAI | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| MurfAI | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ |
| MyCoAI | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ |
| MyRouter | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ | ➖ |
| NagaAI | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ |
| NavyAI | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ❌ | ➖ |
| NanoGPT | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ❌ | ➖ | ❌ | ➖ |
| Nataris | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| NEARAI | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ➖ | ❌ | ➖ | ➖ |
| NLPCloud | ✅ | 🟡 | 🟡 | ❌ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ |
| NRPNautilus | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Nscale | ✅ | ✅ | ❌ | ❌ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Nebius | ✅ | ✅ | ❌ | ❌ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| NebulaBlock | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Neuralwatt | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Neosantara | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ |
| NetMind | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ❌ | ➖ | ➖ | ➖ |
| Nextbit | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Nexusify | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ |
| NinjaChat | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ | ❌ | ➖ |
| Nodion | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ | ➖ | ➖ |
| Noiz | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ |
| NVIDIA | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Nodebyt | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| NONKYCAI | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| NousResearch | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Nouswise | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| NovAI | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Novita | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ |
| OCRSkill | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Octagon | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OfoxAI | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OhMyGPT | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ |
| Ollama | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OmniaKey | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OneInfer | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ❌ | ➖ | ❌ | ➖ |
| OneKey | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OODAAI | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ❌ | ➖ |
| OPEAI | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OpenAdapter | ✅ | ✅ | ✅ | ✅ | ➖ | ❌ | ❌ | ➖ | ➖ | ➖ |
| OpenAI | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ |
| OpenCode | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OpenGate | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OpenGateway | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OpenHands | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OpenLimits | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OpenPipe | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OpenRouter | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ |
| OpenSourceAIHub | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OpperAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OpusCode | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Oraicle | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OrbGPU | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OrcaRouter | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ❌ | ➖ | ➖ | ➖ |
| OrqAgentRuntime | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OrqRouter | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ | ➖ | ➖ |
| Osiris | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OurToken | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OVHcloud | ✅ | ✅ | ❌ | ❌ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ |
| OXOAPI | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Parallel | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ParalonCloud | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Parasail | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Paul | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| PayPerQ | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ❌ | ➖ |
| Pellet | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Perceptron | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Perplexity | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| PiAPI | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ | ❌ | ➖ |
| Picklyone | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ❌ | ➖ |
| Picsart | ✅ | ❌ | ❌ | ❌ | 🟡 | ➖ | ➖ | ➖ | ➖ | ➖ |
| Pinecone | ❌ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| Pioneer | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| PixelDojo | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| PixCode | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ✅ | ➖ | ✅ | ➖ |
| Poe | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Pollinations | ✅ | ❌ | ❌ | ❌ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Poolside | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Portkey | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ |
| PreAPI | ❌ | ❌ | ❌ | ❌ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ |
| Puter | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Prakasa | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| PrimeIntellect | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Privatemode | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| PublicAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ❌ | ➖ | ➖ |
| Qiniu | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| QuiverAI | ✅ | ✅ | ✅ | ❌ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Radiance | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Radient | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ |
| Railwail | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ |
| RaxAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Realrouter | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Recraft | ✅ | ❌ | ❌ | ❌ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| RedPill | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ |
| ReGraph | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ |
| RegoloAI | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ |
| RekaAI | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Relace | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| RelaxAI | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Renderful | ❌ | ❌ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Replicate | ✅ | ❌ | ❌ | ❌ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ |
| Requesty | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ResembleAI | ✅ | ❌ | 🟡 | ❌ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ |
| Reve | ✅ | ❌ | ❌ | ❌ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| RewindAI | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ❌ | ➖ |
| Rime | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ |
| RodiumAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| RoutePlex | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Router9 | ✅ | ✅ | ✅ | ✅ | ➖ | ❌ | ❌ | ➖ | ➖ | ➖ |
| RouterLink | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Routera | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Routeway | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Routstr | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Routmy | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ❌ | ➖ |
| RunAPI | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Runcrate | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ❌ | ➖ |
| Runpod | ✅ | ✅ | ❌ | ❌ | ✅ | ❌ | ✅ | ✅ | ➖ | ➖ |
| Runtimo | ✅ | ❌ | ❌ | ❌ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ |
| Runware | ✅ | ❌ | ❌ | ❌ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ |
| Runway | ✅ | ❌ | ❌ | ❌ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ |
| SambaNova | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Sapiom | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Sargalay | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Sarvam | ✅ | ✅ | 🟡 | ❌ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ |
| Scaleway | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ |
| ScalixWorld | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| SchatziAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ScrapeLLM | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| SEALION | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| SelinaAI | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Serverspace | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Setapp | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Shakespeare | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ShannonAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ShareAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Shengsuanyun | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ShuttleAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| SiliconFlow | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ |
| SimpleLLM | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Simplismart | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ |
| Segmind | ✅ | ❌ | ❌ | ❌ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| SkillBoss | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| SkypoolToken | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Slancha | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| SmallestAI | ✅ | 🟡 | 🟡 | ❌ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ |
| SmartAIPI | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Smooth | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| SovrGPT | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Speechactors | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ |
| Speechify | ✅ | ❌ | 🟡 | ❌ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ |
| Speechmatics | ✅ | ❌ | ❌ | ❌ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ |
| StabilityAI | ✅ | ❌ | 🟡 | ❌ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ |
| StealthGPT | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| StepFun | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ |
| Straico | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ❌ | ➖ |
| StreamLake | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| SurferCloud | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| SudoRouter | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ❌ | ➖ |
| SunoAPI | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ |
| SUPA | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| SUFY | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Supertone | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ |
| Swarms | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Syllogy | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Synexa | ✅ | ✅ | ✅ | ❌ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ |
| Synthetic | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Tapas | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Tavily | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| TEAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| TeamDay | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Telnyx | ✅ | ✅ | ❌ | ❌ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Tembo | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| TencentHunyuan | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| TensorBlock | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Tensorix | ✅ | ✅ | ❌ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ |
| TerminalSkills | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| Tetrate | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Thalam | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ❌ | ➖ | ❌ | ➖ |
| Thaura | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| TheRouterAI | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ |
| TheOldAPI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| TextSynth | ✅ | ✅ | ❌ | ❌ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ |
| TigerCity | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| TikHubAI | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Tinfoil | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| ToAPIs | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Together | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ |
| Token360 | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ❌ | ➖ |
| TokenFlux | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| TokenHub | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| TokenLab | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ | ➖ |
| ToolRelay | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ❌ | ➖ |
| TrueFoundry | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ | ➖ | ➖ |
| TTSReader | ✅ | ❌ | ✅ | ❌ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ |
| Typecast | ✅ | ❌ | 🟡 | ❌ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ |
| UniAPI | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| UltraSafe | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| UncensoredChat | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| UncloseAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ❌ | ➖ | ➖ | ➖ |
| UnrealSpeech | ✅ | ❌ | 🟡 | ❌ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ |
| Unbound | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Upstage | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| UUMuse | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| UVoiceAI | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ |
| Valyu | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Vapi | ✅ | 🟡 | 🟡 | ❌ | ➖ | ❌ | ❌ | ➖ | ➖ | ➖ |
| Venice | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ |
| Verbatik | ✅ | ❌ | 🟡 | ❌ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ |
| Verda | ✅ | ❌ | ❌ | ❌ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| VIABLELab | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| VLMRun | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| VibeKit | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Vidu | ✅ | ❌ | ❌ | ❌ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ |
| Virouter | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Vivgrid | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| VoiceAI | ✅ | ❌ | ❌ | ❌ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ |
| Vogent | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| VoyageAI | ❌ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| Vultr | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Wafer | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| WAI | ✅ | ✅ | ✅ | ❌ | ✅ | ➖ | ➖ | ➖ | ❌ | ➖ |
| WebsearchAPI | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| WebCrawlerAPI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| WesenAI | ✅ | ✅ | ❌ | ❌ | ➖ | ❌ | ❌ | ➖ | ➖ | ➖ |
| WiseRouter | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| WisGate | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ |
| Writer | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| xAI | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ |
| XiaomiMIMO | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ |
| Yollomi | ✅ | ❌ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ | ❌ | ➖ |
| YouCom | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| YouGetAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| YourVoic | ✅ | ❌ | 🟡 | ❌ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ |
| Zai | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ✅ | ➖ |
| Zeabur | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Zenlayer | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ➖ | ❌ | ➖ |
| ZenMux | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ❌ | ➖ |
| ZyloAPI | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Zyphra | ✅ | ❌ | 🟡 | ❌ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ |
