# aihappey-ai

A multi-provider **.NET AI gateway** exposing normalized endpoints for models, media, skills, agents and MCP capabilities.

No smart routing fairy. No shiny admin portal. No hidden logic maze. No code execution.
Just provider capabilities flattened, normalized hard and exposed through open contracts.
Stateless. Boring on purpose.

Access 140k+ models and provider-native capabilities from your favorite client.

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

### TwelveLabs

Supply a TwelveLabs API key with `X-TwelveLabs-Key` (or
`AIServiceConfig.TwelveLabs.ApiKey` in the AzureAuth sample). OpenAI and Vercel text
embeddings use `POST https://api.twelvelabs.io/v1.3/embed-v2` with `x-api-key` authentication.
Discovery uses the existing static catalog: `twelvelabs/marengo3.0` and
`twelvelabs/marengo3.5`, as documented in the official SDK. No credentials are needed
for discovery; execution requires a key. No standalone model-listing API is exposed
by the official SDK.

Each input string produces a separate native request and one embedding, preserving
input order. Marengo 3.0 uses `text.input_text`; Marengo 3.5 uses
`multi_input.input_text`. OpenAI float and base64 output are supported; token-ID
inputs and `user` are not. Explicit `dimensions` maps to `embedding_dimension`;
native dimension restrictions and defaults remain the provider's responsibility.

Native fields pass through embedding additional properties or
`providerOptions.twelvelabs`, including `auto_truncate`, `embedding_uncertainty`,
and nested `multi_input` options. Standardized model and input fields take precedence;
native defaults are not injected. Marengo 3.5 modality token counts are summed into
prompt/total usage. Marengo 3.0 does not report token usage. Vercel response data
preserves native responses, including uncertainty and truncation details.

This integration exposes synchronous embeddings, not streaming, video indexing/search,
analysis, or asynchronous media jobs. No live credentialed API verification has been
performed. The documentation website was inaccessible during implementation; the
official generated SDK supplied the verified API schemas and catalog.

