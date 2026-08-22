using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.OpenAI;

public sealed class OpenAIEmbeddingCompatibilityTests
{
    [Theory]
    [InlineData("\"hello\"")]
    [InlineData("[\"hello\",\"world\"]")]
    [InlineData("[1,2,3]")]
    [InlineData("[[1,2],[3,4]]")]
    public void ValidationAcceptsEveryDocumentedInputShape(string input)
    {
        ModelProviderEmbeddingCompatibilityExtensions.ValidateOpenAIEmbeddingRequest(new OpenAIEmbeddingRequest
        {
            Model = "text-embedding-3-small",
            Input = Json(input),
            EncodingFormat = "base64"
        });
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("[]")]
    [InlineData("[\"ok\",\"\"]")]
    [InlineData("[[1],[]]")]
    [InlineData("[1,\"two\"]")]
    [InlineData("[1.5]")]
    public void ValidationRejectsEmptyOrMixedInput(string input)
    {
        Assert.Throws<ArgumentException>(() =>
            ModelProviderEmbeddingCompatibilityExtensions.ValidateOpenAIEmbeddingRequest(new OpenAIEmbeddingRequest
            {
                Model = "text-embedding-3-small",
                Input = Json(input)
            }));
    }

    [Fact]
    public async Task TransportPreservesRequestFieldsAndBase64Response()
    {
        string? requestBody = null;
        var handler = new StubHandler(async (request, cancellationToken) =>
        {
            requestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse("""
                {
                  "object":"list",
                  "data":[{"object":"embedding","embedding":"AQIDBA==","index":0}],
                  "model":"text-embedding-3-small",
                  "usage":{"prompt_tokens":2,"total_tokens":2}
                }
                """, headers: new() { ["x-request-id"] = "req_123" });
        });

        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
        var result = await client.OpenAICompatibleEmbeddingRequestAsync(new OpenAIEmbeddingRequest
        {
            Model = "text-embedding-3-small",
            Input = Json("\"hello\""),
            Dimensions = 256,
            EncodingFormat = "base64",
            User = "user-1"
        });

        using var sent = JsonDocument.Parse(requestBody!);
        Assert.Equal("hello", sent.RootElement.GetProperty("input").GetString());
        Assert.Equal(256, sent.RootElement.GetProperty("dimensions").GetInt32());
        Assert.Equal("base64", sent.RootElement.GetProperty("encoding_format").GetString());
        Assert.Equal("user-1", sent.RootElement.GetProperty("user").GetString());
        Assert.Equal("AQIDBA==", result.Response.Data.Single().Embedding.GetString());
        Assert.Equal(2, result.Response.Usage.TotalTokens);
        Assert.Equal("req_123", result.Headers["x-request-id"]);
    }

    [Fact]
    public void VercelMappingForcesFloatAndMapsOptions()
    {
        var options = JsonSerializer.SerializeToElement(new { dimensions = 512, user = "user-2", encoding_format = "base64" });
        var mapped = new EmbeddingRequest
        {
            Model = "text-embedding-3-large",
            Values = ["first", "second"],
            ProviderOptions = new() { ["openai"] = options }
        }.ToOpenAIEmbeddingRequest("openai");

        Assert.Equal("float", mapped.EncodingFormat);
        Assert.Equal(512, mapped.Dimensions);
        Assert.Equal("user-2", mapped.User);
    }

    [Fact]
    public void VercelResponseOrdersEmbeddingsAndMapsUsageAndHeaders()
    {
        var openAIResponse = new OpenAIEmbeddingResponse
        {
            Model = "model",
            Data =
            [
                new() { Index = 1, Embedding = Json("[3.0,4.0]") },
                new() { Index = 0, Embedding = Json("[1.0,2.0]") }
            ],
            Usage = new() { PromptTokens = 7, TotalTokens = 7 }
        };

        var mapped = new OpenAICompatibleEmbeddingResult(
            openAIResponse,
            new Dictionary<string, string> { ["x-request-id"] = "req_456" })
            .ToEmbeddingResponse();

        Assert.Equal([1f, 2f], mapped.Embeddings.First());
        Assert.Equal([3f, 4f], mapped.Embeddings.Last());
        Assert.Equal(7, mapped.Usage!.Tokens);
        Assert.Equal("req_456", mapped.Response!.Headers!["x-request-id"]);
        Assert.Null(mapped.Response.Body);
        Assert.Null(mapped.ProviderMetadata);
        Assert.Empty(mapped.Warnings);
    }

    [Fact]
    public async Task TransportIncludesUpstreamStatusAndBodyInErrors()
    {
        using var client = new HttpClient(new StubHandler((_, _) => Task.FromResult(
            JsonResponse("{\"error\":{\"message\":\"bad input\"}}", HttpStatusCode.BadRequest))))
        {
            BaseAddress = new Uri("https://example.test/")
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.OpenAICompatibleEmbeddingRequestAsync(new OpenAIEmbeddingRequest
            {
                Model = "model",
                Input = Json("\"hello\"")
            }));

        Assert.Contains("400", exception.Message);
        Assert.Contains("bad input", exception.Message);
    }

    [Fact]
    public async Task TransportPropagatesCancellation()
    {
        using var client = new HttpClient(new StubHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return JsonResponse("{}");
        })) { BaseAddress = new Uri("https://example.test/") };
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.OpenAICompatibleEmbeddingRequestAsync(new OpenAIEmbeddingRequest
            {
                Model = "model",
                Input = Json("\"hello\"")
            }, cancellationToken: source.Token));
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static HttpResponseMessage JsonResponse(
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK,
        Dictionary<string, string>? headers = null)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        foreach (var header in headers ?? [])
            response.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return response;
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }
}
