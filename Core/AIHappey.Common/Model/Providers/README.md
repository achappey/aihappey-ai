# Provider metadata models

This folder contains provider-specific request metadata models used for schema generation and strongly-typed deserialization.

## Conventions

- Providers are grouped by folder and namespace, e.g. `AIHappey.Common.Model.Providers.OpenAI`.
- Prefer **one type per file**.
- Keep JSON contract stable by using `System.Text.Json.Serialization.JsonPropertyName`.

## How it is used

- Incoming request DTOs carry provider options as opaque JSON (`Dictionary<string, JsonElement>`).
- Providers deserialize their own options via [`MetadataExtensions.GetProviderMetadata<T>()`](../Extensions/MetadataExtensions.cs:69) and friends.
- The MCP schema endpoint uses the metadata types for JSON schema generation in [`ProviderTools.AIProvider_GetMetadataSchema()`](../../AIHappey.Core/MCP/Provider/ProviderTools.cs:25).

## Runware provider notes

### Vidu Q1 Image (Runware-proxied)

- Model AIR ID: `vidu:q1@image`
- Full model id exposed by this gateway: `runware/vidu:q1@image`
- Workflows: text-to-image and image-to-image

Behavior implemented in [`RunwareProvider.ImageRequest()`](../../AIHappey.Core/Providers/Runware/RunwareProvider.Images.cs:18):

- `files` (0-7) are mapped to Runware task field `referenceImages` (multi-reference).
- If more than 7 files are provided, only the first 7 are used and a warning is emitted.
- `mask` is ignored (warning emitted).
- Size handling:
  - Allowed explicit sizes: `1920x1080`, `1440x1440`, `1080x1920` (also accepts `:` as separator).
  - If `files` are provided and no `size`/`aspectRatio` is provided, `width`/`height` are omitted (auto inferred from first reference image).

Example request (text-to-image):

```json
{
  "model": "runware/vidu:q1@image",
  "prompt": "A professional product shot on a marble surface with elegant lighting",
  "size": "1920x1080"
}
```

Example request (image-to-image with references + auto dimensions):

```json
{
  "model": "runware/vidu:q1@image",
  "prompt": "Turn this into a cinematic still, preserve identity and outfit",
  "files": [
    { "type": "file", "mediaType": "image/png", "data": "<base64>" },
    { "type": "file", "mediaType": "image/jpeg", "data": "<base64>" }
  ]
}
```

### Runway Gen-4 Image + Image Turbo (Runware-proxied)

- Model AIR IDs:
  - `runway:4@1` (Gen-4 Image)
  - `runway:4@2` (Gen-4 Image Turbo)
- Full model ids exposed by this gateway:
  - `runware/runway:4@1`
  - `runware/runway:4@2`

Behavior implemented in [`RunwareProvider.ImageRequest()`](../../AIHappey.Core/Providers/Runware/RunwareProvider.Images.cs:20):

- `files` are mapped to Runware task field `inputs.referenceImages` (max 3) as objects:
  - `image`: the file as a `data:image/...;base64,...` dataURI
  - `tag`: auto-generated as `img1`, `img2`, `img3` (so prompts can reference `@img1`, etc.)
- `runway:4@2` requires 1-3 reference images (throws if none provided).
- `mask` is ignored (warning emitted).
- Size handling: no validation against Runway's allowed-dimension list; `size` is passed as parsed `width`/`height` (or inferred from `aspectRatio`, or default 1024x1024).
- Provider-specific settings are passed through via `providerOptions.runware.providerSettings.runway` to `providerSettings.runway`.

Example request (Gen-4 Image, text-to-image):

```json
{
  "model": "runware/runway:4@1",
  "prompt": "A cinematic portrait with dramatic lighting and rich color palette",
  "size": "1920x1080"
}
```

Example request (Gen-4 Image Turbo, image-to-image w/ references):

```json
{
  "model": "runware/runway:4@2",
  "prompt": "Create a dynamic composition featuring @img1 with bold colors",
  "size": "1920x1080",
  "files": [
    { "type": "file", "mediaType": "image/png", "data": "<base64>" }
  ],
  "providerOptions": {
    "runware": {
      "providerSettings": {
        "runway": {
          "contentModeration": {
            "publicFigureThreshold": 0.5
          }
        }
      }
    }
  }
}
```

### ByteDance SeedEdit / Seedream (Runware-proxied)

- Model AIR IDs:
  - `bytedance:4@1` (SeedEdit 3.0)
  - `bytedance:5@0` (Seedream 4.0)
  - `bytedance:seedream@4.5` (Seedream 4.5)
- Full model ids exposed by this gateway:
  - `runware/bytedance:4@1`
  - `runware/bytedance:5@0`
  - `runware/bytedance:seedream@4.5`

Behavior implemented in [`RunwareProvider.ImageRequest()`](../../AIHappey.Core/Providers/Runware/RunwareProvider.Images.cs:16):

- Reference image mapping:
  - `bytedance:4@1` (SeedEdit 3.0): uploaded `files` are mapped to top-level `referenceImages` and **must contain exactly 1 image** (throws otherwise).
  - `bytedance:5@0` (Seedream 4.0): uploaded `files` are mapped to top-level `referenceImages`.
  - `bytedance:seedream@4.5`: uploaded `files` are mapped to `inputs.referenceImages`.
- Sequential images: when using `providerOptions.runware.providerSettings.bytedance.maxSequentialImages`, we enforce `referenceImages + maxSequentialImages <= 15`.
- Provider-specific settings are passed through via `providerOptions.runware.providerSettings.bytedance` to `providerSettings.bytedance`.

Example request (Seedream 4.5 + sequential images):

```json
{
  "model": "runware/bytedance:seedream@4.5",
  "prompt": "A character walking through different seasons: spring, summer, autumn, winter",
  "size": "2560x1440",
  "files": [
    { "type": "file", "mediaType": "image/png", "data": "<base64>" }
  ],
  "providerOptions": {
    "runware": {
      "providerSettings": {
        "bytedance": {
          "maxSequentialImages": 4,
          "optimizePromptMode": "standard"
        }
      }
    }
  }
}
```

