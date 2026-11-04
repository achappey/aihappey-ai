using System.Reflection;
using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.HeaderAuth.Controllers;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AIHappey.Tests.Contracts;

public class NativeEndpointMetadataTests
{
    [Theory]
    [InlineData("chat", false)]
    [InlineData("chat", true)]
    [InlineData("messages", false)]
    [InlineData("messages", true)]
    [InlineData("responses", false)]
    [InlineData("responses", true)]
    public async Task NativeControllersPreserveApplicationMetadataAndRootExtensions(string endpoint, bool stream)
    {
        var json = """
            {"model":"test/model","input":"hello","messages":[],"max_tokens":100,
             "metadata":{"user_id":"user","test":"application"},
             "service_tier":"priority","future_null":null,"future_array":[],
             "future_object":{"nested":[1,true,null]},"future_empty":{}}
            """;
        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        fields["stream"] = JsonSerializer.SerializeToElement(stream);
        json = JsonSerializer.Serialize(fields);
        var provider = DispatchProxy.Create<IModelProvider, RecordingProvider>();
        var recorder = (RecordingProvider)(object)provider;
        var resolver = new Resolver(provider);
        var context = new ControllerContext { HttpContext = new DefaultHttpContext() };
        context.HttpContext.Response.Body = new MemoryStream();

        var result = endpoint switch
        {
            "chat" => await new ChatCompletionsController(resolver) { ControllerContext = context }
                .Post(JsonSerializer.Deserialize<ChatCompletionOptions>(json)!, default),
            "messages" => await new MessagesController(resolver) { ControllerContext = context }
                .Post(JsonSerializer.Deserialize<MessagesRequest>(json)!, default),
            _ => await new ResponsesController(resolver) { ControllerContext = context }
                .Post(JsonSerializer.Deserialize<ResponseRequest>(json, ResponseJson.Default)!, default)
        };
        Assert.True(result is OkObjectResult or EmptyResult);
        var sent = recorder.Request!.Value;
        Assert.Equal("model", sent.GetProperty("model").GetString());
        foreach (var name in new[] { "metadata", "service_tier", "future_null", "future_array", "future_object", "future_empty" })
            Assert.True(JsonElement.DeepEquals(fields[name], sent.GetProperty(name)), name);
        Assert.False(sent.TryGetProperty("test", out _));
        Assert.False(sent.TryGetProperty("providerOptions", out _));
        Assert.False(sent.TryGetProperty("providerMetadata", out _));
    }

    public class RecordingProvider : DispatchProxy
    {
        public JsonElement? Request { get; private set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "GetIdentifier") return "test";
            object native = args![0] switch
            {
                ChatCompletionOptions chat => chat.ToUnifiedRequest("test").ToChatCompletionOptions("test"),
                MessagesRequest messages => messages.ToUnifiedRequest("test").ToMessagesRequest("test"),
                ResponseRequest responses => responses.ToUnifiedRequest("test").ToResponseRequest("test"),
                _ => throw new NotSupportedException(method.Name)
            };
            Request = JsonSerializer.SerializeToElement(native, JsonSerializerOptions.Web);
            return method.Name switch
            {
                "CompleteChatAsync" => Task.FromResult(JsonSerializer.Deserialize<ChatCompletion>("""{"id":"chat","model":"model","choices":[]}""")!),
                "MessagesAsync" => Task.FromResult(JsonSerializer.Deserialize<MessagesResponse>("""{"id":"msg","model":"model","role":"assistant","content":[]}""")!),
                "ResponsesAsync" => Task.FromResult(JsonSerializer.Deserialize<ResponseResult>("""{"id":"resp","model":"model","output":[]}""")!),
                "CompleteChatStreamingAsync" => Empty<ChatCompletionUpdate>(),
                "MessagesStreamingAsync" => Empty<MessageStreamPart>(),
                "ResponsesStreamingAsync" => Empty<AIHappey.Responses.Streaming.ResponseStreamPart>(),
                _ => throw new NotSupportedException(method.Name)
            };
        }

        private static async IAsyncEnumerable<T> Empty<T>()
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class Resolver(IModelProvider provider) : IAIModelProviderResolver
    {
        public Task<IModelProvider> Resolve(string model, CancellationToken ct = default) => Task.FromResult(provider);
        public IModelProvider GetProvider() => provider;
        public Task<ModelResponse> ResolveModels(CancellationToken ct) => throw new NotSupportedException();
    }
}
