using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using AIHappey.Core.ModelProviders;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AIHappey.Core.MCP.Rerank;

[McpServerToolType]
public class RerankingTools
{
    [Description("Rerank a list of text documents for a given query using the unified reranking endpoint.")]
    [McpServerTool(
        Title = "Rerank texts",
        Name = "ai_rerank_texts",
        Idempotent = true,
        ReadOnly = true,
        OpenWorld = false)]
    public static async Task<CallToolResult?> AI_RerankTexts(
        [Description("AI model identifier")] string model,
        [Description("Query used for reranking")] string query,
        [Description("List of document texts to rerank")] IReadOnlyList<string> documents,
        RequestContext<CallToolRequestParams> requestContext,
        IServiceProvider services,
        CancellationToken ct = default) =>
        await requestContext.WithExceptionCheck(async () =>
        {
            if (string.IsNullOrWhiteSpace(model))
                throw new ArgumentException("'model' is required.");
            if (string.IsNullOrWhiteSpace(query))
                throw new ArgumentException("'query' is required.");
            if (documents is null || documents.Count == 0)
                throw new ArgumentException("'documents' must be a non-empty list.");

            var resolver = services.GetRequiredService<IAIModelProviderResolver>();
            var provider = await resolver.Resolve(model, ct);

            var valuesEl = JsonSerializer.SerializeToElement(documents, JsonSerializerOptions.Web);
            var request = new RerankingRequest
            {
                Model = model.SplitModelId().Model,
                Query = query,
                Documents = new RerankingDocument { Type = "text", Values = valuesEl },
                TopN = null,
                ProviderOptions = null
            };

            var result = await provider.RerankingRequest(request, ct);

            var structured = new JsonObject
            {
                ["ranking"] = JsonSerializer.SerializeToNode(result.Ranking, JsonSerializerOptions.Web),
                ["warnings"] = JsonSerializer.SerializeToNode(result.Warnings, JsonSerializerOptions.Web),
                ["response"] = JsonSerializer.SerializeToNode(result.Response, JsonSerializerOptions.Web)
            };

            return new CallToolResult { StructuredContent = structured };
        });


    [Description("Rerank a list of URL documents for a given query using the unified reranking endpoint. IMPORTANT: This tool accepts only publicly accessible http(s) URLs. The server will download each URL and use the raw response body as text. Fail-fast: if any URL cannot be downloaded, the tool errors.")]
    [McpServerTool(
        Title = "Rerank URLs",
        Name = "ai_rerank_urls",
        Idempotent = true,
        ReadOnly = true,
        OpenWorld = false)]
    public static async Task<CallToolResult?> AI_RerankUrls(
        [Description("AI model identifier")] string model,
        [Description("Query used for reranking")] string query,
        [Description("List of publicly accessible http(s) URLs. Only public URLs work.")] IReadOnlyList<string> urls,
        RequestContext<CallToolRequestParams> requestContext,
        IServiceProvider services,
        CancellationToken ct = default) =>
        await requestContext.WithExceptionCheck(async () =>
        {
            if (string.IsNullOrWhiteSpace(model))
                throw new ArgumentException("'model' is required.");
            if (string.IsNullOrWhiteSpace(query))
                throw new ArgumentException("'query' is required.");
            if (urls is null || urls.Count == 0)
                throw new ArgumentException("'urls' must be a non-empty list.");

            var resolver = services.GetRequiredService<IAIModelProviderResolver>();
            var httpClientFactory = services.GetRequiredService<IHttpClientFactory>();

            var docs = new List<string>(capacity: urls.Count);
            foreach (var u in urls)
            {
                if (string.IsNullOrWhiteSpace(u))
                    throw new ArgumentException("'urls' contains an empty item.");
                if (!Uri.TryCreate(u, UriKind.Absolute, out var uri))
                    throw new ArgumentException($"Invalid url in 'urls': '{u}'.");

                var text = await AIHappey.Core.MCP.Media.MediaContentHelpers.FetchExternalBodyAsTextAsync(uri, httpClientFactory, ct);
                docs.Add(text);
            }

            var provider = await resolver.Resolve(model, ct);

            var valuesEl = JsonSerializer.SerializeToElement(docs, JsonSerializerOptions.Web);
            var request = new RerankingRequest
            {
                Model = model.SplitModelId().Model,
                Query = query,
                Documents = new RerankingDocument { Type = "text", Values = valuesEl },
                TopN = null,
                ProviderOptions = null
            };

            var result = await provider.RerankingRequest(request, ct);

            var structured = new JsonObject
            {
                ["ranking"] = JsonSerializer.SerializeToNode(result.Ranking, JsonSerializerOptions.Web),
                ["warnings"] = JsonSerializer.SerializeToNode(result.Warnings, JsonSerializerOptions.Web),
                ["response"] = JsonSerializer.SerializeToNode(result.Response, JsonSerializerOptions.Web)
            };

            return new CallToolResult { StructuredContent = structured };
        });
}

