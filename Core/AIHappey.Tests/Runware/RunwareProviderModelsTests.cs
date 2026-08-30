using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Runware;
using AIHappey.Core.AI;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Runware;

public class RunwareProviderModelsTests
{
    [Fact]
    public async Task ListModels_searches_four_modalities_filters_maps_and_deduplicates()
    {
        JsonElement? capturedPayload = null;
        AuthenticationHeaderValue? capturedAuthorization = null;
        var provider = CreateProvider(async request =>
        {
            capturedAuthorization = request.Headers.Authorization;
            capturedPayload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.Clone();

            var tasks = capturedPayload.Value.EnumerateArray().ToArray();
            var responseTasks = tasks.Reverse().Select((task, responseIndex) =>
            {
                var search = task.GetProperty("search").GetString();
                var results = search switch
                {
                    "tts" => new object[]
                    {
                        Result("shared:model@1", "Shared speech", false, "io:text-to-audio", "audio"),
                        Result("wrong:model@1", "Wrong output", false, "io:text-to-image", "audio"),
                        Result("private:model@1", "Private", true, "io:text-to-audio", "audio")
                    },
                    "video" =>
                    [
                        Result("video:model@1", "Video model", false, "io:image-to-video", "video")
                    ],
                    "ai" =>
                    [
                        Result("language:model@1", "Language model", false, "io:video-to-text", "text")
                    ],
                    _ => new object[]
                    {
                        Result("image:model@1", "Image model", false, "io:text-to-image", "checkpoint"),
                        Result("shared:model@1", "Duplicate image", false, "io:text-to-image", "checkpoint"),
                        new { name = "Missing AIR", @private = false, capabilities = new[] { "io:text-to-image" } }
                    }
                };

                return new
                {
                    taskUUID = task.GetProperty("taskUUID").GetGuid(),
                    taskType = "modelSearch",
                    results,
                    totalResults = results.Length,
                    responseIndex
                };
            });

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent(new { data = responseTasks })
            };
        });

        var models = (await provider.ListModels()).ToList();

        Assert.Equal("Bearer", capturedAuthorization?.Scheme);
        Assert.Equal("test-key", capturedAuthorization?.Parameter);

        var requests = capturedPayload!.Value.EnumerateArray().ToArray();
        Assert.Equal(4, requests.Length);
        Assert.All(requests, request =>
        {
            Assert.Equal("modelSearch", request.GetProperty("taskType").GetString());
            Assert.Equal(100, request.GetProperty("limit").GetInt32());
            Assert.NotEqual(Guid.Empty, request.GetProperty("taskUUID").GetGuid());
            Assert.False(request.TryGetProperty("sort", out _));
        });
        Assert.Equal(4, requests.Select(request => request.GetProperty("taskUUID").GetGuid()).Distinct().Count());
        AssertRequest(requests[0], "tts", "audio");
        AssertRequest(requests[1], "video", "video");
        AssertRequest(requests[2], "ai", "text");
        AssertRequest(requests[3], "image", null);

        Assert.Collection(models,
            model => AssertModel(model, "runware/shared:model@1", "speech"),
            model => AssertModel(model, "runware/video:model@1", "video"),
            model => AssertModel(model, "runware/language:model@1", "language"),
            model => AssertModel(model, "runware/image:model@1", "image"));

        var image = models[^1];
        Assert.Equal("Creator", image.OwnedBy);
        Assert.Equal("Description", image.Description);
        Assert.Equal(456, image.Created);
        Assert.Contains("io:text-to-image", image.Tags!);
        Assert.Contains("category:checkpoint", image.Tags!);
        Assert.Contains("source:curated", image.Tags!);
        Assert.Contains("architecture:test-architecture", image.Tags!);
    }

    [Fact]
    public async Task ListModels_without_api_key_returns_empty_without_http_request()
    {
        var called = false;
        var provider = CreateProvider(_ =>
        {
            called = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }, apiKey: null);

        Assert.Empty(await provider.ListModels());
        Assert.False(called);
    }

    [Fact]
    public async Task ListModels_requires_a_response_for_every_search_task()
    {
        var provider = CreateProvider(async request =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var firstTask = document.RootElement.EnumerateArray().First();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent(new
                {
                    data = new[]
                    {
                        new
                        {
                            taskUUID = firstTask.GetProperty("taskUUID").GetGuid(),
                            taskType = "modelSearch",
                            results = Array.Empty<object>(),
                            totalResults = 0
                        }
                    }
                })
            };
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await provider.ListModels());

        Assert.Contains("did not include task", exception.Message);
    }

    private static object Result(string air, string name, bool isPrivate, string capability, string category)
        => new
        {
            air,
            name,
            @private = isPrivate,
            capabilities = new[] { capability, "form:checkpoint" },
            tags = new[] { "popular" },
            category,
            comment = "Description",
            architecture = "test-architecture",
            source = "curated",
            addedUnixTimestamp = 123,
            updatedDateUnixTimestamp = 456,
            creator = new { name = "Creator" }
        };

    private static void AssertRequest(JsonElement request, string search, string? category)
    {
        Assert.Equal(search, request.GetProperty("search").GetString());
        if (category is null)
            Assert.False(request.TryGetProperty("category", out _));
        else
            Assert.Equal(category, request.GetProperty("category").GetString());
    }

    private static void AssertModel(AIHappey.Core.Models.Model model, string id, string type)
    {
        Assert.Equal(id, model.Id);
        Assert.Equal(type, model.Type);
    }

    private static StringContent JsonContent(object value)
        => new(JsonSerializer.Serialize(value, JsonSerializerOptions.Web), Encoding.UTF8, MediaTypeNames.Application.Json);

    private static RunwareProvider CreateProvider(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responder,
        string? apiKey = "test-key")
        => new(
            new StaticApiKeyResolver(apiKey),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(new HttpClient(new StaticResponseHttpMessageHandler(responder))));

    private sealed class StaticApiKeyResolver(string? apiKey) : IApiKeyResolver
    {
        public string? Resolve(string provider) => apiKey;
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StaticResponseHttpMessageHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => responder(request);
    }
}
