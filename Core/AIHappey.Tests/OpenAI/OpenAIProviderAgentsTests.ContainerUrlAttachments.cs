using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Providers.OpenAI;
using AIHappey.Responses;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AIHappey.Tests.OpenAI;

public sealed partial class OpenAIProviderAgentsTests
{
    [Theory]
    [InlineData("shell", false, false)]
    [InlineData("shell", true, false)]
    [InlineData("code_interpreter", false, false)]
    [InlineData("code_interpreter", true, false)]
    [InlineData("shell", false, true)]
    [InlineData("shell", true, true)]
    [InlineData("code_interpreter", false, true)]
    [InlineData("code_interpreter", true, true)]
    public async Task Container_url_inputs_are_preserved_without_fetching_and_only_uploads_are_staged(
        string toolType, bool mixed, bool reuseContainer)
    {
        var downloads = 0;
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.MapGet("/attachment", context =>
        {
            Interlocked.Increment(ref downloads);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return context.Response.WriteAsync("This URL must never be fetched by the gateway.");
        });
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single();
            var source = address + "/attachment?signature=keep%2Fexact&key=1";
            ResponseContentPart[] remoteInputs =
            [
                new InputImagePart { ImageUrl = source, Detail = "high" },
                new InputFilePart { FileUrl = source, Filename = "report.pdf" },
                new InputImagePart { ImageUrl = "https://example.invalid/picture?signature=a%2Fb" },
                new InputFilePart { FileUrl = "https://example.invalid/report.pdf", Filename = "remote.pdf" },
                new InputImagePart { ImageUrl = "https://api.openai.com/v1/files/file_remote/content" }
            ];
            List<ResponseContentPart> parts = [new InputTextPart("Inspect these attachments"), .. remoteInputs];
            if (mixed)
                parts.AddRange(
                [
                    new InputImagePart { ImageUrl = "data:image/jpeg;base64,aGVsbG8=" },
                    new InputFilePart { FileData = "data:application/pdf;base64,aGVsbG8=", Filename = "embedded.pdf" },
                    new InputImagePart { FileId = "file_image" },
                    new InputFilePart { FileId = "file_document", Filename = "existing.pdf" }
                ]);

            var originalRemoteJson = JsonSerializer.Serialize(remoteInputs, ResponseJson.Default);
            var message = new ResponseInputMessage { Role = ResponseRole.User, Content = new ResponseMessageContent(parts) };
            var originalContent = message.Content;
            var request = new ResponseRequest
            {
                Model = "openai/gpt-fixture",
                Input = new ResponseInput([message]),
                Tools = [new ResponseToolDefinition { Type = toolType }],
                Metadata = reuseContainer ? new() { ["openai"] = new { container = new { id = "cntr_attachment" } } } : null
            };
            var providerCalls = 0;
            var uploadedMediaTypes = new List<string>();
            var copiedFileIds = new List<string>();
            using var handler = new StaticResponseHttpMessageHandler(upstream =>
            {
                providerCalls++;
                Assert.Equal("Bearer", upstream.Headers.Authorization?.Scheme);
                switch (upstream.RequestUri!.AbsolutePath)
                {
                    case "/v1/containers" when mixed && !reuseContainer:
                        Assert.Equal(HttpMethod.Post, upstream.Method);
                        return JsonResponse(new { id = "cntr_attachment" });
                    case "/v1/containers/cntr_attachment" when reuseContainer:
                        Assert.Equal(HttpMethod.Get, upstream.Method);
                        return JsonResponse(new { id = "cntr_attachment", status = "running" });
                    case "/v1/containers/cntr_attachment/files" when mixed:
                        Assert.Equal(HttpMethod.Post, upstream.Method);
                        if (upstream.Content is MultipartFormDataContent multipart)
                        {
                            var file = Assert.Single(multipart);
                            uploadedMediaTypes.Add(file.Headers.ContentType!.MediaType!);
                            Assert.Equal("hello", file.ReadAsStringAsync().GetAwaiter().GetResult());
                        }
                        else
                        {
                            using var body = JsonDocument.Parse(upstream.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                            copiedFileIds.Add(body.RootElement.GetProperty("file_id").GetString()!);
                        }
                        return JsonResponse(new { id = "cfile_attachment", path = "/mnt/data/attachment", container_id = "cntr_attachment" });
                    default:
                        throw new InvalidOperationException($"Unexpected upstream call: {upstream.Method} {upstream.RequestUri}");
                }
            });
            using var openaiClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/") };
            openaiClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "provider-secret");
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var provider = new OpenAIProvider(new StaticApiKeyResolver(), new StaticHttpClientFactory(openaiClient),
                new AsyncCacheHelper(cache), new StaticEndUserIdResolver());
            var prepare = typeof(OpenAIProvider).GetMethod("PrepareContainerAttachmentsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

            await ((Task)prepare.Invoke(provider, [request, CancellationToken.None])!).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(0, downloads);
            Assert.Equal(mixed ? 5 : reuseContainer ? 1 : 0, providerCalls);
            var retained = message.Content.Parts!;
            Assert.Equal(originalRemoteJson, JsonSerializer.Serialize(retained.Where(part => remoteInputs.Contains(part)), ResponseJson.Default));
            Assert.Equal("Inspect these attachments", Assert.IsType<InputTextPart>(retained[0]).Text);
            if (mixed)
            {
                Assert.Equal(new[] { "image/jpeg", "application/pdf" }, uploadedMediaTypes);
                Assert.Equal(new[] { "file_image", "file_document" }, copiedFileIds);
                Assert.Equal(remoteInputs.Length + 2, retained.Count);
                Assert.Contains("/mnt/data/attachment", Assert.IsType<InputTextPart>(retained.Last()).Text);
            }
            else
            {
                Assert.Same(originalContent, message.Content);
                Assert.Equal(remoteInputs.Length + 1, retained.Count);
                Assert.Empty(uploadedMediaTypes);
                Assert.Empty(copiedFileIds);
            }
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
