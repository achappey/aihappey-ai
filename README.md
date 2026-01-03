# aihappey-ai

A multi-provider **.NET 9 AI backend** that exposes:

- a **Vercel AI SDK UI message stream** compatible endpoint (`POST /api/chat`)
- **OpenAI-style** endpoints (chat completions, models, images)
- hosted **Model Context Protocol (MCP)** servers (streamable-http + registry)

This repo is the “backend” counterpart of [`aihappey-chat`](https://github.com/achappey/aihappey-chat) and can also be used as a downstream AI endpoint for [`aihappey-agents`](https://github.com/achappey/aihappey-agents).

> Note: the codebase contains the foundations for **OpenAI Responses**-style request/stream models (see [`Core/AIHappey.Common/Model/Responses/ResponseRequest.cs`](Core/AIHappey.Common/Model/Responses/ResponseRequest.cs:1)), but the public HTTP surface currently focuses on **Vercel UI message streams** + **`/chat/completions`**.

## ✨ Features

- **Multi-provider routing**
  - Providers are registered via [`ServiceExtensions.AddProviders()`](Core/AIHappey.Core/AI/ServiceExtensions.cs:29)
  - Requests pick a provider by model via [`AIModelProviderResolver.Resolve()`](Core/AIHappey.Core/AI/AIModelProviderResolver.cs:137)
- **Model discovery + enrichment**
  - Providers list models; results may be enriched with Vercel AI Gateway metadata via [`AIModelProviderResolver.ResolveModels()`](Core/AIHappey.Core/AI/AIModelProviderResolver.cs:160)
- **Protocol endpoints**
  - Vercel AI SDK UI message stream: `POST /api/chat` (SSE)
  - OpenAI-style chat completions: `POST /chat/completions` (SSE or JSON)
  - Models list: `GET /v1/models`
  - Images generation: `POST /v1/images/generations`
  - MCP sampling: `POST /sampling`
  - Transcription: `POST /api/Transcription/transcribe`
- **MCP server hosting** (streamable-http)
  - Registry: `GET /v0.1/servers` via [`McpCommonExtensions.MapMcpRegistry()`](Core/AIHappey.Common/MCP/McpCommonExtensions.cs:121)
  - Server endpoints: `/{server}` via [`McpCommonExtensions.MapMcpEndpoints()`](Core/AIHappey.Common/MCP/McpCommonExtensions.cs:109)
- **Two sample hosts**
  - Header auth / local dev: [`Samples/AIHappey.HeaderAuth/Program.cs`](Samples/AIHappey.HeaderAuth/Program.cs:1)
  - Entra ID JWT auth (+ telemetry + MCP registry icons): [`Samples/AIHappey.AzureAuth/Program.cs`](Samples/AIHappey.AzureAuth/Program.cs:1)

## 🧭 Architecture

```mermaid
flowchart LR
  client[Clients<br/>aihappey-chat / other UIs] -->|POST /api/chat<br/>Vercel UI message stream (SSE)| host[Sample Host<br/>ASP.NET]
  host -->|resolve model -> provider| resolver[Model provider resolver]
  resolver --> providers[Providers<br/>OpenAI / Anthropic / Google / ...]
  host -->|GET /v0.1/servers| mcpRegistry[MCP Registry]
  host -->|/{server}<br/>streamable-http| mcpServers[MCP servers]
  host -->|optional| telemetry[(Telemetry DB)]

  resolver -. implemented in .-> implResolver[AIModelProviderResolver]
  implResolver --- resolver
```

Key implementation entry points:

- Provider registration: [`ServiceExtensions.AddProviders()`](Core/AIHappey.Core/AI/ServiceExtensions.cs:29)
- Provider selection + model catalog: [`AIModelProviderResolver`](Core/AIHappey.Core/AI/AIModelProviderResolver.cs:6)
- MCP wiring (server + registry): [`McpCommonExtensions`](Core/AIHappey.Common/MCP/McpCommonExtensions.cs:12)

## 📂 Repository structure

```
.
├─ Core/
│  ├─ AIHappey.Common/          # shared request/response models + helpers
│  ├─ AIHappey.Core/            # provider implementations + resolver + MCP tools
│  └─ AIHappey.Telemetry/       # optional telemetry store + MCP telemetry tools
└─ Samples/
   ├─ AIHappey.HeaderAuth/      # sample host (no auth; provider keys via X-* headers)
   └─ AIHappey.AzureAuth/       # sample host (Entra ID JWT auth; AIServices config)
```

## 🚀 Getting Started

### Prerequisites

- **.NET 9 SDK** (pinned via [`global.json`](global.json:1))

### Build

```bash
dotnet build AIHappey.sln -c Release
```

### Run: HeaderAuth sample (simplest)

```bash
dotnet run --project Samples/AIHappey.HeaderAuth/AIHappey.HeaderAuth.csproj
```

This host is designed for local dev/trusted environments:

- no auth
- provider API keys are supplied per-request via headers (see [`HeaderApiKeyResolver`](Samples/AIHappey.HeaderAuth/HeaderApiKeyResolver.cs:5))

### Run: AzureAuth sample (JWT protected)

```bash
dotnet run --project Samples/AIHappey.AzureAuth/AIHappey.AzureAuth.csproj
```

This host enables:

- Entra ID JWT auth (`[Authorize]` on controllers)
- telemetry DB write-on-finish for chat + sampling
- extra MCP servers (telemetry) and registry icons

## ⚙️ Configuration

### HeaderAuth (keys via request headers)

No appsettings keys are required for providers. Instead send headers like:

- `X-OpenAI-Key`
- `X-Anthropic-Key`
- `X-Google-Key`
- `X-Groq-Key`
- `X-xAI-Key`

The full mapping is defined in [`HeaderApiKeyResolver`](Samples/AIHappey.HeaderAuth/HeaderApiKeyResolver.cs:5).

### AzureAuth (keys via configuration)

The AzureAuth sample binds provider configuration from `AIServices` (see [`Program.cs`](Samples/AIHappey.AzureAuth/Program.cs:20) + [`AIServiceConfig`](Samples/AIHappey.AzureAuth/AIServiceConfig.cs:1)).

Minimal shape (placeholders):

```json
{
  "AzureAd": {
    "Instance": "https://login.microsoftonline.com/",
    "TenantId": "<tenant-id>",
    "ClientId": "<client-id>",
    "Audience": "<audience>"
  },
  "AIServices": {
    "OpenAI": { "ModelId": "gpt-4.1", "ApiKey": "" },
    "Anthropic": { "ModelId": "claude-3-5-sonnet-20241022", "ApiKey": "" },
    "Google": { "ModelId": "gemini-2.5-pro", "ApiKey": "" }
  },
  "TelemetryDatabase": "<connection-string>",
  "DarkIcon": "<optional-icon-url>",
  "LightIcon": "<optional-icon-url>"
}
```

Provider key resolution is implemented by [`ConfigKeyResolver.Resolve()`](Samples/AIHappey.AzureAuth/ConfigKeyResolver.cs:10).

## 🔌 HTTP API

All endpoints below are exposed by both sample hosts; AzureAuth additionally requires a valid JWT.

### Vercel AI SDK (UI message stream)

`POST /api/chat`

- Content-Type: `application/json`
- Response: `text/event-stream`
- Response header: `x-vercel-ai-ui-message-stream: v1` (see [`ChatController`](Samples/AIHappey.HeaderAuth/Controllers/ChatController.cs:9))

### OpenAI-style chat completions

`POST /chat/completions`

- Accepts an OpenAI-style chat payload (see [`ChatCompletionOptions`](Core/AIHappey.Common/Model/ChatCompletions/ChatCompletionOptions.cs:1))
- If `stream: true`, responds as SSE and ends with `data: [DONE]` (see [`ChatCompletionsController`](Samples/AIHappey.HeaderAuth/Controllers/ChatCompletionsController.cs:26))

### Models

`GET /v1/models`

Returns the aggregated model list from all configured providers (see [`ModelsController`](Samples/AIHappey.HeaderAuth/Controllers/ModelsController.cs:6)).

### Images

`POST /v1/images/generations`

Routes an image request to the provider backing the selected `model` (see [`ImageController`](Samples/AIHappey.HeaderAuth/Controllers/ImageController.cs:7)).

### MCP Sampling

`POST /sampling`

Implements MCP “sampling” (`createMessage`) by selecting a provider based on model hints (see [`SamplingController`](Samples/AIHappey.HeaderAuth/Controllers/SamplingController.cs:7)).

### Transcription

`POST /api/Transcription/transcribe`

Multipart form upload that uses OpenAI transcription APIs (see [`TranscriptionController`](Samples/AIHappey.HeaderAuth/Controllers/TranscriptionController.cs:7)).

## 🧰 MCP (Model Context Protocol)

### Registry

`GET /v0.1/servers`

Returns the MCP registry payload including streamable-http URLs (see [`McpCommonExtensions.MapMcpRegistry()`](Core/AIHappey.Common/MCP/McpCommonExtensions.cs:121)).

### Server endpoints

MCP servers are hosted at:

- `/{server}`

For example, core definitions include:

- `AI-Models` and `AI-Providers` from [`CoreMcpDefinitions.GetDefinitions()`](Core/AIHappey.Core/MCP/CoreMcpDefinitions.cs:9)

AzureAuth also adds telemetry MCP servers (e.g. `AI-Users`, `AI-Requests`) from [`TelemetryMcpDefinitions.GetDefinitions()`](Core/AIHappey.Telemetry/MCP/TelemetryMcpDefinitions.cs:11).

## 🔐 Security model (samples)

- **HeaderAuth**: no auth; uses per-provider header keys (see [`HeaderApiKeyResolver`](Samples/AIHappey.HeaderAuth/HeaderApiKeyResolver.cs:5))
- **AzureAuth**: Entra ID JWT auth (`AddMicrosoftIdentityWebApi`) and provider keys from configuration (see [`Program.cs`](Samples/AIHappey.AzureAuth/Program.cs:28))

## 🧪 Telemetry (optional)

The AzureAuth sample registers telemetry services via [`TelemetryServiceCollectionExtensions.AddTelemetryServices()`](Core/AIHappey.Telemetry/Extensions/TelemetryServiceCollectionExtensions.cs:13) and records request timing/token usage for:

- Vercel UI message stream chats (see [`ChatController`](Samples/AIHappey.AzureAuth/Controllers/ChatController.cs:54))
- MCP sampling (see [`SamplingController`](Samples/AIHappey.AzureAuth/Controllers/SamplingController.cs:46))

## 🧩 Provider support

Providers are implemented under [`Core/AIHappey.Core/Providers/`](Core/AIHappey.Core/Providers/:1) and surfaced through the common provider abstraction [`IModelProvider`](Core/AIHappey.Core/AI/Abstractions.cs:9).

The default sample DI registration includes (non-exhaustive): OpenAI, Anthropic, Google, Mistral, Groq, xAI, Together, Cohere, Jina, Runway, and more (see [`ServiceExtensions.AddProviders()`](Core/AIHappey.Core/AI/ServiceExtensions.cs:29)).

## 🧪 Status / roadmap (lightweight)

- Add a dedicated **OpenAI Responses** HTTP endpoint (`POST /v1/responses`) matching OpenAI’s streaming semantics.
- Expand MCP servers (more tools and prompts beyond model/provider discovery and telemetry).
- Add OpenAPI/Swagger descriptions for the public endpoints.

## Contributing

Issues and PRs are welcome. If you change streaming/protocol behavior, please include:

- a before/after sample payload
- an SSE transcript snippet
- notes on backward compatibility with [`aihappey-chat`](https://github.com/achappey/aihappey-chat)

