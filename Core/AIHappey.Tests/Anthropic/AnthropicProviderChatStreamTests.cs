using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Anthropic;
using AIHappey.Messages;
using AIHappey.Tests.TestInfrastructure;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Anthropic;

public class AnthropicProviderChatStreamTests
{
    private const string FilesApiBeta = "files-api-2025-04-14";

    [Fact]
    public async Task StreamAsync_emits_source_and_file_parts_for_provider_tool_file_outputs()
    {
        const string toolUseId = "srvtoolu_01T8zPpWxzTQyYEjcGh4kz1Q";
        const string fileId = "file_011Ca999fm79xAWCqsiYgrvH";
        const string filename = "random_data.xlsx";
        const string mediaType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
        var stdout = "total 6.5K\n-rw-r--r-- 1 root root 6.1K Apr 17 09:54 random_data.xlsx\n";
        var fileBytes = Encoding.UTF8.GetBytes("anthropic-binary-test-content");

        string? capturedBetaHeader = null;
        string? capturedApiKey = null;

        var handler = new StaticResponseHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/messages")
                return CreateStreamingResponse(CreateToolOutputFixture(toolUseId, fileId, stdout));

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == $"/v1/files/{fileId}/content")
            {
                capturedBetaHeader = TryGetSingleHeaderValue(request, "anthropic-beta");
                capturedApiKey = TryGetSingleHeaderValue(request, "X-API-Key");

                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(fileBytes)
                };

                response.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
                return response;
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"Unhandled request: {request.Method} {request.RequestUri}")
            };
        });

        var provider = CreateProvider(handler);
        var chatRequest = CreateChatRequest();

        var uiParts = await FixtureAssertions.CollectAsync(provider.StreamAsync(chatRequest));

        FixtureAssertions.AssertContainsSubsequence(
            uiParts.Select(part => part.Type).ToList(),
            "tool-output-available",
            "source-url",
            "file");

        var toolOutputPart = Assert.IsType<ToolOutputAvailablePart>(uiParts.Single(part => part.Type == "tool-output-available"));
        Assert.Equal(toolUseId, toolOutputPart.ToolCallId);
        Assert.True(toolOutputPart.ProviderExecuted);

        var sourcePart = Assert.IsType<SourceUIPart>(uiParts.Single(part => part.Type == "source-url"));
        Assert.Equal($"https://api.anthropic.com/v1/files/{fileId}/content", sourcePart.Url);
        Assert.Equal(filename, sourcePart.Title);

        var sourceProviderMetadata = Assert.Contains("anthropic", sourcePart.ProviderMetadata ?? []);
        Assert.Equal(fileId, Assert.IsType<string>(sourceProviderMetadata["file_id"]));
        Assert.Equal(toolUseId, Assert.IsType<string>(sourceProviderMetadata["tool_use_id"]));
        Assert.Equal(FilesApiBeta, Assert.IsType<string>(sourceProviderMetadata["anthropic_beta"]));
        Assert.Equal(filename, Assert.IsType<string>(sourceProviderMetadata["filename"]));

        var filePart = Assert.IsType<FileUIPart>(uiParts.Single(part => part.Type == "file"));
        Assert.Equal(mediaType, filePart.MediaType);
        Assert.Null(filePart.Filename);
        Assert.Equal(Convert.ToBase64String(fileBytes), filePart.Url);

        var fileProviderMetadata = Assert.Contains("anthropic", filePart.ProviderMetadata ?? []);
        Assert.Equal(fileId, Assert.IsType<string>(fileProviderMetadata!["file_id"]));
        Assert.Equal(filename, Assert.IsType<string>(fileProviderMetadata["filename"]));
        Assert.Equal(mediaType, Assert.IsType<string>(fileProviderMetadata["media_type"]));

        Assert.Equal(FilesApiBeta, capturedBetaHeader);
        Assert.Equal("test-api-key", capturedApiKey);
    }

    [Fact]
    public async Task StreamAsync_preserves_tool_output_and_source_when_anthropic_file_download_fails()
    {
        const string toolUseId = "srvtoolu_01T8zPpWxzTQyYEjcGh4kz1Q";
        const string fileId = "file_011Ca999fm79xAWCqsiYgrvH";
        var stdout = "total 6.5K\n-rw-r--r-- 1 root root 6.1K Apr 17 09:54 random_data.xlsx\n";

        var handler = new StaticResponseHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/messages")
                return CreateStreamingResponse(CreateToolOutputFixture(toolUseId, fileId, stdout));

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == $"/v1/files/{fileId}/content")
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("files api beta unavailable")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"Unhandled request: {request.Method} {request.RequestUri}")
            };
        });

        var provider = CreateProvider(handler);
        var uiParts = await FixtureAssertions.CollectAsync(provider.StreamAsync(CreateChatRequest()));

        Assert.Contains(uiParts, part => part.Type == "tool-output-available");
        Assert.Contains(uiParts, part => part.Type == "source-url");
        Assert.DoesNotContain(uiParts, part => part.Type == "file");
    }

    private static AnthropicProvider CreateProvider(HttpMessageHandler handler)
        => new(
            new StaticApiKeyResolver(),
            new StaticHttpClientFactory(new HttpClient(handler)));

    private static ChatRequest CreateChatRequest()
        => new()
        {
            Id = "chat-1",
            Model = "anthropic/claude-haiku-4-5-20251001",
            Messages =
            [
                new UIMessage
                {
                    Id = "msg-user-1",
                    Role = Role.user,
                    Parts =
                    [
                        new TextUIPart
                        {
                            Text = "Generate a spreadsheet and return it."
                        }
                    ]
                }
            ]
        };

    private static HttpResponseMessage CreateStreamingResponse(string body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
        };

        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return response;
    }

    private static string CreateToolOutputFixture(string toolUseId, string fileId, string stdout)
    {
        var messageStart = new MessageStreamPart
        {
            Type = "message_start",
            Message = new MessagesResponse
            {
                Id = "msg_1",
                Type = "message",
                Role = "assistant",
                Model = "claude-haiku-4-5-20251001"
            }
        };

        var contentBlockStart = new MessageStreamPart
        {
            Type = "content_block_start",
            Index = 0,
            ContentBlock = new MessageContentBlock
            {
                Type = "bash_code_execution_tool_result",
                ToolUseId = toolUseId,
                Content = new MessagesContent(JsonSerializer.SerializeToElement(new
                {
                    type = "bash_code_execution_result",
                    stdout,
                    stderr = string.Empty,
                    return_code = 0,
                    content = new[]
                    {
                        new
                        {
                            type = "bash_code_execution_output",
                            file_id = fileId
                        }
                    }
                }, JsonSerializerOptions.Web))
            }
        };

        var contentBlockStop = new MessageStreamPart
        {
            Type = "content_block_stop",
            Index = 0
        };

        return string.Join(
                   "\n\n",
                   new[]
                   {
                       messageStart,
                       contentBlockStart,
                       contentBlockStop
                   }.Select(part => $"data: {JsonSerializer.Serialize(part, MessagesJson.Default)}"))
               + "\n\n";
    }

    private static string? TryGetSingleHeaderValue(HttpRequestMessage request, string headerName)
        => request.Headers.TryGetValues(headerName, out var values)
            ? values.SingleOrDefault()
            : null;

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider)
            => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => httpClient;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responder(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
