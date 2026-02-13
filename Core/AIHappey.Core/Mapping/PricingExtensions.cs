namespace AIHappey.Core.Models;

public static class PricingExtensions
{
    private const decimal Million = 1_000_000m;
    private const decimal SecondsPerHour = 3600m;

    /// <summary>
    /// Converts price per million tokens to price per token.
    /// </summary>
    public static decimal PerMillionToPerToken(this decimal pricePerMillion)
        => pricePerMillion / Million;

    /// <summary>
    /// Converts price per hour to price per second.
    /// </summary>
    public static decimal PerHourToPerSecond(this decimal pricePerHour)
        => pricePerHour / SecondsPerHour;
}
