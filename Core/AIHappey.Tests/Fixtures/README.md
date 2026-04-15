# Stream fixture conventions

- Typed sample fixtures use [`*.json`](Core/AIHappey.Tests/Fixtures/README.md) and contain a JSON array of already-typed endpoint payloads.
- Raw captured stream fixtures use [`*.jsonl`](Core/AIHappey.Tests/Fixtures/README.md) and are intended for direct paste-in from real backend or standard SSE streams.
- The loader accepts both plain JSON-lines and SSE-style copy-paste with `event:`, `id:`, `retry:`, and `data:` lines.
- Blank lines terminate an SSE event, and `data: [DONE]` is ignored.
- First-pass folders are [`Fixtures/responses`](Core/AIHappey.Tests/Fixtures/responses) and [`Fixtures/api-chat`](Core/AIHappey.Tests/Fixtures/api-chat), but the loader is endpoint-agnostic so the same pattern can expand to chat completions and messages next.
