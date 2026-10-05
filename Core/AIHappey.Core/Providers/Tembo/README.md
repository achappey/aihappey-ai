# Tembo

## Model discovery

Models are discovered live and cached per API credential for five minutes (with jitter):

- `tembo/agents/{agent UUID}`: non-archived configured agents.
- `tembo/{runtime}:{model}`: available runtime-compatible organization models, preserving the existing direct-session ID syntax.

Discovery paginates agents, models, and runtimes. Unavailable runtimes, unavailable compatible references, and disabled/unavailable organization models are excluded. No stale static model catalog is used.

Every conversational request creates a fresh task/session, not a continuation. The last user message and system instructions form the automatic text task. Upstream tool definitions, structured response negotiation, and automatic multimodal task conversion are unsupported. Native rich content can be supplied through a payload.

## Provider metadata

The unified metadata key is `tembo`. Vercel chat supplies it under `providerMetadata.tembo`; Chat Completions, Responses, and Messages supply it under `metadata.tembo`.

Gateway-only controls:

- `waitForCompletion`: boolean, default `true`.
- `pollIntervalSeconds`: finite number from 0.05 to 300, default 5.
- `pollTimeoutSeconds`: finite number from 0.05 to 86400, default 1800. Covers submission, polling, and final output retrieval.
- `payload`: optional native JSON object sent unchanged as the POST body. No defaults are inserted. Keys nested inside this object, even names matching gateway controls, remain untouched.

Without `payload`, flat metadata fields other than these controls are passed through. Direct sessions default `description` to the task text, `agent` to the selected runtime/model, and `queueRightAway` to `true`. Configured-agent event input defaults `prompt` to the task text and is sent without an `eventPayload` wrapper. Explicit fields win over defaults. Nested values and nulls are preserved.

An explicit direct-session payload must contain a nonempty `description`. `queueRightAway=false` requires `waitForCompletion=false`; this returns the created-session acknowledgement without waiting for execution.

## Configured-agent limitation: explicit queue-only execution

The current create-run response returns `jobId`, while retrieve-run requires `runId`. Neither the supplied OpenAPI nor the inspected Tembo SDK establishes their relationship or enumerates run statuses. The gateway **does not assume these identifiers are interchangeable**, list-match by time/prompt, or report queued work as completed.

Configured-agent models currently require `waitForCompletion=false`. Otherwise a clear `NotSupportedException` is thrown **before submission**. Queue-only results retain the queued state, agent ID, job ID, and raw response, with text explicitly stating that execution has not completed. The stream finish marks the end of the gateway acknowledgement, not completion of remote work.

Example native agent event:

```json
{
  "tembo": {
    "waitForCompletion": false,
    "payload": { "issueUrl": "https://example.com/issues/123", "context": { "priority": "high" } }
  }
}
```

## Direct-session completion and output

Direct sessions use the returned session UUID and targeted session retrieval. Completion comes only from the documented state object, never from pull-request existence. Unknown or inconsistent states throw explicit exceptions. Failure and cancellation are not successful results.

Recorded events are fully paginated, deduplicated by numeric ID, and emitted as `data-tembo-session-event` with their original scope, phases, snapshots, and provider fields. The API does not document event-type vocabulary or nested provider-message encoding, so those envelopes are **not guessed into text/tool deltas**. Canonical assistant text comes from session-filtered persisted messages, ordered by creation timestamp; user/system messages are not answers. Consequently conversational text arrives after completion, while recorded provider data can arrive during polling. It is not a native token stream.

All seven artifact types are queried explicitly by session. Metadata preserves artifact records; documented session, pull-request, source, and service URLs are exposed. Asset download URLs and token usage are not invented. Responses and streaming share the same unified output, with synthetic provider-executed submission tools.

Polling timeout or caller cancellation stops local tracking only. Remote work may continue. POST submissions are never automatically retried; temporary GET failures have bounded retries. HTTP error messages retain status and route but omit potentially sensitive upstream bodies and submitted payloads.

## Removed legacy behavior

The generic `tembo/agent` model, `keyOrId` automation routing, legacy task/create and task/list APIs, automation trigger route, heuristic task correlation, and Postman transport headers are removed.

Contract sources inspected: https://docs.tembo.io/llms.txt, the current Tembo public OpenAPI, and https://github.com/tembo/sdk/blob/main/src/resources/agents/runs.ts (2026-10-05).
