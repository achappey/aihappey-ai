using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.UUMuse;
using AIHappey.Unified.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.UUMuse;

public class UUMuseProviderUnifiedTests
{
    private const string WorkspaceId = "dadfb531-6314-48df-82df-7b44d5c50ea9";

    [Fact]
    public async Task ExecuteUnifiedAsync_UsesMetadataWorkspaceAndMapsCitationsToSourceUrls()
    {
        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        var provider = CreateProvider(request =>
        {
            captured = request;
            capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse(
                """
                {
                  "answer":"Based on the report, revenue grew 12% YoY.",
                  "citations":[
                    {
                      "file_name":"Q3_Report.pdf",
                      "file_id":"file_123",
                      "chunk_index":3,
                      "content_preview":"Revenue increased 12% year-over-year...",
                      "source_url":"https://example.com/q3-report.pdf"
                    }
                  ],
                  "model_id":"openai/gpt-5.4",
                  "usage":{"prompt_tokens":1520,"completion_tokens":340,"total_tokens":1860}
                }
                """);
        });

        var response = await provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "uumuse",
            Model = "uumuse/openai/gpt-5.4",
            Metadata = new Dictionary<string, object?>
            {
                ["uumuse"] = new Dictionary<string, object>
                {
                    ["workspace_id"] = WorkspaceId
                }
            },
            Input = CreateInput("What are the findings?")
        });

        Assert.NotNull(captured);
        Assert.Equal("https://api.uumuse.ai/v1/ask", captured!.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", captured.Headers.Authorization?.Scheme);
        Assert.Equal("test-key", captured.Headers.Authorization?.Parameter);

        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.Equal("What are the findings?", doc.RootElement.GetProperty("question").GetString());
        Assert.Equal(WorkspaceId, doc.RootElement.GetProperty("workspace_id").GetString());
        Assert.Equal("openai/gpt-5.4", doc.RootElement.GetProperty("model_id").GetString());

        Assert.Equal("uumuse/openai/gpt-5.4@dadfb531-6314-48df-82df-7b44d5c50ea9", response.Model);
        var message = Assert.Single(response.Output!.Items!, item => item.Type == "message");
        Assert.Equal("Based on the report, revenue grew 12% YoY.", Assert.Single(message.Content!.OfType<AITextContentPart>()).Text);

        var source = Assert.Single(response.Output.Items, item => item.Type == "source-url");
        Assert.Equal("https://example.com/q3-report.pdf", source.Metadata!["chatcompletions.source.url"]?.ToString());
        Assert.Equal("Q3_Report.pdf", source.Metadata!["chatcompletions.source.title"]?.ToString());
        Assert.Equal("file_123", source.Metadata!["uumuse.citation.file_id"]?.ToString());
    }

    [Fact]
    public async Task ExecuteUnifiedAsync_UsesModelWorkspaceShortcutWhenMetadataIsAbsent()
    {
        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        var provider = CreateProvider(request =>
        {
            captured = request;
            capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse(
                """
                {
                  "answer":"shortcut answer",
                  "citations":[],
                  "model_id":"google/gemini-2.5-flash",
                  "usage":{"prompt_tokens":1,"completion_tokens":2}
                }
                """);
        });

        await provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "uumuse",
            Model = $"uumuse/google/gemini-2.5-flash@{WorkspaceId}",
            Input = CreateInput("question")
        });

        Assert.NotNull(captured);
        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.Equal(WorkspaceId, doc.RootElement.GetProperty("workspace_id").GetString());
        Assert.Equal("google/gemini-2.5-flash", doc.RootElement.GetProperty("model_id").GetString());
    }

    [Fact]
    public async Task StreamUnifiedAsync_MimicsStreamingAndIncludesSourceUrlBeforeFinish()
    {
        var provider = CreateProvider(_ =>
            JsonResponse(
                """
                {
                  "answer":"streamed answer",
                  "citations":[{"file_name":"Doc.pdf","file_id":"file_1","chunk_index":0,"content_preview":"preview"}],
                  "model_id":"deepseek-v3.2",
                  "usage":{"prompt_tokens":5,"completion_tokens":7,"total_tokens":12}
                }
                """));

        var events = await CollectAsync(provider.StreamUnifiedAsync(new AIRequest
        {
            ProviderId = "uumuse",
            Model = $"uumuse/deepseek-v3.2@{WorkspaceId}",
            Input = CreateInput("question")
        }));

        Assert.True(events.Count >= 5);
        Assert.Equal("text-start", events[0].Event.Type);
        Assert.Equal("text-delta", events[1].Event.Type);
        Assert.Equal("text-end", events[2].Event.Type);
        Assert.Contains(events, streamEvent => streamEvent.Event.Type == "source-url");
        Assert.Equal("finish", events[^1].Event.Type);

        var delta = Assert.IsType<AITextDeltaEventData>(events[1].Event.Data);
        Assert.Equal("streamed answer", delta.Delta);

        var sourceData = Assert.IsType<AISourceUrlEventData>(events.Single(streamEvent => streamEvent.Event.Type == "source-url").Event.Data);
        Assert.Equal("uumuse://files/file_1#chunk=0", sourceData.Url);
        Assert.Equal("Doc.pdf", sourceData.Title);
        Assert.Equal("file_1", sourceData.FileId);
    }

    [Fact]
    public async Task ListModels_IncludesNativeModelsAndUuidWorkspaceShortcuts()
    {
        var provider = CreateProvider(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/v1/models", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse(
                    """
                    {
                      "models":[
                        {
                          "id":"openai/gpt-5.4",
                          "name":"GPT-5.4",
                          "provider":"openrouter",
                          "tier":"advanced",
                          "context_window":272000,
                          "max_output_tokens":16384
                        }
                      ],
                      "total":1
                    }
                    """);
            }

            return JsonResponse(
                $$"""
                {
                  "workspaces":[
                    {
                      "id":"{{WorkspaceId}}",
                      "name":"aihappey",
                      "description":null,
                      "file_count":0,
                      "created_at":"2026-05-31T18:21:07.922500Z"
                    }
                  ],
                  "total":1
                }
                """);
        });

        var models = (await provider.ListModels()).ToList();

        Assert.Contains(models, model => model.Id == "uumuse/openai/gpt-5.4");
        var shortcut = Assert.Single(models, model => model.Id == $"uumuse/openai/gpt-5.4@{WorkspaceId}");
        Assert.Equal("GPT-5.4 @ aihappey", shortcut.Name);
        Assert.Contains("shortcut", shortcut.Tags!);
        Assert.Contains($"workspace:{WorkspaceId}", shortcut.Tags!);
    }

    private static AIInput CreateInput(string text)
        => new()
        {
            Items =
            [
                new AIInputItem
                {
                    Type = "message",
                    Role = "user",
                    Content =
                    [
                        new AITextContentPart { Type = "text", Text = text }
                    ]
                }
            ]
        };

    private static UUMuseProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(new HttpClient(new StaticResponseHttpMessageHandler(responder))));

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
            list.Add(item);
        return list;
    }

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "uumuse" ? "test-key" : null;
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name = "") => httpClient;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
