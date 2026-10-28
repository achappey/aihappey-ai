using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using static AIHappey.Tests.Zoom.ZoomTestHelpers;

namespace AIHappey.Tests.Zoom;

public sealed class ZoomDecisionTests
{
    private const string NativeResponse = """
        {
          "request_id":"req-decide","model":"zoom-decisions-pro",
          "answers":[
            {"type":"predicate","name":"urgent","probability":0.9},
            {"type":"choice","name":"route","choice":true,"probabilities":[{"value":"true","probability":0.8},{"value":"false","probability":0.2}]},
            {"type":"score","name":"severity","score":1.5,"probabilities":[{"value":0,"probability":0.1},{"value":1,"probability":0.3},{"value":2,"probability":0.6}]},
            {"type":"refusal","name":"refuse"}
          ],
          "usage":{"input_units":120,"output_units":4,"unit_type":"tokens","details":{"cached_input_tokens":20}}
        }
        """;

    private static OpenAIDecisionRequest Request() => Read<OpenAIDecisionRequest>("""
        {
          "model":"zoom/zoom-decisions-pro","input":"The shipment is damaged.",
          "reference_id":"trace-123",
          "questions":[
            {"type":"predicate","name":"urgent","instructions":"Is this urgent?"},
            {"type":"choice","name":"route","instructions":"Escalate?","choices":[{"value":true,"description":"Escalate"},{"value":false}]},
            {"type":"score","name":"severity","instructions":"How severe?","levels":[{"label":"low"},{"label":"medium"},{"label":"high"}]},
            {"type":"predicate","name":"refuse","instructions":"An unanswerable question"}
          ]
        }
        """);

    [Fact]
    public async Task Native_decisions_preserve_types_options_refusals_and_metering()
    {
        var provider = Provider(async (message, token) =>
        {
            Assert.Equal("https://api.zoom.us/v2/aiservices/decisions/decide", message.RequestUri!.ToString());
            Assert.Equal("zoom-secret", Assert.Single(message.Headers.GetValues("x-api-key")));
            Assert.Null(message.Headers.Authorization);
            using var payload = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            Assert.Equal("zoom-decisions-pro", payload.RootElement.GetProperty("model").GetString());
            Assert.Equal("trace-123", payload.RootElement.GetProperty("reference_id").GetString());
            Assert.False(payload.RootElement.TryGetProperty("safety_identifier", out _));
            Assert.Equal(JsonValueKind.True, payload.RootElement.GetProperty("questions")[1].GetProperty("choices")[0].GetProperty("value").ValueKind);
            return Reply(NativeResponse);
        });
        var result = await provider.OpenAIDecisionRequestAsync(Request());
        Assert.Equal(0.9, Assert.IsType<OpenAIDecisionPredicateAnswer>(result.Answers[0]).Probability);
        var choice = Assert.IsType<OpenAIDecisionChoiceAnswer>(result.Answers[1]);
        Assert.True(choice.Choice.Value.GetBoolean());
        Assert.True(choice.Probabilities[0].Value.Value.GetBoolean());
        Assert.Equal(0, choice.Confidence);
        var score = Assert.IsType<OpenAIDecisionScoreAnswer>(result.Answers[2]);
        Assert.Equal(0.75, score.Score);
        Assert.Equal("high", score.Probabilities[2].Label);
        Assert.Equal(1, score.Probabilities[2].Value);
        Assert.IsType<OpenAIDecisionRefusalAnswer>(result.Answers[3]);
        Assert.Equal(120, result.Usage.InputTokens);
        Assert.Equal(20, result.Usage.InputTokensDetails.CachedTokens);
        Assert.Equal(0, result.Usage.OutputTokens);
        Assert.Equal(120, result.Usage.TotalTokens);
        var native = result.AdditionalProperties!["providerMetadata"].GetProperty("zoom");
        Assert.Equal(4, native.GetProperty("usage").GetProperty("output_units").GetInt32());
        Assert.False(native.GetProperty("answers")[1].TryGetProperty("confidence", out _));
        Assert.Contains("questions", result.AdditionalProperties["warnings"].GetRawText());
    }

