using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;

namespace AIHappey.Tests.ChatCompletions;

public sealed class ChatCompletionSseParsingTests
{
    [Fact]
    public async Task Data_lines_are_joined_per_event_and_done_stops_the_stream()
    {
        const string responseBody =
            "data: {\n" +
            "data:   \"choices\": [],\n" +
            "data:   \"created\": 1\n" +
            "data: }\n\n" +
            "data: {\"choices\":[],\"created\":2}\n\n" +
            "data: [DONE]\n\n" +
            "data: {\"choices\":[],\"created\":3}\n\n";
        using var client = CreateClient(responseBody);

        var events = await Collect(client.GetChatCompletionSseEvents(new ChatCompletionOptions
        {
            Model = "test"
        }, "test"));

        Assert.Equal(2, events.Count);
        Assert.Equal(1, GetCreated(events[0]));
        Assert.Equal(2, GetCreated(events[1]));
    }

    [Fact]
    public async Task Comment_and_unterminated_final_data_event_are_preserved()
    {
        using var client = CreateClient(": keepalive\n\ndata: {\"choices\":[],\"created\":1}");

        var events = await Collect(client.GetChatCompletionSseEvents(new ChatCompletionOptions
        {
            Model = "test"
        }, "test"));

        Assert.Collection(events,
            item => Assert.Equal("keepalive", item.Comment),
            item => Assert.Equal(1, GetCreated(item)));
    }

    private static HttpClient CreateClient(string body)
        => new(new StaticResponseHandler(body)) { BaseAddress = new Uri("https://example.test/") };

    private static int GetCreated(ChatCompletionSseEvent item)
    {
        using var document = JsonDocument.Parse(item.Data!);
        return document.RootElement.GetProperty("created").GetInt32();
    }

    private static async Task<List<ChatCompletionSseEvent>> Collect(
        IAsyncEnumerable<ChatCompletionSseEvent> events)
    {
        var result = new List<ChatCompletionSseEvent>();
        await foreach (var item in events)
            result.Add(item);
        return result;
    }

    private sealed class StaticResponseHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
            });
    }
}
