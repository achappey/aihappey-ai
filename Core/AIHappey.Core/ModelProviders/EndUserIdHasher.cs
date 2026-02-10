using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace AIHappey.Core.ModelProviders;

public sealed class EndUserIdHasher(IOptions<EndUserIdHashingOptions> options)
{
    private readonly EndUserIdHashingOptions _options = options.Value;

    public string? Hash(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var salt = _options.SecretSalt?.Trim();
        if (string.IsNullOrWhiteSpace(salt))
            return null;

        var normalized = input.Trim();
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(salt));
        var bytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

