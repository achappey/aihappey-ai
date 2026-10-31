# AbacusAI media

All media requests use `POST /v1/chat/completions`. Models are selected by the
caller; `abacusai/` is removed from native model IDs. Abacus validates model
capabilities and model-specific options, so new models are not blocked by a
local allowlist. Existing chat, Responses, Messages and Decisions routes are
unchanged.

## Options

Use `providerOptions.abacusai` for raw chat-completion JSON properties. Nested
`audio`, `image_config` and `stream_options` objects merge with contract-derived
defaults; explicit provider options take precedence. Unknown properties are
forwarded without filtering. Media modalities and the actual streaming mode are
owned by the gateway. OpenAI-compatible requests accept the same raw fields via
their existing extension-data properties.

For example, image options can contain:

```json
{
  "abacusai": {
    "image_config": {
      "resolution": "2K",
      "quality": "high",
      "rewrite_prompt": false,
      "future_model_option": true
    }
  }
}
```

Speech sends `modalities: ["text", "audio"]` and `audio.voice`/`audio.format`.
The default voice is `alloy`, and the default format is `mp3`. The documented
common voices are `alloy`, `echo`, `fable`, `onyx`, `nova` and `shimmer`;
Abacus maps these to Gemini voices. Output formats are `mp3`, `wav`, `opus`,
`aac` and `flac`. Instructions and language become delivery instructions;
speed is an instruction, not a guaranteed playback rate. Streaming text parts
form one script and must agree on voice and part-level options.

Transcription sends an `input_audio` content part and verbatim-transcription
instructions. Input formats are `wav`, `mp3`, `ogg`, `flac`, `webm`, `m4a`,
`aac`, `pcm` and `mpga`. Provider options `language` and `prompt` supply
transcription context; other options are forwarded to chat completions.

Image generation and editing send `modalities: ["image"]`, an `image_config`
with prompt/count/size/aspect ratio/seed, and input images as `image_url` message
parts. Files become data URLs; URL references are preserved. Model-specific
configuration belongs in `image_config`. `num_images` is omitted for the
documented single-image models (`flux_kontext_edit`, `gpt_image15_edit`,
`imagine_art`, `magnific`, `midjourney`), unless explicitly supplied in raw options.
Output URLs are fetched through the shared public-image helper, without Abacus
authorization, and returned as inline media.

## Streaming and limitations

Speech streaming uses native SSE `delta.audio.data`, buffers incomplete base64
quartets, and emits decoded/re-encoded ordered audio chunks. A successful finish
requires audio and a `stop` completion marker; cancellation, provider errors and
truncated streams do not emit a successful finish. Usage, compute points and
reported monetary cost are preserved in provider metadata where the response
contract permits them.

Native incremental transcription is not documented. Streaming transcription is
explicitly emulated: one batch request, then completed transcript/final events,
with no artificial partials or invented timings. Transcription timestamps,
diarization and logprobs, progressive image streaming, custom sample rates and a
common image-mask parameter are not documented. Vercel requests warn for
unsupported standard mask/rate fields; OpenAI requests requiring unavailable
transcription detail or standard masks fail explicitly. A documented
model-specific mask can still be sent verbatim in raw `image_config`.

Official references: [audio capabilities](https://abacus.ai/help/route-llm/chat-completions/audio)
and [image analysis and generation](https://abacus.ai/help/developer-platform/route-llm/chat-completions/image-analysis).