    [Fact]
    public async Task Vercel_supports_sdk_images_boolean_criteria_scores_headers_and_refusals()
    {
        var provider = Provider(async (message, token) =>
        {
            using var payload = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            Assert.Equal("image/png", payload.RootElement.GetProperty("input")[0].GetProperty("content")[0]
                .GetProperty("image_url").GetString()!.Split(';')[0]["data:".Length..]);
            Assert.True(payload.RootElement.GetProperty("enable_image_input").GetBoolean());
            Assert.Equal("trace", Assert.Single(message.Headers.GetValues("X-Zoom-X-Trace")));
            return Reply(NativeResponse);
        });
        var result = await provider.DecisionRequestAsync(Read<DecisionRequest>("""
            {
              "model":"zoom/zoom-decisions-pro","stateFormat":"sdk",
              "state":[{"type":"file","mediaType":"image/png","data":{"type":"data","data":"AQ=="}}],
              "questions":{
                "urgent":{"type":"boolean","instructions":"Urgent?"},
                "route":{"type":"boolean","instructions":"Escalate?","criteria":{"true":"escalate","false":"do not"}},
                "severity":{"type":"score","instructions":"How severe?","criteria":["low","medium","high"]},
                "refuse":{"type":"boolean","instructions":"Unanswerable"}
              },
              "headers":{"X-Zoom-X-Trace":"trace"},
              "providerOptions":{"zoom":{"enable_image_input":true,"reference_id":"trace-123"}}
            }
            """));
        Assert.Equal(0.8, Assert.IsType<DecisionBooleanAnswer>(result.Answers["route"]).Probability);
        Assert.Equal(1.5, Assert.IsType<DecisionScoreAnswer>(result.Answers["severity"]).Score);
        Assert.IsType<DecisionRefusalAnswer>(result.Answers["refuse"]);
        Assert.Equal(0, result.Usage!.OutputTokens);
        Assert.Equal("req-decide", result.Response!.Id);
        Assert.NotEmpty(result.Warnings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Optional_or_repeated_OpenAI_names_are_reversible_without_mutating_request(bool repeated)
    {
        var request = Read<OpenAIDecisionRequest>("""
            {"model":"zoom-decisions","input":"text","questions":[
              {"type":"predicate","instructions":"One?"},{"type":"predicate","instructions":"Two?"}]}
            """);
        if (repeated)
            foreach (var question in request.Questions)
                question.Name = "same";
        var provider = Provider(async (message, token) =>
        {
            var payload = JsonNode.Parse(await message.Content!.ReadAsStringAsync(token))!;
            var answers = payload["questions"]!.AsArray().Select(question =>
                new { type = "predicate", name = question!["name"]!.GetValue<string>(), probability = 0.5 }).ToArray();
            return Reply(JsonSerializer.Serialize(new
            {
                model = "zoom-decisions", request_id = "req", answers,
                usage = new { input_units = 2, output_units = 2, unit_type = "tokens" }
            }));
        });
        var result = await provider.OpenAIDecisionRequestAsync(request);
        Assert.All(result.Answers, answer => Assert.Equal(repeated ? "same" : null, answer.Name));
        Assert.All(request.Questions, question => Assert.Equal(repeated ? "same" : null, question.Name));
    }

    [Fact]
    public async Task Vercel_string_choices_and_documented_confidence_are_mapped()
    {
        var provider = Provider((_, _) => Task.FromResult(Reply("""
            {"model":"zoom-decisions","request_id":"req","answers":[
              {"name":"route","type":"choice","choice":"support","confidence":0.8,
               "probabilities":[{"value":"sales","probability":0.2},{"value":"support","probability":0.8}]}],
             "usage":{"input_units":5,"output_units":1,"unit_type":"tokens"}}
            """)));
        var result = await provider.DecisionRequestAsync(Read<DecisionRequest>("""
            {"model":"zoom-decisions","state":"Broken shipment","questions":{
              "route":{"type":"choice","instructions":"Route this","criteria":{"sales":null,"support":"Customer support"}}}}
            """));
        Assert.Equal("support", Assert.IsType<DecisionChoiceAnswer>(result.Answers["route"]).Choice);
        Assert.Equal(0.8, result.ProviderMetadata!["zoom"].GetProperty("answers")[0].GetProperty("confidence").GetDouble());
    }

    [Fact]
    public async Task Score_indices_can_be_JSON_numbers_with_decimal_notation()
    {
        var provider = Provider((_, _) => Task.FromResult(Reply(NativeResponse
            .Replace("\"value\":0,", "\"value\":0.0,", StringComparison.Ordinal)
            .Replace("\"value\":1,", "\"value\":1.0,", StringComparison.Ordinal)
            .Replace("\"value\":2,", "\"value\":2.0,", StringComparison.Ordinal))));
        var result = await provider.OpenAIDecisionRequestAsync(Request());
        Assert.Equal(0.75, Assert.IsType<OpenAIDecisionScoreAnswer>(result.Answers[2]).Score);
    }

    [Theory]
    [InlineData("count")]
    [InlineData("name")]
    [InlineData("type")]
    [InlineData("probability")]
    [InlineData("distribution")]
    [InlineData("score")]
    [InlineData("units")]
    [InlineData("usage")]
    public async Task Invalid_native_outputs_are_not_returned_as_valid_decisions(string invalid)
    {
        var body = JsonNode.Parse(NativeResponse)!;
        switch (invalid)
        {
            case "count": body["answers"]!.AsArray().RemoveAt(0); break;
            case "name": body["answers"]![0]!["name"] = "wrong"; break;
            case "type": body["answers"]![0]!["type"] = "choice"; break;
            case "probability": body["answers"]![0]!["probability"] = 1.1; break;
            case "distribution": body["answers"]![1]!["probabilities"]![1]!["probability"] = 0.5; break;
            case "score": body["answers"]![2]!["score"] = 2; break;
            case "units": body["usage"]!["unit_type"] = "characters"; break;
            case "usage": body["usage"]!["input_units"] = -1; break;
        }
        var provider = Provider((_, _) => Task.FromResult(Reply(body.ToJsonString())));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.OpenAIDecisionRequestAsync(Request()));
    }

    [Fact]
    public async Task Unsupported_options_limits_and_image_opt_in_fail_before_HTTP()
    {
        var request = Request();
        request.SafetyIdentifier = "unsupported";
        await Assert.ThrowsAsync<NotSupportedException>(() => NoHttp().OpenAIDecisionRequestAsync(request));
        request.SafetyIdentifier = null;
        request.Questions = Enumerable.Repeat(request.Questions[0], 11).ToArray();
        await Assert.ThrowsAsync<ArgumentException>(() => NoHttp().OpenAIDecisionRequestAsync(request));
        request = Request();
        request.Input = new OpenAIDecisionInputMessage[]
        {
            new() { Content = new OpenAIDecisionInputPart[] { new OpenAIDecisionInputImage { ImageUrl = "data:image/png;base64,AQ==" } } }
        };
        await Assert.ThrowsAsync<ArgumentException>(() => NoHttp().OpenAIDecisionRequestAsync(request));
        request = Request();
        ((OpenAIDecisionChoiceQuestion)request.Questions[1]).Choices =
            [new() { Value = true }, new() { Value = "true" }];
        await Assert.ThrowsAsync<ArgumentException>(() => NoHttp().OpenAIDecisionRequestAsync(request));
    }

    [Fact]
    public async Task Errors_include_HTTP_status_and_native_body()
    {
        var provider = Provider((_, _) => Task.FromResult(Reply("""{"reason":"QUOTA_EXCEEDED"}""", HttpStatusCode.TooManyRequests)));
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => provider.OpenAIDecisionRequestAsync(Request()));
        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
        Assert.Contains("QUOTA_EXCEEDED", exception.Message);
    }
}
