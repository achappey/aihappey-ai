using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Common.Model.Providers.Anthropic;
using AIHappey.Common.Model.Providers.Cohere;
using AIHappey.Common.Model.Providers.Google;
using AIHappey.Common.Model.Providers.Jina;
using AIHappey.Common.Model.Providers.Mistral;
using AIHappey.Common.Model.Providers.OpenAI;
using AIHappey.Common.Model.Providers.Perplexity;
using AIHappey.Common.Model.Providers.Groq;
using AIHappey.Common.Model.Providers.ElevenLabs;
using AIHappey.Common.Model.Providers.Deepgram;
using AIHappey.Common.Model.Providers.Alibaba;
using AIHappey.Common.Model.Providers.Runware;
using AIHappey.Common.Model.Providers.XAI;
using AIHappey.Common.Model.Providers.Together;
using AIHappey.Common.Model.Providers.Pollinations;

using PollinationsProviderMetadata = AIHappey.Common.Model.Providers.Pollinations.PollinationsProviderMetadata;

using TogetherProviderMetadata = AIHappey.Common.Model.Providers.Together.TogetherProviderMetadata;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NJsonSchema;
using NJsonSchema.Generation;

namespace AIHappey.Core.MCP.Provider;

[McpServerToolType]
public class ProviderTools
{
    [Description("Get AI Provider metadata options JSON schemas.")]
    [McpServerTool(Title = "Get AI Provider metadata options",
        Name = "ai_provider_metadata_get_schema",
        UseStructuredContent = true,
        Idempotent = true,
        ReadOnly = true,
        OpenWorld = false)]
    public static async Task<CallToolResult?> AIProvider_GetMetadataSchema(
        [Description("AI provider identifier")] string aiProviderId,
        RequestContext<CallToolRequestParams> requestContext,
        IServiceProvider services,
        CancellationToken ct = default) =>
        await requestContext.WithExceptionCheck(async () =>
        {
            JsonSchemaGenerator generator = new(new SystemTextJsonSchemaGeneratorSettings
            {
                SchemaType = SchemaType.JsonSchema
            });

            var providers = services.GetServices<IModelProvider>();

            JsonSchema? schema = aiProviderId.ToLowerInvariant() switch
            {
                "openai" => generator.Generate(typeof(OpenAiProviderMetadata)),
                "deepinfra" => generator.Generate(typeof(OpenAiProviderMetadata)),
                "anthropic" => generator.Generate(typeof(AnthropicProviderMetadata)),
                "google" => generator.Generate(typeof(GoogleProviderMetadata)),
                "cohere" => generator.Generate(typeof(CohereProviderMetadata)),
                "perplexity" => generator.Generate(typeof(PerplexityProviderMetadata)),
                "mistral" => generator.Generate(typeof(MistralProviderMetadata)),
                "pollinations" => generator.Generate(typeof(PollinationsProviderMetadata)),
                "xai" => generator.Generate(typeof(XAIProviderMetadata)),
                "jina" => generator.Generate(typeof(JinaProviderMetadata)),
                "groq" => generator.Generate(typeof(GroqProviderMetadata)),
                "together" => generator.Generate(typeof(TogetherProviderMetadata)),
                "elevenlabs" => generator.Generate(typeof(ElevenLabsProviderMetadata)),
                "deepgram" => generator.Generate(typeof(DeepgramSpeechProviderMetadata)),
                "alibaba" => generator.Generate(typeof(AlibabaImageProviderMetadata)),
                "runware" => generator.Generate(typeof(RunwareImageProviderMetadata)),
                _ => throw new Exception($"Provider {aiProviderId} not supported. Available providers: {JsonSerializer
                    .Serialize(new
                    {
                        providers = providers.Select(a => a.GetIdentifier())
                    }, JsonSerializerOptions.Web)}"),
            };

            return new CallToolResult()
            {
                StructuredContent = JsonNode.Parse(schema.ToJson())
            };
        });

    [Description("Get AI Provider metadata options JSON schemas.")]
    [McpServerTool(Title = "Get AI Provider metadata options",
        Name = "ai_provider_metadata_get_schema",
        Idempotent = true,
        ReadOnly = true,
        OpenWorld = false)]
    public static async Task<CallToolResult?> AIProvider_GetProviders(
          IServiceProvider services)
    {
        var providers = services.GetServices<IModelProvider>();

        return new CallToolResult()
        {
            StructuredContent = JsonNode.Parse(JsonSerializer.Serialize(new
            {
                providers = providers.Select(a => a.GetIdentifier())
            }, JsonSerializerOptions.Web))
        };
    }

    [Description("Get AI models from all providers.")]
    [McpServerTool(Title = "Get AI models",
        Name = "ai_provider_get_models",
        UseStructuredContent = true,
        Idempotent = true,
        ReadOnly = true,
        OpenWorld = false)]
    public static async Task<ModelReponse?> AIProvider_GetModels(
         IServiceProvider services,
         CancellationToken cancellationToken)
    {
        var resolver = services.GetRequiredService<IAIModelProviderResolver>();

        return await resolver.ResolveModels(cancellationToken);
    }
}
