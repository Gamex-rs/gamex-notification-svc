using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace Gamex.Notifications.Infrastructure.Security;

/// <summary>Public key and expected claims of tokens issued by the User Library Service.</summary>
public sealed class JwtValidationOptions
{
    /// <summary>Null only in Development without <c>JWT_PUBLIC_KEY</c>: every token is then rejected.</summary>
    public RsaSecurityKey? Key { get; init; }

    /// <summary><c>JWT_ISSUER</c>; validated when set.</summary>
    public string? Issuer { get; init; }

    /// <summary><c>JWT_AUDIENCE</c> of access tokens; validated when set.</summary>
    public string? Audience { get; init; }

    public static RsaSecurityKey? LoadKey(string? pem)
    {
        if (string.IsNullOrWhiteSpace(pem))
        {
            return null;
        }

        // Environment variables often carry the PEM with escaped newlines.
        var rsa = RSA.Create();
        rsa.ImportFromPem(pem.Replace("\\n", "\n", StringComparison.Ordinal));
        return new RsaSecurityKey(rsa);
    }
}
