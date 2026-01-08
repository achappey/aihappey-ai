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

