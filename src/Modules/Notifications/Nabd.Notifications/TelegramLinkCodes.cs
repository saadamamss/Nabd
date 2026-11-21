namespace Nabd.Notifications;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// The one-time link code (FR-38, D9b). 16 Crockford-base32 chars = 80 random bits,
/// no ambiguous letters, easy to read from a phone. The stored form is an HMAC whose
/// key is DERIVED from NABD_SECRET_KEY with HKDF (domain separation — never the raw
/// AES key), so a database copy cannot be turned back into a usable code.
/// </summary>
public static class TelegramLinkCodes
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"; // Crockford: no I, L, O, U
    private const int CodeLength = 16;
    private static readonly byte[] Info = Encoding.UTF8.GetBytes("nabd/telegram/link-code/v1");

    /// <summary>The plaintext code — shown once and never stored.</summary>
    public static string Mint()
    {
        var bytes = RandomNumberGenerator.GetBytes((CodeLength * 5 + 7) / 8);   // 80 bits = 10 bytes
        var chars = new char[CodeLength];
        var bit = 0;
        for (var i = 0; i < chars.Length; i++)
        {
            var value = 0;
            for (var b = 0; b < 5; b++)
            {
                var byteIndex = bit / 8;
                var bitIndex = 7 - (bit % 8);
                value = (value << 1) | ((bytes[byteIndex] >> bitIndex) & 1);
                bit++;
            }
            chars[i] = Alphabet[value];
        }
        return new string(chars);
    }

    /// <summary>
    /// Reads a code from a bot message: `/start CODE` (also `/start@bot CODE`) or a bare
    /// CODE. Anything else is NOT a link attempt — the bot stays silent (no echo-spam).
    /// Crockford normalization: O→0, I/L→1, spaces/dashes dropped, upper-cased.
    /// </summary>
    public static string? Extract(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var trimmed = text.Trim();
        var rest = trimmed;
        if (trimmed.StartsWith('/'))
        {
            var space = trimmed.IndexOf(' ');
            var command = space < 0 ? trimmed : trimmed[..space];
            if (!command.Equals("/start", StringComparison.OrdinalIgnoreCase)
                && !command.StartsWith("/start@", StringComparison.OrdinalIgnoreCase))
                return null;
            rest = space < 0 ? "" : trimmed[(space + 1)..];
        }

        var normalized = rest.Replace("-", "").Replace(" ", "").ToUpperInvariant()
            .Replace('O', '0').Replace('I', '1').Replace('L', '1');

        if (normalized.Length != CodeLength) return null;
        foreach (var c in normalized)
            if (!Alphabet.Contains(c)) return null;
        return normalized;
    }

    /// <summary>The ONLY stored form: HMAC-SHA-256(HKDF(NABD_SECRET_KEY), code).</summary>
    public static byte[] Hash(string code)
    {
        var key = HKDF.DeriveKey(
            HashAlgorithmName.SHA256, SecretBox.Key(), 32, salt: null, info: Info);
        return HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(code));
    }
}
