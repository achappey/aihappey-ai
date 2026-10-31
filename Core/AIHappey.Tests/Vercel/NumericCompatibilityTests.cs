using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.Core;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.LTX;
using AIHappey.Messages.Mapping;
using AIHappey.Responses.Mapping;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Vercel;

public sealed class NumericCompatibilityTests
{
    [Theory]
    [InlineData(0.123456789012345)]
    [InlineData(0.7)]
    [InlineData(0)]
    [InlineData(2)]
    public void TemperaturePreservesPrecisionAcrossGatewayMappings(double temperature)
    {
        var unified = new ChatRequest { Model = "model", Temperature = temperature }
            .ToUnifiedRequest("provider");

        Assert.Equal(temperature, unified.Temperature);
        Assert.Equal(temperature, unified.ToChatCompletionOptions("provider").Temperature);
        Assert.Equal(temperature, unified.ToMessagesRequest("provider").Temperature);
        Assert.Equal(temperature, AIFinishMessageMetadata.Create("model", DateTimeOffset.UtcNow,
            temperature: unified.Temperature).Temperature);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1.123456789012345)]
    public void SpeechSpeedRetainsPrecisionNullabilityAndRawProviderFields(double? speed)
    {
        var original = new AudioSpeechRequest
        {
            Model = "model", Input = "Hello", Speed = speed,
            AdditionalProperties = new() { ["native_option"] = JsonSerializer.SerializeToElement(new { enabled = true }) }
        };
        var wire = JsonSerializer.Serialize(original);
        var roundTrip = JsonSerializer.Deserialize<AudioSpeechRequest>(wire)!;

        Assert.Equal(speed, roundTrip.Speed);
        Assert.Equal(speed, roundTrip.ToSpeechRequest().Speed);
        using var json = JsonDocument.Parse(wire);
        Assert.True(json.RootElement.GetProperty("native_option").GetProperty("enabled").GetBoolean());
        Assert.Equal(speed.HasValue, json.RootElement.TryGetProperty("speed", out _));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0d, 0)]
    [InlineData(24000d, 24000)]
    [InlineData(2147483647d, int.MaxValue)]
    [InlineData(-2147483648d, int.MinValue)]
    public void IntegralBoundaryPreservesExactValuesAndNulls(double? value, int? expected)
        => Assert.Equal(expected, ProviderNumericConversions.ToInt32Exact(value, "rate"));

    [Theory]
    [InlineData(24000.5)]
    [InlineData(2147483648d)]
    [InlineData(-2147483649d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.Epsilon)]
    public void IntegralBoundaryNeverTruncatesRoundsOrOverflows(double value)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProviderNumericConversions.ToInt32Exact(value, "outputFormat.rate"));
        Assert.Equal("outputFormat.rate", error.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.7)]
    [InlineData(0.25)]
    [InlineData(1d)]
    public void ResponsesBoundaryPreservesTheJsonNumberOrNull(double? temperature)
    {
        var request = new AIRequest { ProviderId = "provider", Temperature = temperature };
        var mapped = request.ToResponseRequest("provider");
        if (temperature is null)
            Assert.Null(mapped.Temperature);
        else
            Assert.Equal(temperature.Value, JsonSerializer.SerializeToElement(mapped)
                .GetProperty("temperature").GetDouble());
    }

    [Theory]
    [InlineData(0.123456789012345)]
    [InlineData(1e100)]
    [InlineData(double.Epsilon)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ResponsesBoundaryRejectsPrecisionLossInsteadOfSilentlyRounding(double temperature)
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AIRequest { ProviderId = "provider", Temperature = temperature }.ToResponseRequest("provider"));

    [Fact]
    public void EmbeddingsPreserveJsonPrecisionOrderingDimensionsUsageAndMetadata()
    {
        var headers = new Dictionary<string, string> { ["x-request-id"] = "embedding-id" };
        var metadata = new Dictionary<string, JsonElement> { ["provider"] = JsonSerializer.SerializeToElement(new { native = true }) };
        var result = new OpenAICompatibleEmbeddingResult(new OpenAIEmbeddingResponse
        {
            Model = "model",
            Data =
            [
                new() { Index = 1, Embedding = JsonSerializer.SerializeToElement(new[] { -0.987654321098765, double.Epsilon }) },
                new() { Index = 0, Embedding = JsonSerializer.SerializeToElement(new[] { 0.123456789012345, 1e100 }) }
            ],
            Usage = new() { PromptTokens = 9, TotalTokens = 9 }
        }, headers);

        var mapped = result.ToEmbeddingResponse(metadata);
        var vectors = mapped.Embeddings.Select(vector => vector.ToArray()).ToArray();
        Assert.Equal(2, vectors.Length);
        Assert.Equal([0.123456789012345, 1e100], vectors[0]);
        Assert.Equal([-0.987654321098765, double.Epsilon], vectors[1]);
        Assert.Equal(9, mapped.Usage!.Tokens);
        Assert.Same(headers, mapped.Response!.Headers);
        Assert.Same(metadata, mapped.ProviderMetadata);
    }

    [Theory]
    [InlineData("[1,\"invalid\"]")]
    [InlineData("[1e999]")]
    [InlineData("\"AACAPwAAAEA=\"")]
    public void NumericEmbeddingMappingDoesNotMisinterpretMalformedOrEncodedVectors(string json)
    {
        var result = new OpenAICompatibleEmbeddingResult(new OpenAIEmbeddingResponse
        {
            Data = [new() { Embedding = JsonDocument.Parse(json).RootElement.Clone() }]
        }, new Dictionary<string, string>());
        Assert.Throws<InvalidOperationException>(() => result.ToEmbeddingResponse());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LtxWholeSecondDurationPreservesProviderOverridePrecedence(bool useOverride)
    {
        JsonElement sent = default;
        var provider = CreateLtx(async request =>
        {
            sent = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.Clone();
            return AcceptedVideo();
        });
        await provider.StartVideoOperation(new VideoRequest
        {
            Model = "ltx-2-3", Prompt = "A cat", Resolution = "1080p", Duration = 6,
            ProviderOptions = useOverride
                ? new() { ["ltx"] = JsonSerializer.SerializeToElement(new { duration = 8 }) } : null
        });
        Assert.Equal(useOverride ? 8 : 6, sent.GetProperty("duration").GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LtxWholeSecondDurationDoesNotSilentlyRoundEitherInput(bool useOverride)
    {
        var provider = CreateLtx(_ => throw new InvalidOperationException("HTTP must not be called."));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => provider.StartVideoOperation(new VideoRequest
        {
            Model = "ltx-2-3", Prompt = "A cat", Resolution = "1080p", Duration = useOverride ? 6 : 6.5,
            ProviderOptions = useOverride
                ? new() { ["ltx"] = JsonSerializer.SerializeToElement(new { duration = 6.5 }) } : null
        }));
    }

    [Theory]
    [InlineData("retake")]
    [InlineData("extend")]
    public async Task LtxFractionalOperationsRetainFractionalSeconds(string operation)
    {
        JsonElement sent = default;
        var provider = CreateLtx(async request =>
        {
            sent = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.Clone();
            return AcceptedVideo();
        });
        const double seconds = 2.123456789012345;
        await provider.StartVideoOperation(new VideoRequest
        {
            Model = "ltx-2-3", Duration = seconds,
            ProviderOptions = new() { ["ltx"] = JsonSerializer.SerializeToElement(new
                { operation, video_data = "AQID", start_time = 0.125 }) }
        });
        Assert.Equal(seconds, sent.GetProperty("duration").GetDouble());
    }

    private static HttpResponseMessage AcceptedVideo()
        => new(HttpStatusCode.Accepted) { Content = new StringContent("{\"id\":\"job-id\"}", Encoding.UTF8, "application/json") };

    private static LTXProvider CreateLtx(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
        => new(new Keys(), new Clients(new HttpClient(new Handler(respond))));

    private sealed class Keys : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class Clients(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request);
    }
}
