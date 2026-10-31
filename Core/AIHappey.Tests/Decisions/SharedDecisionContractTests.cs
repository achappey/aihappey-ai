using System.Net;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;

namespace AIHappey.Tests.Decisions;

public class SharedDecisionContractTests
{
    [Fact]
    public void Gateway_provider_contract_uses_the_standalone_assembly()
    {
        var method = typeof(IModelProvider).GetMethod(nameof(IModelProvider.OpenAIDecisionRequestAsync))!;
        Assert.Equal(typeof(OpenAIDecisionRequest), method.GetParameters()[0].ParameterType);
        Assert.Equal("AIHappey.Decisions", method.GetParameters()[0].ParameterType.Assembly.GetName().Name);
        Assert.Null(typeof(IModelProvider).Assembly.GetType("AIHappey.Core.Models.OpenAIDecisionRequest"));
    }

    [Theory]
    [InlineData("""[{"role":"developer","content":"Policy"}]""")]
    [InlineData("""[{"role":"assistant","content":"History"}]""")]
    [InlineData("""[{"type":"function_call","call_id":"f1","name":"lookup","arguments":"{}"}]""")]
    [InlineData("""[{"role":"user","content":[{"type":"input_json","data":{"id":1}}]}]""")]
    [InlineData("""[{"role":"user","content":[{"type":"input_text","text":"x","prompt_cache_breakpoint":{"mode":"explicit"}}]}]""")]
    public void Rich_contract_inputs_do_not_silently_expand_provider_adapter_support(string json)
    {
        var request = Request();
        request.Input = JsonSerializer.Deserialize<OpenAIDecisionInput>(json)!;
        var error = Record.Exception(() => ModelProviderDecisionExtensions.ValidateOpenAIDecisionRequest(request));
        Assert.True(error is ArgumentException or NotSupportedException, error?.ToString() ?? "Expected explicit rejection.");
    }

    [Fact]
    public void Adapter_validation_rejects_multipart_questions_and_typed_cache_controls()
    {
        var request = Request();
        request.Questions[0].InstructionContent = new OpenAIDecisionInputPart[] { new OpenAIDecisionInputText { Text = "Test" } };
        Assert.Throws<NotSupportedException>(() => ModelProviderDecisionExtensions.ValidateOpenAIDecisionRequest(request));
        request = Request();
        request.PromptCacheOptions = new() { Mode = "explicit", Ttl = "30m" };
        Assert.Throws<NotSupportedException>(() => ModelProviderDecisionExtensions.ValidateOpenAIDecisionRequest(request));
    }

    [Fact]
    public async Task Generic_transport_preserves_existing_cache_and_extension_option_passthrough()
    {
        using var client = new HttpClient(new Handler(request =>
        {
            var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement;
            Assert.Equal("caller", body.GetProperty("prompt_cache_key").GetString());
            Assert.Equal("explicit", body.GetProperty("prompt_cache_options").GetProperty("mode").GetString());
            Assert.Equal(1, body.GetProperty("custom_option").GetInt32());
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"model":"model","answers":[{"type":"predicate","name":null,"probability":0.8}],"usage":{"input_tokens":1,"output_tokens":0,"total_tokens":1}}""")
            };
        })) { BaseAddress = new("https://provider.test/") };
        var request = Request();
        request.PromptCacheKey = "caller";
        request.PromptCacheOptions = new() { Mode = "explicit" };
        request.AdditionalProperties = new() { ["custom_option"] = JsonSerializer.SerializeToElement(1) };
        var result = await client.OpenAICompatibleDecisionRequestAsync(request);
        Assert.Equal(0.8, Assert.IsType<OpenAIDecisionPredicateAnswer>(Assert.Single(result.Answers)).Probability);
    }

    [Fact]
    public void Typed_cache_controls_cannot_be_overridden_by_extension_data()
    {
        var request = Request();
        request.PromptCacheKey = "caller";
        request.AdditionalProperties = new() { ["prompt_cache_key"] = JsonSerializer.SerializeToElement("override") };
        Assert.Throws<ArgumentException>(() => ModelProviderDecisionExtensions.ValidateOpenAIDecisionRequest(request, allowPromptCacheControls: true));
    }

    private static OpenAIDecisionRequest Request() => new()
    {
        Model = "model", Input = "Evidence", Questions = [new OpenAIDecisionPredicateQuestion { Instructions = "Check it" }]
    };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
