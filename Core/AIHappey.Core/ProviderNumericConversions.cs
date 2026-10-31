namespace AIHappey.Core;

internal static class ProviderNumericConversions
{
    // Use only at provider boundaries that require integral values (e.g. sample rates in Hz).
    // Never round or truncate a valid fractional Vercel number to satisfy an integer API.
    internal static int? ToInt32Exact(double? value, string parameterName)
    {
        if (value is null)
            return null;

        if (!double.IsFinite(value.Value)
            || value < int.MinValue || value > int.MaxValue
            || Math.Truncate(value.Value) != value.Value)
        {
            throw new ArgumentOutOfRangeException(parameterName, value,
                "The provider requires a finite, integral 32-bit value.");
        }

        return checked((int)value.Value);
    }

    // The standalone Responses DTO still exposes a float. Permit narrowing only when
    // its JSON number is unchanged; otherwise fail explicitly rather than send a rounded value.
    internal static float? ToSinglePreservingJson(double? value, string parameterName)
    {
        if (value is null)
            return null;

        if (double.IsFinite(value.Value) && value >= -float.MaxValue && value <= float.MaxValue)
        {
            var single = (float)value.Value;
            if (System.Text.Json.JsonSerializer.SerializeToElement(single).GetDouble() == value.Value)
                return single;
        }

        throw new ArgumentOutOfRangeException(parameterName, value,
            "The standalone Responses contract cannot represent this number without losing JSON precision.");
    }
}
