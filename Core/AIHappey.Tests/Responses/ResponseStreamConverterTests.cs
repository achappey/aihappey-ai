using System.Text.Json;
using AIHappey.Responses.Streaming;

namespace AIHappey.Tests.Responses;

public sealed class ResponseStreamConverterTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"type":42}""")]
    [InlineData("[]")]
    public void Deserialize_missing_or_invalid_type_throws_json_exception(string json)
    {
        var exception = Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<ResponseStreamPart>(json));

        Assert.Contains("required string 'type'", exception.Message);
    }
}
