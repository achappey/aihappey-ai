using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Decisions;

public class DecisionSerializationTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [Fact]
    public void OpenAIRequest_RoundTripsAllQuestionsAndTypedChoices()
    {
        const string json = """
        {
          "model": "openai/gpt-6-luna",
          "input": "The package arrived with a broken screen.",
          "questions": [
            { "instructions": "Damaged?", "type": "predicate", "name": "damaged" },
            { "choices": [{ "value": "true" }, { "value": true, "description": "Boolean" }, { "value": false }], "instructions": "Choose", "type": "choice" },
            { "levels": [{ "label": "low" }, { "label": "high", "description": "Severe" }], "instructions": "Rate", "type": "score" }
          ],
          "safety_identifier": "opaque-caller-id",
          "custom_option": { "enabled": true }
        }
        """;

        var request = Deserialize<OpenAIDecisionRequest>(json);
        Assert.Equal("The package arrived with a broken screen.", request.Input.Text);
        Assert.Null(request.Input.Messages);
        Assert.Collection(request.Questions,
            q => Assert.IsType<OpenAIDecisionPredicateQuestion>(q),
            q => Assert.IsType<OpenAIDecisionChoiceQuestion>(q),
            q => Assert.IsType<OpenAIDecisionScoreQuestion>(q));
        var choices = Assert.IsType<OpenAIDecisionChoiceQuestion>(request.Questions[1]).Choices;
        Assert.Equal(JsonValueKind.String, choices[0].Value.Value.ValueKind);
        Assert.Equal(JsonValueKind.True, choices[1].Value.Value.ValueKind);
        Assert.Equal(JsonValueKind.False, choices[2].Value.Value.ValueKind);
        Assert.Equal("opaque-caller-id", request.SafetyIdentifier);
        Assert.True(request.AdditionalProperties!["custom_option"].GetProperty("enabled").GetBoolean());
        AssertRoundTrip(json, request);
    }

    [Fact]
    public void OpenAIRequest_RoundTripsUserMessagesWithTextAndInlineImages()
    {
        const string json = """
        {
          "model": "openai/gpt-6-luna",
          "input": [
            { "role": "user", "content": "Look at the evidence" },
            { "role": "user", "type": "message", "content": [
              { "text": "Damage", "type": "input_text" },
              { "image_url": "data:image/png;base64,aGVsbG8=", "detail": "original", "type": "input_image" },
              { "image_url": "data:image/png;base64,aGVsbG8=", "type": "input_image" }
            ] }
          ],
          "questions": [{ "instructions": "Damaged?", "type": "predicate" }]
        }
        """;

        var request = Deserialize<OpenAIDecisionRequest>(json);
        Assert.Null(request.Input.Text);
        Assert.Equal(2, request.Input.Messages!.Count);
        Assert.Equal("Look at the evidence", request.Input.Messages[0].Content.Text);
        var parts = request.Input.Messages[1].Content.Parts!;
        Assert.Equal("Damage", Assert.IsType<OpenAIDecisionInputText>(parts[0]).Text);
        Assert.Equal("original", Assert.IsType<OpenAIDecisionInputImage>(parts[1]).Detail);
        Assert.Null(Assert.IsType<OpenAIDecisionInputImage>(parts[2]).Detail);
        AssertRoundTrip(json, request);
    }

    [Fact]
    public void OpenAIResponse_RoundTripsAllOrderedAnswersAndCompleteUsage()
    {
        const string json = """
        {
          "model": "gpt-6-luna",
          "answers": [
            { "name": "damaged", "probability": 0.95, "type": "predicate" },
            { "choice": true, "confidence": 0.8, "name": null, "probabilities": [{ "value": true, "probability": 0.8 }, { "value": "true", "probability": 0.2 }], "type": "choice" },
            { "score": 0.6, "confidence": 0.6, "name": "severity", "probabilities": [{ "label": "low", "value": 0, "probability": 0.4 }, { "label": "high", "value": 1, "probability": 0.6 }], "type": "score" },
            { "name": null, "type": "refusal" }
          ],
          "usage": {
            "input_tokens": 42, "input_tokens_details": { "cache_write_tokens": 3, "cached_tokens": 7 },
            "output_tokens": 5, "output_tokens_details": { "reasoning_tokens": 5 }, "total_tokens": 47
          }
        }
        """;

        var response = Deserialize<OpenAIDecisionResponse>(json);
        Assert.Collection(response.Answers,
            a => Assert.IsType<OpenAIDecisionPredicateAnswer>(a),
            a => Assert.IsType<OpenAIDecisionChoiceAnswer>(a),
            a => Assert.IsType<OpenAIDecisionScoreAnswer>(a),
            a => Assert.IsType<OpenAIDecisionRefusalAnswer>(a));
        Assert.Equal(JsonValueKind.True, Assert.IsType<OpenAIDecisionChoiceAnswer>(response.Answers[1]).Choice.Value.ValueKind);
        Assert.Equal(3, response.Usage.InputTokensDetails.CacheWriteTokens);
        Assert.Equal(7, response.Usage.InputTokensDetails.CachedTokens);
        Assert.Equal(5, response.Usage.OutputTokensDetails.ReasoningTokens);
        AssertRoundTrip(json, response);
    }

    [Theory]
    [InlineData("\"shared text\"")]
    [InlineData("{\"customer\":{\"id\":1},\"flag\":true}")]
    [InlineData("[\"evidence\",{\"image\":\"inline\"},1,true,null]")]
    public void VercelRequest_RoundTripsAllQuestionsAndOneStructuredState(string state)
    {
        var json = """
        {
          "model": "openai/gpt-6-luna",
          "state": STATE,
          "questions": {
            "category": { "instructions": { "task": "Classify" }, "criteria": { "damaged": "Broken", "other": null }, "type": "choice" },
            "severity": { "instructions": ["Rate", { "context": "customer" }], "criteria": [null, { "level": "high" }], "type": "score" },
            "damaged": { "instructions": "Damaged?", "criteria": { "true": null, "false": "Intact" }, "type": "boolean" },
            "simple": { "instructions": "Relevant?", "type": "boolean" },
            "emptyCriteria": { "instructions": "Relevant?", "criteria": {}, "type": "boolean" }
          },
          "headers": { "x-custom": "value", "x-omitted": null },
          "providerOptions": { "openai": { "custom": [1,2] } }
        }
        """.Replace("STATE", state);

        var request = Deserialize<DecisionRequest>(json);
        Assert.Equal(JsonDocument.Parse(state).RootElement.ValueKind, request.State.Value.ValueKind);
        Assert.Equal(5, request.Questions.Count);
        Assert.Null(Assert.IsType<DecisionChoiceQuestion>(request.Questions["category"]).Criteria["other"]);
        Assert.Null(Assert.IsType<DecisionScoreQuestion>(request.Questions["severity"]).Criteria[0]);
        Assert.Null(Assert.IsType<DecisionBooleanQuestion>(request.Questions["simple"]).Criteria);
        AssertRoundTrip(json, request);
    }

    [Fact]
    public void VercelResponse_RoundTripsKeyedAnswersAndOptionalMetadata()
    {
        const string json = """
        {
          "answers": {
            "category": { "choice": "damaged", "probabilities": { "damaged": 0.8, "other": 0.2 }, "type": "choice" },
            "severity": { "score": 0.6, "probabilities": { "0": 0.4, "1": 0.6 }, "type": "score" },
            "damaged": { "probability": 0.95, "type": "boolean" },
            "refused": { "type": "refusal" },
            "noDistribution": { "choice": "other", "type": "choice" },
            "noScoreDistribution": { "score": 1.5, "type": "score" }
          },
          "rounding": { "probabilityDecimals": 2, "scoreDecimals": 1 },
          "usage": { "inputTokens": 42, "outputTokens": 5 },
          "warnings": [{ "type": "other", "message": "test warning" }],
          "providerMetadata": { "openai": { "custom": true } },
          "response": { "id": "decision-1", "timestamp": "2026-10-07T14:00:00+00:00", "modelId": "gpt-6-luna", "headers": { "x-id": "1" }, "body": { "raw": true } }
        }
        """;

        var response = Deserialize<DecisionResponse>(json);
        Assert.Equal(6, response.Answers.Count);
        Assert.Equal("damaged", Assert.IsType<DecisionChoiceAnswer>(response.Answers["category"]).Choice);
        Assert.Equal(0.6, Assert.IsType<DecisionScoreAnswer>(response.Answers["severity"]).Score);
        Assert.Equal(0.95, Assert.IsType<DecisionBooleanAnswer>(response.Answers["damaged"]).Probability);
        Assert.IsType<DecisionRefusalAnswer>(response.Answers["refused"]);
        Assert.Null(Assert.IsType<DecisionChoiceAnswer>(response.Answers["noDistribution"]).Probabilities);
        Assert.Equal(2, response.Rounding!.ProbabilityDecimals);
        Assert.Equal(42, response.Usage!.InputTokens);
        Assert.Equal(DateTimeOffset.Parse("2026-10-07T14:00:00+00:00"), response.Response!.Timestamp);
        AssertRoundTrip(json, response);
    }

    [Fact]
    public void VercelResponse_OmitsOptionalFieldsButKeepsWarningsAndAnswers()
    {
        AssertRoundTrip("""{"answers":{},"warnings":[]}""", new DecisionResponse());
        AssertRoundTrip("""{"answers":{},"warnings":[],"rounding":{},"usage":{},"response":{}}""",
            new DecisionResponse { Rounding = new(), Usage = new(), Response = new() });
    }

    [Fact]
    public void NativeConstruction_ProducesWireUnionsNotWrapperObjects()
    {
        var request = new OpenAIDecisionRequest
        {
            Model = "model",
            Input = new OpenAIDecisionInputMessage[]
            {
                new() { Content = new OpenAIDecisionInputPart[] { new OpenAIDecisionInputText { Text = "text" } } }
            },
            Questions = [new OpenAIDecisionChoiceQuestion
            {
                Instructions = "Choose", Choices = [new() { Value = "true" }, new() { Value = true }]
            }]
        };
        var roundTrip = Deserialize<OpenAIDecisionRequest>(JsonSerializer.Serialize(request, Options));
        Assert.Single(roundTrip.Input.Messages!);
        var choice = Assert.IsType<OpenAIDecisionChoiceQuestion>(roundTrip.Questions[0]);
        Assert.Equal(JsonValueKind.String, choice.Choices[0].Value.Value.ValueKind);
        Assert.Equal(JsonValueKind.True, choice.Choices[1].Value.Value.ValueKind);

        var vercel = new DecisionRequest
        {
            Model = "model", State = "shared",
            Questions = new() { ["q"] = new DecisionBooleanQuestion { Instructions = "Check", Criteria = new() { True = null } } }
        };
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(vercel, Options));
        Assert.Equal("shared", serialized.RootElement.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, serialized.RootElement.GetProperty("questions").GetProperty("q").GetProperty("criteria").GetProperty("true").ValueKind);
    }

    [Theory]
    [InlineData("123")]
    [InlineData("true")]
    [InlineData("null")]
    public void VercelInput_RejectsUnsupportedTopLevelShapes(string json)
    {
        // Null is rejected by the request's non-null contract/model binding, not the reference converter.
        if (json == "null")
            Assert.Null(JsonSerializer.Deserialize<DecisionInput>(json, Options));
        else
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DecisionInput>(json, Options));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void OpenAIChoice_RejectsValuesOtherThanStringsAndBooleans(string json)
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<OpenAIDecisionChoiceValue>(json, Options));

    [Theory]
    [InlineData("""{"role":"assistant","content":"text"}""")]
    [InlineData("""{"role":"system","content":"text"}""")]
    [InlineData("""{"role":"user","type":"function_call","content":"text"}""")]
    [InlineData("""{"role":"user","content":[{"type":"input_audio"}]}""")]
    [InlineData("""{"role":"user","content":[{"type":"input_file"}]}""")]
    public void OpenAIInput_RejectsUnsupportedMessageAndPartVariants(string json)
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<OpenAIDecisionInputMessage>(json, Options));

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"type\":\"unknown\"}")]
    [InlineData("{\"type\":1}")]
    public void Discriminators_RejectMissingAndUnsupportedTypes(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DecisionQuestion>(json, Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DecisionAnswer>(json, Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<OpenAIDecisionQuestion>(json, Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<OpenAIDecisionAnswer>(json, Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<OpenAIDecisionInputPart>(json, Options));
    }

    private static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)!;

    private static void AssertRoundTrip<T>(string json, T value)
    {
        using var expected = JsonDocument.Parse(json);
        using var actual = JsonDocument.Parse(JsonSerializer.Serialize(value, Options));
        Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement),
            $"Expected: {json}{Environment.NewLine}Actual: {actual.RootElement.GetRawText()}");
    }
}
