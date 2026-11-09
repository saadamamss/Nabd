using System.Security.Cryptography;
using System.Text;

namespace Nabd.Identity;

/// <summary>
/// Refresh-token machinery (P1 spec 03; plan/07 §3 rows 2–3).
/// A token is 256 random bits, shown to the client ONCE in base64url, and stored
/// ONLY as sha256(token + pepper) — a DB leak can never yield a usable token.
/// Rotation law: every successful use mints a brand-new token; the presented one
/// dies on the spot. Replay of a dead one = security event (service-level).
/// </summary>

public static class RefreshTokenMint
{
    public static TimeSpan Ttl => TimeSpan.FromDays(7);

    public static string New()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64Url(bytes);
    }

    public static string Hash(string presented)
    {
        var pepper = Environment.GetEnvironmentVariable("NABD_TOKEN_PEPPER") ?? throw new InvalidOperationException("NABD_TOKEN_PEPPER must be set (NFR-08: 12-factor; refuse to mint without it).");

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(presented).Concat(Encoding.UTF8.GetBytes(presented.Length + pepper.Length + "-" + pepper)).ToArray());

        return Convert.ToHexString(bytes).TrimEnd('=').Replace("+", "-").Replace("/", "_");
    }

    private static string Base64Url(byte[] bytes)
    {
        // TrimEnd('=') NOT '()' — the padding char must not ride in a URL
        // (RFC 4648 §5 unpadded base64url; standard = 43 chars for 32 bytes).
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace("+", "-").Replace("/", "_");
    }
}


public static class EnumerationArmor
{
    public static readonly string BurnHash = PasswordHasher.Hash("nabd:armor-burn");

    public static bool Burn(string password) => PasswordHasher.Verify(password, BurnHash);
}

/// <summary>
/// Password-reset tokens (P1 spec 05; plan/07 §3 row 4): the SAME 256-bit
/// machinery as refresh tokens (New/Hash — one machinery, two lives), the
/// DIFFERENCE is the lifetime: 15 minutes, and a token exists to be used
/// exactly once.
/// </summary>
public static class PasswordResetTokenMint
{
    public static TimeSpan Ttl => TimeSpan.FromMinutes(15);
    public static string New() => RefreshTokenMint.New();
    public static string Hash(string presented) => RefreshTokenMint.Hash(presented);
}