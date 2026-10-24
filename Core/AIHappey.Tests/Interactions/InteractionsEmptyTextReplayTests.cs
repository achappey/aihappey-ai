using System.Text.Json;
using AIHappey.Interactions;
using AIHappey.Interactions.Mapping;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Interactions;

public sealed class InteractionsEmptyTextReplayTests
{
    [Theory]
    [InlineData("{\"type\":\"text\",\"text\":\"\"}")]
    [InlineData("{\"type\":\"text\",\"text\":null}")]
    [InlineData("{\"type\":\"text\"}")]
    public void Chat_followup_omits_empty_text_without_changing_signatures_or_other_history(string emptyTextPart)
    {
        var chat = FollowupChatRequest(emptyTextPart);
        var originalChat = JsonSerializer.Serialize(chat, JsonSerializerOptions.Web);
        var interaction = chat.ToUnifiedRequest("google").ToInteractionRequest("google");
        var wire = JsonSerializer.SerializeToElement(interaction, InteractionJson.Default);

        AssertNoEmptyText(wire.GetProperty("input"));
        Assert.Equal(new[] { "sig-client", "sig-after-client", "sig-code-1", "sig-code-2", "sig-final" },
            interaction.Input!.Steps!.OfType<InteractionThoughtContent>().Select(thought => thought.Signature));
        Assert.Equal("Thanks.", wire.GetProperty("input").EnumerateArray().Last()
            .GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(originalChat, JsonSerializer.Serialize(chat, JsonSerializerOptions.Web));

        // Compare against the same saved conversation without its UI placeholder.
        // This deliberately leaves existing tool replay behavior outside this fix.
        chat.Messages[4].Parts.RemoveAt(4);
        var expected = chat.ToUnifiedRequest("google").ToInteractionRequest("google");
        Assert.Equal(JsonSerializer.Serialize(expected, InteractionJson.Default), wire.GetRawText());
    }

    [Theory]
    [InlineData("assistant", null)]
    [InlineData("assistant", "")]
    [InlineData("user", null)]
    [InlineData("user", "")]
    public void Empty_only_messages_do_not_produce_empty_input_or_output_steps(string role, string? text)
    {
        var request = new AIRequest
        {
            ProviderId = "google",
            Input = new AIInput
            {
                Items =
                [
                    // Deliberately exercise malformed/null text from saved input.
                    new AIInputItem { Role = role, Content = [new AITextContentPart { Type = "text", Text = text! }] },
                    new AIInputItem { Role = "user", Content = [new AITextContentPart { Type = "text", Text = "Continue." }] }
                ]
            }
        };

        var interaction = request.ToInteractionRequest("google");
        var step = Assert.IsType<InteractionUserInputStep>(Assert.Single(interaction.Input!.Steps!));
        Assert.Equal("Continue.", Assert.IsType<InteractionTextContent>(Assert.Single(step.Content!)).Text);
    }

    [Theory]
    [InlineData("assistant", "  Keep this text.\r\n")]
    [InlineData("user", "  Keep this text.\r\n")]
    [InlineData("assistant", " \n\t")]
    [InlineData("user", " \n\t")]
    public void Non_empty_text_is_preserved_verbatim(string role, string text)
    {
        var request = new AIRequest
        {
            ProviderId = "google",
            Input = new AIInput
            {
                Items = [new AIInputItem { Role = role, Content = [new AITextContentPart { Type = "text", Text = text }] }]
            }
        };

        var wire = JsonSerializer.SerializeToElement(request.ToInteractionRequest("google"), InteractionJson.Default);
        Assert.Equal(text, wire.GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString());
    }

    internal static void AssertNoEmptyText(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                AssertNoEmptyText(item);
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("type", out var type) && type.GetString() == "text")
            {
                Assert.True(value.TryGetProperty("text", out var text));
                Assert.Equal(JsonValueKind.String, text.ValueKind);
                Assert.False(string.IsNullOrEmpty(text.GetString()));
            }

            foreach (var property in value.EnumerateObject())
                AssertNoEmptyText(property.Value);
        }
    }

    // Sanitized reproduction of saved client-tool and code-execution rounds followed
    // by a normal user message. No captured prompts, identifiers, or signatures.
    internal static ChatRequest FollowupChatRequest(string emptyTextPart = "{\"type\":\"text\",\"text\":\"\"}")
        => JsonSerializer.Deserialize<ChatRequest>($$"""
        {
          "model": "google/gemini-4-argon",
          "providerMetadata": { "google": { "tools": [{ "type": "code_execution" }] } },
          "messages": [
            { "id": "user-1", "role": "user", "parts": [{ "type": "text", "text": "Get country details." }] },
            { "id": "assistant-1", "role": "assistant", "parts": [
              { "type": "reasoning", "text": "", "providerMetadata": { "google": { "type": "thought_signature", "signature": "sig-client" } } },
              { "type": "tool-country_details", "toolCallId": "client-1", "providerExecuted": false,
                "state": "output-available", "input": { "code": "PL" },
                "output": { "content": [], "structuredContent": { "name": "Poland" } } },
              { "type": "reasoning", "text": "", "providerMetadata": { "google": { "type": "thought_signature", "signature": "sig-after-client" } } },
              { "type": "text", "text": "Country details." }
            ] },
            { "id": "user-2", "role": "user", "parts": [{ "type": "text", "text": "Create a document." }] },
            { "id": "assistant-empty", "role": "assistant", "parts": [{ "type": "text", "text": "" }] },
            { "id": "assistant-2", "role": "assistant", "parts": [
              { "type": "reasoning", "text": "", "providerMetadata": { "google": { "type": "thought_signature", "signature": "sig-code-1" } } },
              { "type": "tool-code_execution", "toolCallId": "code-1", "providerExecuted": true,
                "state": "output-available", "input": { "language": "python", "code": "print('ready')" },
                "output": { "content": [{ "type": "text", "text": "ready" }] } },
              { "type": "reasoning", "text": "", "providerMetadata": { "google": { "type": "thought_signature", "signature": "sig-code-2" } } },
              { "type": "tool-code_execution", "toolCallId": "code-2", "providerExecuted": true,
                "state": "output-available", "input": { "language": "python", "code": "print('done')" },
                "output": { "content": [{ "type": "text", "text": "done" }] } },
              {{emptyTextPart}},
              { "type": "reasoning", "text": "", "providerMetadata": { "google": { "type": "thought_signature", "signature": "sig-final" } } },
              { "type": "text", "text": "The document is ready." }
            ] },
            { "id": "user-3", "role": "user", "parts": [{ "type": "text", "text": "Thanks." }] }
          ]
        }
        """, JsonSerializerOptions.Web)!;
}
