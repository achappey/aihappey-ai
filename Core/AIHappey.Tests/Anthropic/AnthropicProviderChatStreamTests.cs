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
    public async Task MessagesAsync_uploads_raw_file_blocks_as_container_uploads_when_code_execution_tool_is_present()
    {
        const string uploadedFileId = "file_uploaded_123";
        const string originalBeta = "code-execution-2025-08-25";
        var fileBytes = Encoding.UTF8.GetBytes("name,value\nfoo,1\n");
        var expectedBase64 = Convert.ToBase64String(fileBytes);

        string? uploadBetaHeader = null;
        string? messageBetaHeader = null;
        string? uploadedContentType = null;
        string? uploadedFilenameDisposition = null;
        string? messageBody = null;

        var handler = new StaticResponseHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/files")
            {
                uploadBetaHeader = TryGetSingleHeaderValue(request, "anthropic-beta");
                var multipart = Assert.IsType<MultipartFormDataContent>(request.Content);
                var fileContent = Assert.Single(multipart);
                uploadedContentType = fileContent.Headers.ContentType?.MediaType;
                uploadedFilenameDisposition = fileContent.Headers.ContentDisposition?.FileName;
                var uploadedBytes = fileContent.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                Assert.Equal(fileBytes, uploadedBytes);

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        id = uploadedFileId,
                        filename = "data.csv",
                        mime_type = "text/csv"
                    }), Encoding.UTF8, "application/json")
                };
            }

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/messages")
            {
                messageBetaHeader = TryGetSingleHeaderValue(request, "anthropic-beta");
                messageBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        id = "msg_1",
                        type = "message",
                        role = "assistant",
                        model = "claude-haiku-4-5-20251001",
                        content = new[] { new { type = "text", text = "ok" } },
                        stop_reason = "end_turn",
                        usage = new { input_tokens = 1, output_tokens = 1 }
                    }), Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"Unhandled request: {request.Method} {request.RequestUri}")
            };
        });

        var provider = CreateProvider(handler);
        var request = CreateMessagesRequestWithFile(expectedBase64, includeCodeExecutionTool: true);

        await provider.MessagesAsync(request, new Dictionary<string, string> { ["anthropic-beta"] = originalBeta });

        Assert.Equal(FilesApiBeta, uploadBetaHeader);
        Assert.Equal("text/csv", uploadedContentType);
        Assert.Equal("\"data.csv\"", uploadedFilenameDisposition);
        Assert.Contains(originalBeta, messageBetaHeader);
        Assert.Contains(FilesApiBeta, messageBetaHeader);
        Assert.NotNull(messageBody);

        using var document = JsonDocument.Parse(messageBody!);
        var content = document.RootElement.GetProperty("messages")[0].GetProperty("content");
        var uploadBlock = content.EnumerateArray().Single(block => block.GetProperty("type").GetString() == "container_upload");
        Assert.Equal(uploadedFileId, uploadBlock.GetProperty("file_id").GetString());
        Assert.DoesNotContain(expectedBase64, messageBody);
    }

    [Fact]
    public async Task MessagesAsync_keeps_raw_file_blocks_when_code_execution_tool_is_absent()
    {
        var fileBytes = Encoding.UTF8.GetBytes("name,value\nfoo,1\n");
        var expectedBase64 = Convert.ToBase64String(fileBytes);
        var fileUploadAttempted = false;
        string? messageBody = null;

        var handler = new StaticResponseHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/files")
            {
                fileUploadAttempted = true;
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/messages")
            {
                messageBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        id = "msg_1",
                        type = "message",
                        role = "assistant",
                        model = "claude-haiku-4-5-20251001",
                        content = new[] { new { type = "text", text = "ok" } },
                        stop_reason = "end_turn",
                        usage = new { input_tokens = 1, output_tokens = 1 }
                    }), Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var provider = CreateProvider(handler);
        var request = CreateMessagesRequestWithFile(expectedBase64, includeCodeExecutionTool: false);

        await provider.MessagesAsync(request, []);

        Assert.False(fileUploadAttempted);
        Assert.NotNull(messageBody);
        Assert.Contains(expectedBase64, messageBody);
        Assert.DoesNotContain("container_upload", messageBody);
    }

    [Fact]
    public async Task MessagesAsync_fails_when_anthropic_file_upload_fails()
    {
        var messageApiCalled = false;
        var handler = new StaticResponseHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/files")
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("upload failed")
                };
            }

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/messages")
                messageApiCalled = true;

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var provider = CreateProvider(handler);
        var request = CreateMessagesRequestWithFile(Convert.ToBase64String(Encoding.UTF8.GetBytes("bad")), includeCodeExecutionTool: true);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => provider.MessagesAsync(request, []));

        Assert.Contains("400", exception.Message);
        Assert.False(messageApiCalled);
    }

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
            "tool-input-start",
            "tool-input-available",
            "tool-output-available",
            "file");

        var toolOutputPart = Assert.IsType<ToolOutputAvailablePart>(uiParts.OfType<ToolOutputAvailablePart>().First());
        Assert.Equal(toolUseId, toolOutputPart.ToolCallId);
        Assert.True(toolOutputPart.ProviderExecuted);

        Assert.DoesNotContain(uiParts, part => part.Type == "source-url");

        var filePart = Assert.IsType<FileUIPart>(uiParts.Single(part => part.Type == "file"));
        Assert.Equal(mediaType, filePart.MediaType);
        Assert.Null(filePart.Filename);
        Assert.Equal($"data:{mediaType};base64,{Convert.ToBase64String(fileBytes)}", filePart.Url);

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

    private static MessagesRequest CreateMessagesRequestWithFile(string base64Data, bool includeCodeExecutionTool)
    {
        var request = new MessagesRequest
        {
            Model = "claude-haiku-4-5-20251001",
            MaxTokens = 128,
            Messages =
            [
                new MessageParam
                {
                    Role = "user",
                    Content = new MessagesContent(
                    [
                        new MessageContentBlock
                        {
                            Type = "text",
                            Text = "Analyze this CSV."
                        },
                        new MessageContentBlock
                        {
                            Type = "document",
                            Title = "data.csv",
                            Source = new MessageSource
                            {
                                Type = "base64",
                                MediaType = "text/csv",
                                Data = base64Data
                            }
                        }
                    ])
                }
            ]
        };

        if (includeCodeExecutionTool)
        {
            request.Tools =
            [
                new MessageToolDefinition
                {
                    Type = "code_execution_20250825",
                    Name = "code_execution"
                }
            ];
        }

        return request;
    }

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