Official references: [embedding API](https://docs.twelvelabs.io/v1.3/api-reference/create-embeddings-v2/create-embeddings),
[SDK request schema and model catalog](https://github.com/twelvelabs-io/twelvelabs-python/blob/main/src/twelvelabs/embed/v_2/client.py),
[authentication](https://github.com/twelvelabs-io/twelvelabs-python/blob/main/src/twelvelabs/core/client_wrapper.py),
[response schema](https://github.com/twelvelabs-io/twelvelabs-python/blob/main/src/twelvelabs/types/embedding_success_response.py),
and [usage schema](https://github.com/twelvelabs-io/twelvelabs-python/blob/main/src/twelvelabs/types/embedding_usage.py).

### Contextual AI

Supply a Contextual AI API key with `X-ContextualAI-Key` (or
`AIServiceConfig.ContextualAI.ApiKey` in the AzureAuth sample). Text reranking uses bearer
authentication at `POST https://api.contextual.ai/v1/rerank`.

Discovery uses the existing static catalog, with the three models documented by the official SDK:
`contextualai/ctxl-rerank-v2-instruct-multilingual`,
`contextualai/ctxl-rerank-v2-instruct-multilingual-mini`, and
`contextualai/ctxl-rerank-v1-instruct`. The SDK exposes no standalone model-listing API.
Discovery requires no key; execution does.

The existing reranking surface maps model, query, text documents, and optional `topN`.
Native fields such as `instruction`, per-document `metadata`, and future options pass through
`providerOptions.contextualai` unchanged; no native defaults are injected. Standardized model,
query, documents, and an explicitly supplied `topN` take precedence over conflicting native options.
Native result order, indices, and relevance scores are retained, with the full response preserved
in provider metadata and response data. The documented response has no token usage or streaming.
Chat, generation/agents, LMUnit evaluation, parsing, embeddings, and media are not exposed by this
rerank integration. No live credentialed API verification has been performed.

Official references: [rerank API](https://docs.contextual.ai/api-reference/rerank/rerank),
[SDK model catalog and request schema](https://github.com/ContextualAI/contextual-client-python/blob/main/src/contextual/types/rerank_create_params.py),
[SDK response schema](https://github.com/ContextualAI/contextual-client-python/blob/main/src/contextual/types/rerank_create_response.py),
[SDK authentication and base URL](https://github.com/ContextualAI/contextual-client-python/blob/main/src/contextual/_client.py).

### Isaacus

Supply an Isaacus API key with `X-Isaacus-Key` (or `AIServiceConfig.Isaacus.ApiKey` in the
AzureAuth sample). Requests use bearer authentication at `https://api.isaacus.com/v1`.
Discovery is static and requires no key: `isaacus/kanon-2-embedder` supports text embeddings;
`isaacus/kanon-2-reranker` and `isaacus/kanon-universal-classifier` support text reranking.
Isaacus does not expose a models endpoint in its official SDK.

OpenAI and Vercel embeddings map to `POST /embeddings`; OpenAI float and base64 output are
supported, but token-ID inputs and `user` are not. Reranking maps to `POST /rerankings` and
accepts only text documents. Native options such as `task`, `overflow_strategy`, `dimensions`,
`is_iql`, `scoring_method`, and `chunking_options` pass through embedding additional properties
or `providerOptions.isaacus` as appropriate. No native option defaults are injected.
`input_tokens` becomes embedding prompt/total token usage; Vercel metadata preserves native
responses and usage. Chat, streaming, and media capabilities are unsupported.
No live credentialed API verification has been performed.

Official references: [model introduction](https://docs.isaacus.com/models/introduction),
[Python SDK and API schemas](https://github.com/isaacus-dev/isaacus-python).

### Infermatic

Supply an Infermatic API key with `X-Infermatic-Key` (or `AIServiceConfig.Infermatic.ApiKey` in the
AzureAuth sample). Models are discovered dynamically from `GET https://api.totalgpt.ai/v1/models`;
their native IDs and available metadata are preserved. Chat Completions and streaming use
`/v1/chat/completions`; embeddings use `POST /v1/embeddings`. Responses, Messages, and UI chat use
the existing unified chat mapping. Provider-native chat and embedding parameters continue through
the existing additional-properties passthrough. Image, audio, and other media capabilities are not
exposed. No live credentialed API verification has been performed.

Official references: [API overview](https://infermatic.ai/docs/overview/),
[embedding models](https://infermatic.ai/intro-to-embedding-models/),
[API keys](https://infermatic.ai/docs/api-key/).

### Coze

Supply a Coze API token with `X-Coze-Key` (or `AIServiceConfig.Coze.ApiKey` in the AzureAuth
sample). Agent discovery lists the token's workspaces and their published, online bots; discovered
IDs use `coze/{workspace_id}/{bot_id}`. The integration uses Coze's global API host and bearer
authentication.

Chat Completions and streaming use Coze's `/v3/chat` endpoint. Responses, Messages, and UI chat use
the existing unified chat mapping. Usage from Coze chat-completed events is normalized to token
usage. Provider-native request fields can be supplied through additional properties, including
`conversation_id`, `user_id`, `custom_variables`, `meta_data`, and `parameters`. If `user_id` is
omitted, an ephemeral ID is generated for that request; pass both `user_id` and `conversation_id`
to continue a Coze conversation. Coze bots provide their own system instructions; requests accept
text-only user/assistant messages. Chats requiring client-submitted native tool outputs are not
supported. The mainland China API host is not configured. No live credentialed API verification
has been performed.

Official references:
[chat v3](https://www.coze.com/docs/developer_guides/chat_v3),
[published bot discovery](https://www.coze.com/docs/developer_guides/published_bots_list),
[official Python SDK](https://github.com/coze-dev/coze-py).

### GigaChat

Supply the base64 Authorization key from the GigaChat developer console with `X-GigaChat-Key`
(or `AIServiceConfig.GigaChat.ApiKey` in the AzureAuth sample). The key is exchanged for a bearer
token through GigaChat OAuth for each request using the documented `GIGACHAT_API_PERS` scope.

Models are discovered dynamically from `https://api.giga.chat/v1/models`; only entries of type `chat`
are exposed, with their native IDs and metadata preserved. Chat Completions and streaming use
`https://api.giga.chat/v1/chat/completions`. Responses, Messages, and UI chat use the existing unified
chat mapping. Standard function tools are translated to GigaChat v1 `functions`/`function_call`
messages and responses; the v1 API supports one function call per assistant message. Token usage is
returned by the provider's compatible completion response, and native chat options continue through
the existing additional-properties/provider-metadata passthrough.

Some hosting environments require the Russian National Certification Authority root certificate for
TLS validation. Follow the [GigaChat certificate guide](https://developers.sber.ru/docs/ru/gigachat/certificates)
to obtain the certificate and install it in the operating-system trust store used by the .NET process.
On Windows, import it into the Local Computer **Trusted Root Certification Authorities** store. For
Azure Linux/custom containers, add the certificate to the image's CA bundle (for Debian/Ubuntu images,
place the `.crt` file in `/usr/local/share/ca-certificates/` and run `update-ca-certificates`). The
provider uses the normal .NET/OS certificate validation; do not disable TLS certificate checks.

This integration currently supports the personal API scope and the compatible v1 chat surface; it
does not expose GigaChat embedding models or the newer v2 chat/thread contract. No live credentialed
API verification has been performed.

Official references:
[GigaChat API reference](https://developers.sber.ru/docs/ru/gigachat/api/reference/rest/gigachat-api),
[certificate installation](https://developers.sber.ru/docs/ru/gigachat/certificates),
[available models](https://developers.sber.ru/docs/ru/gigachat/models),
[official SDK](https://github.com/ai-forever/gigachat).

### SenseNova

Supply the SenseNova Token Plan API key with `X-SenseNova-Key` (or `AIServiceConfig.SenseNova.ApiKey`
in the AzureAuth sample). The provider uses `https://token.sensenova.cn/v1` with bearer authentication.
It is separate from the legacy SenseCore API and its authentication scheme.

Models are discovered dynamically from `/models`, retaining native IDs and metadata.
Text-input/text-output entries are exposed for chat; image-output entries for image generation.
Chat supports text, image inputs, SSE reasoning/text deltas and token usage through the existing
OpenAI-compatible adapters. Responses, Messages and UI chat use the existing unified chat mapping.
Native chat options are forwarded through additional properties/provider metadata without
gateway-selected sampling settings.

Image generation and editing support the native OpenAI-compatible request/response surfaces and provider options.
The generic image surface requires base64 output; URL-only results are available through the native
OpenAI image surface instead. Edits use `/images/edits` with one or more URL/Base64 image references;
uploaded images are converted to data URLs without downloading remote inputs. Generic image requests
with `files` use editing. Native options such as `watermark` and `prompt_extend` pass through unchanged.
Use JSON `images` references with additional properties for native options; the existing shared
multipart form mapper only reads standard OpenAI fields.
Editing availability is provider/model-controlled (the official client documents SenseNova U1.5 Lite);
discovery retains native input/output modality metadata without adding model IDs or tags.
Masks, image streaming and hosted agent execution are not exposed.
Agent runtimes and skills mentioned by SenseNova are client-side components, not hosted agents.
Only the mainland China Token Plan endpoint is configured; account permissions and model availability
remain provider-controlled. No live credentialed API verification has been performed.

Official references:
[chat and streaming](https://github.com/OpenSenseNova/SenseNova6.7/blob/main/API.md),
[dynamic discovery and image generation](https://github.com/OpenSenseNova/SenseNova-U1/blob/main/apps/comfyui/api_client.py),
[discovery schema tests](https://github.com/OpenSenseNova/SenseNova-U1/blob/main/tests/test_comfyui_api_models.py),
[image request format](https://github.com/OpenSenseNova/SenseNova-Skills/blob/main/skills/sn-image-base/scripts/sn_image_base/generation/openai_image.py),
[image editing](https://github.com/OpenSenseNova/SenseNova-Skills/blob/main/skills/sn-image-base/scripts/sn_image_base/generation/sensenova.py).

### ModelScope

Supply a ModelScope API token with `X-ModelScope-Key` (or `AIServiceConfig.ModelScope.ApiKey`
in the AzureAuth sample). The integration uses the global API-Inference endpoint at
`https://api-inference.modelscope.ai/v1` with bearer authentication.

Models are discovered dynamically through `/models`; their native IDs and returned metadata are
retained, and entries declaring image-generation tasks are exposed as image models. Chat
completions and streaming use `/chat/completions`, forwarding provider-native request properties
through the existing additional-properties mechanism. Image generation uses `/images/generations`
in async mode, polls the documented task endpoint, and converts returned image URLs to base64
data URLs for the existing image APIs. Responses, Messages and UI chat use the existing
chat-completions mapping. Other provider surfaces are not implemented. No live credentialed API
verification has been performed.

Official references:
[API-Inference provider and OpenAI compatibility](https://modelscope.ai/docs/model-service/API-Inference/api-provider),
[access tokens](https://modelscope.ai/docs/accounts/token),
[OpenAPI specification](https://modelscope.ai/docs/openapi),
[official image-generation client example](https://github.com/modelscope/ms-agent/blob/main/ms_agent/tools/image_generator/ms_image_gen.py).

### Leonardo.Ai

Supply a Leonardo API key with `X-Leonardo-Key` (or `AIServiceConfig.Leonardo.ApiKey` in the
AzureAuth sample). Leonardo models are discovered dynamically from the v1 platform-model listing.
Image generation uses the v1 generation and polling endpoints; Leonardo-specific request options
are forwarded from `providerOptions.leonardo`. Generation results are downloaded and returned as
base64 image data, and the raw creation/result responses are retained in `providerMetadata`.
Chat Completions, Responses, Messages, and UI chat use the existing unified image adapters, which
return once generation completes rather than streaming provider output. Image editing, video,
speech, transcription, embeddings, reranking, and decisions are not implemented. No live
credentialed API verification has been performed.

Official references:
[list platform models](https://docs.leonardo.ai/v1.0/reference/listplatformmodels),
[create image generation](https://docs.leonardo.ai/v1.0/reference/creategeneration),
[get generation results](https://docs.leonardo.ai/v1.0/reference/getgenerationbyid),
[official Python SDK](https://github.com/Leonardo-Interactive/leonardo-python-sdk).

### IBM watsonx.ai

Supply an IBM Cloud IAM API key with `X-WatsonX-Key` (or `AIServiceConfig.WatsonX.ApiKey` in the
AzureAuth sample) and configure the regional watsonx endpoint in `AIServiceConfig.WatsonX.Endpoint`.
The API key is exchanged for an IAM bearer token for each request; credentials and access tokens are
not persisted. Models are discovered dynamically from
`/ml/v1/foundation_model_specs` with IBM's `function_text_chat` filter, retaining native IDs and
model metadata.

Chat and streaming use IBM's `/ml/v1/text/chat` and `/ml/v1/text/chat_stream` endpoints with
`version=2024-05-31`. Responses, Messages, and UI chat use the existing unified chat mapping.
Provider-native parameters, including the required `project_id`, are forwarded through existing
additional-properties/provider-metadata mechanisms. Only chat surfaces and model discovery are
implemented. The regional endpoint and project ID must be supplied; no live credentialed API
verification has been performed.

Official references:
[list foundation models](https://dataplatform.cloud.ibm.com/docs/content/wsj/analyze-data/fm-api-get-models.html?context=wx),
[chat API](https://www.ibm.com/docs/en/watsonx/saas?topic=code-chat),
[IBM Cloud IAM API-key token exchange](https://cloud.ibm.com/docs/iam?topic=iam-iamtoken_from_apikey).

## Provider Support Matrix

Below is a capability matrix for all supported providers. 
Provider descriptions and links are available [here](https://chat.aihappey.com/providers).

The table below shows which endpoints each provider implements (✅), not yet implemented (❌), partially implemented (🟡) or for which an endpoint is not applicable to the provider (➖).

💬 [Chat](https://ai-sdk.dev/docs/reference/ai-sdk-ui/use-chat) | ✍️ [ChatCompletions](https://developers.openai.com/api/reference/resources/chat) | 📤 [Responses](https://developers.openai.com/api/reference/resources/responses) | 📨 [Messages](https://platform.claude.com/docs/en/api/messages) | 🖼️ [Images](https://ai-sdk.dev/docs/ai-sdk-core/image-generation) | 🎙️ [Transcriptions](https://ai-sdk.dev/docs/ai-sdk-core/transcription) | 🔊 [Speech](https://ai-sdk.dev/docs/ai-sdk-core/speech) | 🏆 [Rerank](https://ai-sdk.dev/docs/ai-sdk-core/reranking) | 🎬 [Video](https://ai-sdk.dev/docs/ai-sdk-core/video-generation) | 🧩 [Skills](https://developers.openai.com/api/reference/resources/skills) | 🧬 [Embeddings](https://developers.openai.com/api/reference/resources/embeddings) | ⚖️ [Decisions](https://developers.openai.com/api/reference/resources/embeddings)


| Provider | [💬](https://ai-sdk.dev/docs/reference/ai-sdk-ui/use-chat) | [✍️](https://developers.openai.com/api/reference/resources/chat) | [📤](https://developers.openai.com/api/reference/resources/responses) | [📨](https://platform.claude.com/docs/en/api/messages) | [🖼️](https://ai-sdk.dev/docs/ai-sdk-core/image-generation) | [🎙️](https://ai-sdk.dev/docs/ai-sdk-core/transcription) | [🔊](https://ai-sdk.dev/docs/ai-sdk-core/speech) | [🏆](https://ai-sdk.dev/docs/ai-sdk-core/reranking) | [🎬](https://ai-sdk.dev/docs/ai-sdk-core/video-generation) | [🧩](https://developers.openai.com/api/reference/resources/skills) | [🧬](https://developers.openai.com/api/reference/resources/embeddings) | [⚖️](https://developers.openai.com/api/reference/resources/decisions)
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 302AI | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ | ➖ | ➖ |
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
| AgentPhone | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
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
| Alibaba | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ |
| AllToken | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| AlphaNeural | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| AmazonBedrock | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Ambient | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Amnt | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Antbase | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Anthropic | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| AnyRouter | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
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
| Baseten | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
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
| Deepgram | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Depaza | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| DevicAI | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| DigitalOcean | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
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
| Google | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ |
| GoogleTranslate | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| GooseAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| GonkaGate | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| GPTsAPI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| GPTProto | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ❌ | ➖ | ➖ |
| Gradium | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| GreenPT | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ | ✅ |
| GrooveDev | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| Groq | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| GTranslate | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Haimaker | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Hanzo | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
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
| HuggingFace | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
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
| InceptionLabs | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
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
| Keyplex | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Kilo | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Kimrel | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Kirha | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| KissAPI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| KlingAI | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ |
| KnoxChat | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ❌ | ➖ | ➖ | ➖ |
| LangbaseAgent | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LangbasePipe | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LaoZhang | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Lara | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Lazu | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LectoAI | ✅ | ❌ | 🟡 | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LelapaAI | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Leonardo.Ai | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LexiCo | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LibertAI | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ✅ |
| LightOn | ✅ | ✅ | ❌ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Lilac | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Lingvanex | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ |
| Linkup | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LitAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| LiteRouter | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Llama | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
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
| OpenAI | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ✅ | ✅ |
| OpenCodeGo | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| OpenCodeZen | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
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
| PrimeIntellect | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| PrunaAI | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| PublicAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ❌ | ➖ | ➖ | ➖ |
| Qiniu | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| Quantanova | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| QuiverAI | ✅ | ❌ | ❌ | ❌ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Radiance | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Radient | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Railwail | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| RampRouter | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
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
| Reve | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| RewindAI | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ |
| Rime | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| RodiumAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| RoutePlex | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Router9 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
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
| SmallestAI | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| SmartAIPI | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Smooth | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Softref | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Soniox | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| SovereignEG | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| Sovinfra | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ |
| SovrGPT | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ |
| SpaceXAI | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ |
| Speechactors | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Speechify | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Speechmatics | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| StabilityAI | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| StandardCompute | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| StealthGPT | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| StepFun | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| StreamLake | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| SudoRouter | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ |
| SUFY | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| SUMMA | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| SunbirdAI | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| SunoAPI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| SUPA | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| SurferCloud | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Swarms | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Synexa | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ |
| Synthetic | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| Tapas | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Tavily | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| TEAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| TeamDay | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Telnyx | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ |
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
| Venice | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ |
| Verbatik | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Vercel | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Verda | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| VIABLELab | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| VibeKit | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Vidu | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ✅ | ➖ | ➖ |
| Vikasit | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Virouter | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Vivgrid | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ |
| VLMRun | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ |
| VoiceAI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Vogent | ✅ | ✅ | ❌ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| VoyageAI | ❌ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ | ✅ |
| Vultr | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Wafer | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| WAI | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Waslo | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| WAYSCloud | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ | ✅ |
| WaveSpeed | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| WebCrawlerAPI | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| WebsearchAPI | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| WatsonX | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| WiseRouter | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Writer | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| XiaomiMIMO | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Xpersona | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Yollomi | ✅ | ❌ | ❌ | ❌ | ✅ | ➖ | ➖ | ➖ | ❌ | ➖ | ➖ |
| YouCom | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| YourVoic | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Yutori | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Zai | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ |
| Zeabur | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Zebracat | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |
| Zenlayer | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ |
| ZenMux | ✅ | ✅ | ✅ | ✅ | ❌ | ➖ | ➖ | ➖ | ❌ | ➖ | ➖ |
| ZeroEntropy | ❌ | ❌ | ❌ | ❌ | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ | ✅ |
| ZyloAPI | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| Zyphra | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
