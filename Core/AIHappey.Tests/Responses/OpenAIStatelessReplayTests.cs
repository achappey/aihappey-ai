using System.Text.Json;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Responses;

public sealed class OpenAIStatelessReplayTests
{
    [Theory]
    [InlineData("openai")]
    [InlineData("spacexai")]
    public void Native_reasoning_summary_array_survives_repeated_gateway_roundtrips(string providerId)
    {
        var request = JsonSerializer.Deserialize<ResponseRequest>("""
            {"model":"openai/test","store":false,"input":[
              {"type":"reasoning","id":"rs_original","encrypted_content":"original-encrypted","summary":[{"type":"summary_text","text":"First"},{"type":"summary_text","text":"Second"}]},
              {"type":"message","role":"assistant","id":"msg_original","phase":"final_answer","content":[{"type":"output_text","text":"Done","annotations":[]}]}
            ]}
            """, ResponseJson.Default)!;

        for (var turn = 0; turn < 3; turn++)
        {
            request = request.ToUnifiedRequest(providerId).ToResponseRequest(providerId);
            var items = request.Input!.Items!;
            Assert.Equal(2, items.Count);
            var reasoning = Assert.IsType<ResponseReasoningItem>(items[0]);
            Assert.Equal("rs_original", reasoning.Id);
            Assert.Equal("original-encrypted", reasoning.EncryptedContent);
            Assert.Equal(new[] { "First", "Second" }, reasoning.Summary.Select(part => part.Text));
            Assert.Equal("final_answer", Assert.IsType<ResponseInputMessage>(items[1]).Phase);
            Assert.False(request.Store);
        }
    }

    [Fact]
    public void Chat_history_preserves_adjacent_native_assistant_message_boundaries_and_phases()
    {
        var chat = JsonSerializer.Deserialize<ChatRequest>("""
            {"model":"openai/test","messages":[{"id":"ui-uuid","role":"assistant","parts":[
              {"type":"reasoning","text":"display","providerMetadata":{"openai":{"id":"rs_chat","encrypted_content":"chat-encrypted","summary":[{"type":"summary_text","text":"Original"}]}}},
              {"type":"text","text":"Checking","providerMetadata":{"openai":{"id":"msg_commentary","phase":"commentary","output_index":1}}},
              {"type":"text","text":"Done","providerMetadata":{"openai":{"id":"msg_final","phase":"final_answer","output_index":2}}}
            ]}]}
            """, JsonSerializerOptions.Web)!;
        for (var turn = 0; turn < 3; turn++)
        {
            chat = JsonSerializer.Deserialize<ChatRequest>(JsonSerializer.Serialize(chat, JsonSerializerOptions.Web), JsonSerializerOptions.Web)!;
            var request = chat.ToUnifiedRequest("openai").ToResponseRequest("openai");
            var items = request.Input!.Items!;
            Assert.Equal(3, items.Count);
            var reasoning = Assert.IsType<ResponseReasoningItem>(items[0]);
            Assert.Equal("rs_chat", reasoning.Id);
            Assert.Equal("Original", Assert.Single(reasoning.Summary).Text);
            Assert.Equal("commentary", Assert.IsType<ResponseInputMessage>(items[1]).Phase);
            Assert.Equal("final_answer", Assert.IsType<ResponseInputMessage>(items[2]).Phase);
            Assert.False(request.Store);
        }
    }
}
