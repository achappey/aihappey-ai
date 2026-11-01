using System.Text.Json;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Unified.Models;

namespace AIHappey.Tests.Responses;

public sealed class ResponsesCodeInterpreterRequestTests
{
    [Theory]
    [InlineData("openai", null)]
    [InlineData("openai", "null")]
    [InlineData("openai", "[]")]
    [InlineData("openai", "[{\"type\":\"logs\",\"logs\":\"42\",\"provider_field\":{\"value\":true}}]")]
    [InlineData("spacexai", null)]
    [InlineData("spacexai", "null")]
    [InlineData("spacexai", "[]")]
    [InlineData("spacexai", "[{\"type\":\"logs\",\"logs\":\"42\",\"provider_field\":{\"value\":true}}]")]
    public void Code_interpreter_outputs_roundtrip_without_dropping_native_item(string providerId, string? outputsJson)
    {
        var outputsProperty = outputsJson is null ? "" : $",\"outputs\":{outputsJson}";
        var request = JsonSerializer.Deserialize<ResponseRequest>($$"""
            {
              "model": "test-model",
              "input": [{
                "type": "code_interpreter_call",
                "id": "ci_test",
                "code": "print(42)",
                "container_id": "cntr_test",
                "status": "completed"
                {{outputsProperty}}
              }]
            }
            """, ResponseJson.Default)!;

        var unified = request.ToUnifiedRequest(providerId);
        var inputItem = Assert.Single(unified.Input!.Items!);
        var toolPart = Assert.IsType<AIToolCallContentPart>(Assert.Single(inputItem.Content!));
        var metadata = Assert.IsType<Dictionary<string, object?>>(toolPart.Metadata![providerId]);
        Assert.True(toolPart.ProviderExecuted);
        Assert.Equal("code_interpreter_call", metadata["type"]);
        AssertOutputs(outputsJson, toolPart.Output);
        AssertOutputs(outputsJson, metadata["outputs"]);

        var replay = Assert.IsType<ResponseCodeInterpreterCallItem>(
            Assert.Single(unified.ToResponseRequest(providerId).Input!.Items!));

        Assert.Equal("ci_test", replay.Id);
        Assert.Equal("print(42)", replay.Code);
        Assert.Equal("cntr_test", replay.ContainerId);
        Assert.Equal("completed", replay.Status);
        AssertOutputs(outputsJson, replay.Outputs);

        // Exercise the actual wire serializer as well as the in-memory replay.
        var serialized = JsonSerializer.SerializeToElement<ResponseInputItem>(replay, ResponseJson.Default);
        if (outputsJson is null or "null")
            Assert.False(serialized.TryGetProperty("outputs", out _));
        else
            Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(outputsJson), serialized.GetProperty("outputs")));
    }

    [Fact]
    public void Code_interpreter_outputs_are_independent_of_source_document_lifetime()
    {
        const string outputsJson = "[{\"type\":\"logs\",\"logs\":\"42\",\"provider_field\":{\"value\":true}}]";
        AIRequest unified;
        using (var document = JsonDocument.Parse(outputsJson))
        {
            var request = new ResponseRequest
            {
                Model = "test-model",
                Input = new ResponseInput([
                    new ResponseCodeInterpreterCallItem
                    {
                        Id = "ci_test",
                        Code = "print(42)",
                        ContainerId = "cntr_test",
                        Outputs = document.RootElement
                    }
                ])
            };
            unified = request.ToUnifiedRequest("openai");
        }

        var item = Assert.Single(unified.Input!.Items!);
        var toolPart = Assert.IsType<AIToolCallContentPart>(Assert.Single(item.Content!));
        var metadata = Assert.IsType<Dictionary<string, object?>>(toolPart.Metadata!["openai"]);
        AssertOutputs(outputsJson, toolPart.Output);
        AssertOutputs(outputsJson, metadata["outputs"]);
        var replay = Assert.IsType<ResponseCodeInterpreterCallItem>(
            Assert.Single(unified.ToResponseRequest("openai").Input!.Items!));
        AssertOutputs(outputsJson, replay.Outputs);
    }

    [Fact]
    public void Code_interpreter_without_outputs_is_not_replayed_to_another_provider()
    {
        var request = new ResponseRequest
        {
            Model = "test-model",
            Input = new ResponseInput([
                new ResponseCodeInterpreterCallItem
                {
                    Id = "ci_test",
                    Code = "print(42)",
                    ContainerId = "cntr_test"
                }
            ])
        };

        var replay = request.ToUnifiedRequest("openai").ToResponseRequest("spacexai");

        Assert.Empty(replay.Input!.Items!);
    }

    private static void AssertOutputs(string? expectedJson, object? actual)
    {
        if (expectedJson is null or "null")
        {
            Assert.Null(actual);
            return;
        }

        var element = Assert.IsType<JsonElement>(actual);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(expectedJson), element));
    }
}
