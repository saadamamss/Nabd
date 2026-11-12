using System.Security.Cryptography;
using System.Text;

namespace Nabd.Assets;

/// <summary>
/// Device-token hashing per NFR-07 + plan/05 §11: HMAC-SHA-256 keyed with a
/// SERVER-side pepper. The token is 32 random bytes — high entropy — so a slow
/// password hash would only add CPU cost and a reconnect-storm DoS vector.
/// The pepper is env-only, fail-shut: no pepper = the class refuses to exist
/// (constructor throws AT STARTUP — the app never half-boots without it).
/// Distinct pepper from identity's (NABD_DEVICE_TOKEN_PEPPER): rotating one
/// never re-keys the other's stored hashes.
/// </summary>
public sealed class DeviceTokenHasher
{
    private readonly byte[] _pepper;

    public DeviceTokenHasher()
    {
        var pepper = Environment.GetEnvironmentVariable("NABD_DEVICE_TOKEN_PEPPER")
            ?? throw new InvalidOperationException(
                "NABD_DEVICE_TOKEN_PEPPER must be set (NFR-08: 12-factor; refuse to mint device tokens without it).");
        _pepper = Encoding.UTF8.GetBytes(pepper);
    }

    /// <summary>32 random bytes, Base64Url — the plaintext token, shown ONCE.</summary>
    public static string Mint()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
    /// <summary>HMAC-SHA-256(pepper, token) as lowercase hex — the ONLY stored form.</summary>
    public string Hash(string presentedToken)
    {
        var digest = HMACSHA256.HashData(_pepper, Encoding.UTF8.GetBytes(presentedToken));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}