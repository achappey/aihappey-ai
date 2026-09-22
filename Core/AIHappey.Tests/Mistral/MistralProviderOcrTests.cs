using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Mistral;
using AIHappey.Unified.Models;
using Microsoft.Extensions.Caching.Memory;
using ModelContextProtocol.Protocol;

namespace AIHappey.Tests.Mistral;

public sealed class MistralProviderOcrTests
{
    [Fact]
    public async Task ExecuteOcrProcessesAllLatestUserFilesAndMapsToolMarkdownAndImages()
    {
        var requests = new List<string>();
        var image = Convert.ToBase64String([1, 2, 3]);
        var responses = new Queue<string>(
        [
            JsonSerializer.Serialize(new
            {
                pages = new[] { new { index = 0, markdown = "# First", images = new[] { new { id = "figure.jpeg", image_base64 = image } } } },
                model = "mistral-ocr-latest",
                usage_info = new { pages_processed = 1 }
            }),
            """{"pages":[{"index":0,"markdown":"# Second","images":[]}],"model":"mistral-ocr-latest","usage_info":{"pages_processed":1}}"""
        ]);
        var provider = CreateProvider(async request =>
        {
            Assert.Equal("https://api.mistral.ai/v1/ocr", request.RequestUri!.AbsoluteUri);
            requests.Add(await request.Content!.ReadAsStringAsync());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responses.Dequeue(), Encoding.UTF8, "application/json")
            };
        });

        var secret = Convert.ToBase64String([9, 8, 7]);
        var result = await provider.ExecuteUnifiedAsync(CreateRequest(
            new AIInputItem
            {
                Role = "user",
                Content = [new AIFileContentPart { Type = "file", Filename = "ignored.pdf", MediaType = "application/pdf", Data = Convert.ToBase64String([0]) }]
            },
            new AIInputItem
            {
                Role = "user",
                Content =
                [
                    new AIFileContentPart { Type = "file", Filename = "first.pdf", MediaType = "application/pdf", Data = secret },
                    new AIFileContentPart { Type = "file", Filename = "second.png", MediaType = "image/png", Data = "data:image/png;base64," + Convert.ToBase64String([6]) }
                ]
            }));

        Assert.Equal(2, requests.Count);
        Assert.All(requests, body =>
        {
            using var json = JsonDocument.Parse(body);
            Assert.Equal("mistral-ocr-latest", json.RootElement.GetProperty("model").GetString(), ignoreCase: true);
            Assert.True(json.RootElement.GetProperty("include_image_base64").GetBoolean());
        });
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(result));

        var items = result.Output!.Items!;
        Assert.Equal(4, items.Count);
        var tool = Assert.Single(items[0].Content!.OfType<AIToolCallContentPart>());
        Assert.True(tool.ProviderExecuted);
        Assert.Equal("mistral_ocr", tool.ToolName);
        var toolResult = Assert.IsType<CallToolResult>(tool.Output);
        Assert.Equal("mistral-ocr-latest", toolResult.StructuredContent!.Value.GetProperty("model").GetString(), ignoreCase: true);

        Assert.Equal("# First", Assert.Single(items[1].Content!.OfType<AITextContentPart>()).Text);
        var returnedImage = Assert.Single(items[1].Content!.OfType<AIFileContentPart>());
        Assert.Equal("image/jpeg", returnedImage.MediaType);
        Assert.Equal("figure.jpeg", returnedImage.Filename);
        Assert.Equal("# Second", Assert.Single(items[3].Content!.OfType<AITextContentPart>()).Text);

        var usage = Assert.IsType<Dictionary<string, object?>>(result.Usage);
        Assert.Equal(2, Assert.IsType<int>(usage["pages_processed"]));
        Assert.Equal(2, Assert.IsType<int>(result.Metadata!["mistral.ocr.pages_processed"]));
        var gateway = Assert.IsType<Dictionary<string, object?>>(result.Metadata["gateway"]);
        Assert.Equal(0.007m, Assert.IsType<decimal>(gateway["cost"]));
    }

    [Fact]
    public async Task ExecuteOcrPrefersReportedPageUsageAndFallsBackToReturnedPages()
    {
        var responses = new Queue<string>(
        [
            """{"pages":[{"index":0,"markdown":"one","images":[]}],"model":"mistral-ocr-latest","usage_info":{"pages_processed":3}}""",
            """{"pages":[{"index":0,"markdown":"two","images":[]},{"index":1,"markdown":"three","images":[]}],"model":"mistral-ocr-latest"}"""
        ]);
        var provider = CreateProvider(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responses.Dequeue(), Encoding.UTF8, "application/json")
        }));

        var result = await provider.ExecuteUnifiedAsync(CreateRequest(
            new AIInputItem
            {
                Role = "user",
                Content =
                [
                    new AIFileContentPart { Type = "file", Filename = "reported.pdf", MediaType = "application/pdf", Data = Convert.ToBase64String([1]) },
                    new AIFileContentPart { Type = "file", Filename = "fallback.pdf", MediaType = "application/pdf", Data = Convert.ToBase64String([2]) }
                ]
            }));

        var usage = Assert.IsType<Dictionary<string, object?>>(result.Usage);
        Assert.Equal(5, Assert.IsType<int>(usage["pages_processed"]));
        Assert.Equal(5, Assert.IsType<int>(result.Metadata!["mistral.ocr.pages_processed"]));
        var gateway = Assert.IsType<Dictionary<string, object?>>(result.Metadata["gateway"]);
        Assert.Equal(0.0175m, Assert.IsType<decimal>(gateway["cost"]));
    }

    [Fact]
    public async Task ExecuteOcrRejectsRemoteUrlsBeforeSending()
    {
        var called = false;
        var provider = CreateProvider(_ =>
        {
            called = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => provider.ExecuteUnifiedAsync(CreateRequest(
            new AIInputItem
            {
                Role = "user",
                Content = [new AIFileContentPart { Type = "file", Filename = "remote.pdf", MediaType = "application/pdf", Data = "https://example.com/a.pdf" }]
            })));

        Assert.Contains("remote URL", exception.Message, StringComparison.Ordinal);
        Assert.False(called);
    }

    [Fact]
    public async Task StreamOcrEmitsToolTextImageAndSingleFinishInOrder()
    {
        var image = Convert.ToBase64String([4, 5]);
        var provider = CreateProvider(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    pages = new[] { new { index = 0, markdown = "text", images = new[] { new { id = "image.png", image_base64 = image } } } },
                    model = "mistral-ocr-latest"
                }),
                Encoding.UTF8,
                "application/json")
        }));

        var events = new List<AIStreamEvent>();
        await foreach (var item in provider.StreamUnifiedAsync(CreateRequest(
            new AIInputItem
            {
                Role = "user",
                Content = [new AIFileContentPart { Type = "file", Filename = "one.pdf", MediaType = "application/pdf", Data = Convert.ToBase64String([1]) }]
            })))
            events.Add(item);

        Assert.Equal(
            ["tool-input-available", "tool-output-available", "text-start", "text-delta", "text-end", "file", "finish"],
            events.Select(item => item.Event.Type));

        var finish = Assert.IsType<AIFinishEventData>(events[^1].Event.Data);
        Assert.Equal(0.0035m, finish.MessageMetadata?.Gateway?.Cost);
        var gateway = Assert.IsType<Dictionary<string, object?>>(events[^1].Metadata!["gateway"]);
        Assert.Equal(0.0035m, Assert.IsType<decimal>(gateway["cost"]));
    }

    [Fact]
    public async Task ExecuteOcrTranslatesJsonSchemaAndReturnsDocumentAnnotationAsAssistantText()
    {
        string? requestBody = null;
        var image = Convert.ToBase64String([7, 8]);
        var annotation = """{"invoice_number":"INV-42","total":19.95}""";
        var provider = CreateProvider(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    pages = new[]
                    {
                        new
                        {
                            index = 0,
                            markdown = "# This markdown must not be returned",
                            images = new[] { new { id = "receipt.png", image_base64 = image } }
                        }
                    },
                    document_annotation = annotation,
                    model = "mistral-ocr-latest",
                    usage_info = new { pages_processed = 1 }
                }), Encoding.UTF8, "application/json")
            };
        });

        var responseFormat = JsonSerializer.SerializeToElement(new
        {
            type = "json_schema",
            json_schema = new
            {
                name = "invoice",
                description = "Extract the invoice summary.",
                strict = true,
                schema = new
                {
                    type = "object",
                    properties = new
                    {
                        invoice_number = new { type = "string" },
                        total = new { type = "number" }
                    },
                    required = new[] { "invoice_number", "total" },
                    additionalProperties = false
                }
            }
        });
        var result = await provider.ExecuteUnifiedAsync(CreateRequest(responseFormat,
            new AIInputItem
            {
                Role = "user",
                Content =
                [
                    new AITextContentPart { Type = "text", Text = "Extract only the requested invoice fields." },
                    new AITextContentPart { Type = "text", Text = "Use the printed total." },
                    new AIFileContentPart
                    {
                        Type = "file",
                        Filename = "invoice.pdf",
                        MediaType = "application/pdf",
                        Data = Convert.ToBase64String([1, 2, 3])
                    }
                ]
            }));

        using var payload = JsonDocument.Parse(requestBody!);
        var root = payload.RootElement;
        Assert.Equal(
            "Extract only the requested invoice fields.\n\nUse the printed total.",
            root.GetProperty("document_annotation_prompt").GetString());
        var format = root.GetProperty("document_annotation_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        var jsonSchema = format.GetProperty("json_schema");
        Assert.Equal("invoice", jsonSchema.GetProperty("name").GetString());
        Assert.Equal("Extract the invoice summary.", jsonSchema.GetProperty("description").GetString());
        Assert.True(jsonSchema.GetProperty("strict").GetBoolean());
        Assert.Equal("object", jsonSchema.GetProperty("schema").GetProperty("type").GetString());
        Assert.False(jsonSchema.TryGetProperty("schema_definition", out _));

        var message = result.Output!.Items!.Single(item => item.Type == "message");
        Assert.Equal(annotation, Assert.Single(message.Content!.OfType<AITextContentPart>()).Text);
        var returnedImage = Assert.Single(message.Content!.OfType<AIFileContentPart>());
        Assert.Equal("receipt.png", returnedImage.Filename);
        Assert.DoesNotContain("markdown must not be returned", JsonSerializer.Serialize(message), StringComparison.Ordinal);

        var tool = Assert.Single(result.Output.Items![0].Content!.OfType<AIToolCallContentPart>());
        var toolResult = Assert.IsType<CallToolResult>(tool.Output);
        Assert.Equal(annotation, toolResult.StructuredContent!.Value.GetProperty("document_annotation").GetString());
    }

    [Fact]
    public async Task ExecuteOcrMapsJsonObjectModeAndOmitsBlankPrompt()
    {
        string? requestBody = null;
        var provider = CreateProvider(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"pages":[{"index":0,"markdown":"ignored","images":[]}],"document_annotation":"{\"value\":42}","model":"mistral-ocr-latest"}""",
                    Encoding.UTF8,
                    "application/json")
            };
        });

        var result = await provider.ExecuteUnifiedAsync(CreateRequest(
            JsonSerializer.SerializeToElement(new { type = "json_object" }),
            new AIInputItem
            {
                Role = "user",
                Content =
                [
                    new AITextContentPart { Type = "text", Text = "   " },
                    new AIFileContentPart
                    {
                        Type = "file",
                        Filename = "document.pdf",
                        MediaType = "application/pdf",
                        Data = Convert.ToBase64String([1])
                    }
                ]
            }));

        using var payload = JsonDocument.Parse(requestBody!);
        Assert.Equal("json_object", payload.RootElement
            .GetProperty("document_annotation_format")
            .GetProperty("type")
            .GetString());
        Assert.False(payload.RootElement.TryGetProperty("document_annotation_prompt", out _));
        var message = result.Output!.Items!.Single(item => item.Type == "message");
        Assert.Equal("{\"value\":42}", Assert.Single(message.Content!.OfType<AITextContentPart>()).Text);
    }

    [Fact]
    public async Task StreamOcrStructuredOutputUsesDocumentAnnotationInNormalTextEvents()
    {
        var provider = CreateProvider(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"pages":[{"index":0,"markdown":"not returned","images":[]}],"document_annotation":"{\"name\":\"Ada\"}","model":"mistral-ocr-latest"}""",
                Encoding.UTF8,
                "application/json")
        }));
        var responseFormat = JsonSerializer.SerializeToElement(new
        {
            type = "json_schema",
            json_schema = new
            {
                name = "person",
                schema = new { type = "object", properties = new { name = new { type = "string" } } }
            }
        });

        var events = new List<AIStreamEvent>();
        await foreach (var item in provider.StreamUnifiedAsync(CreateRequest(responseFormat,
            new AIInputItem
            {
                Role = "user",
                Content = [new AIFileContentPart
                {
                    Type = "file",
                    Filename = "person.pdf",
                    MediaType = "application/pdf",
                    Data = Convert.ToBase64String([1])
                }]
            })))
        {
            events.Add(item);
        }

        Assert.Equal(
            ["tool-input-available", "tool-output-available", "text-start", "text-delta", "text-end", "finish"],
            events.Select(item => item.Event.Type));
        Assert.Equal("{\"name\":\"Ada\"}", Assert.IsType<AITextDeltaEventData>(events[3].Event.Data).Delta);
        Assert.DoesNotContain(events, item => item.Event.Type.StartsWith("data-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteOcrRejectsMalformedJsonSchemaBeforeSending()
    {
        var called = false;
        var provider = CreateProvider(_ =>
        {
            called = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        var responseFormat = JsonSerializer.SerializeToElement(new
        {
            type = "json_schema",
            json_schema = new { name = "missing_schema" }
        });

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => provider.ExecuteUnifiedAsync(CreateRequest(
            responseFormat,
            new AIInputItem
            {
                Role = "user",
                Content = [new AIFileContentPart
                {
                    Type = "file",
                    Filename = "document.pdf",
                    MediaType = "application/pdf",
                    Data = Convert.ToBase64String([1])
                }]
            })));

        Assert.Contains("json_schema.schema", exception.Message, StringComparison.Ordinal);
        Assert.False(called);
    }

    private static AIRequest CreateRequest(params AIInputItem[] items)
        => new()
        {
            ProviderId = "mistral",
            Model = "mistral/MISTRAL-OCR-LATEST",
            Input = new AIInput { Items = [.. items] }
        };

    private static AIRequest CreateRequest(object responseFormat, params AIInputItem[] items)
        => new()
        {
            ProviderId = "mistral",
            Model = "mistral/MISTRAL-OCR-LATEST",
            ResponseFormat = responseFormat,
            Input = new AIInput { Items = [.. items] }
        };

    private static MistralProvider CreateProvider(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        => new(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(new HttpClient(new StaticResponseHttpMessageHandler(responder))));

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request);
    }
}
