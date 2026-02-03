using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Common.Model.Providers.Anthropic;
using AIHappey.Common.Model.Providers.Audixa;
using AIHappey.Common.Model.Providers.Cohere;
using AIHappey.Common.Model.Providers.Google;
using AIHappey.Common.Model.Providers.Jina;
using AIHappey.Common.Model.Providers.Mistral;
using AIHappey.Common.Model.Providers.OpenAI;
using AIHappey.Common.Model.Providers.Perplexity;
using AIHappey.Common.Model.Providers.Groq;
using AIHappey.Common.Model.Providers.ElevenLabs;
using AIHappey.Common.Model.Providers.Deepgram;
using AIHappey.Common.Model.Providers.DeepInfra;
using AIHappey.Common.Model.Providers.Alibaba;
using AIHappey.Common.Model.Providers.Runware;
using AIHappey.Common.Model.Providers.XAI;
using AIHappey.Common.Model.Providers.Lingvanex;
using AIHappey.Common.Model.Providers.ModernMT;

using PollinationsProviderMetadata = AIHappey.Common.Model.Providers.Pollinations.PollinationsProviderMetadata;

using TogetherProviderMetadata = AIHappey.Common.Model.Providers.Together.TogetherProviderMetadata;
using AIHappey.Core.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NJsonSchema;
using NJsonSchema.Generation;
using AIHappey.Core.ModelProviders;

namespace AIHappey.Core.MCP.Provider;

[McpServerToolType]
public class ProviderTools
{
    [Description("Get AI Provider metadata options JSON schemas.")]
    [McpServerTool(Title = "Get AI Provider metadata options",
        Name = "ai_provider_metadata_get_schema",
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
                "deepinfra" => generator.Generate(typeof(DeepInfraSpeechProviderMetadata)),
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
                "audixa" => generator.Generate(typeof(AudixaSpeechProviderMetadata)),
                "alibaba" => generator.Generate(typeof(AlibabaImageProviderMetadata)),
                "runware" => generator.Generate(typeof(RunwareProviderMetadata)),
                "lingvanex" => generator.Generate(typeof(LingvanexProviderMetadata)),
                "modernmt" => generator.Generate(typeof(ModernMTProviderMetadata)),
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

    [Description("List all available AI provider identifiers.")]
    [McpServerTool(
        Title = "List AI providers",
        Name = "ai_providers_list",
        Idempotent = true,
        ReadOnly = true,
        OpenWorld = false)]
    public static async Task<CallToolResult?> AIProviders_List(
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
        Idempotent = true,
        ReadOnly = true,
        OpenWorld = false)]
    public static async Task<CallToolResult?> AIProvider_GetModels(
        [Description("Provider identifier")] string providerId,
         IServiceProvider services,
         CancellationToken cancellationToken)
    {
        var resolver = services.GetRequiredService<IAIModelProviderResolver>();
        var provider = await resolver.Resolve(providerId, cancellationToken);
        var models = await provider.ListModels(cancellationToken);

        return new CallToolResult()
        {
            StructuredContent = JsonNode.Parse(JsonSerializer.Serialize(new
            {
                models
            }))
        };
    }
}
