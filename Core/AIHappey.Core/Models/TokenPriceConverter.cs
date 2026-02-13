
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Models;

public sealed class TokenPriceConverter : JsonConverter<decimal>
{
    private const decimal PerMillion = 1_000_000m;

    public override decimal Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        var perToken = reader.TokenType switch
        {
            JsonTokenType.String => decimal.Parse(reader.GetString()!, CultureInfo.InvariantCulture),
            JsonTokenType.Number => reader.GetDecimal(),
            _ => throw new JsonException("Invalid token price")
        };

        return perToken;
    }

    public override void Write(
        Utf8JsonWriter writer,
        decimal value,
        JsonSerializerOptions options)
    {
        // internal value is already per 1M → write as number
        writer.WriteNumberValue(value);
    }
}
