namespace Nabd.Notifications;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// AES-256-GCM for `notifications.channel_config` (plan/04 §9: encrypted at rest).
/// The key is NFR-08 config (base64, 32 bytes) and MUST exist — a missing key is a
/// startup-class error, never a quiet plaintext fallback. The AAD binds a ciphertext
/// to its channel, so a row's secret cannot be replayed into another row.
/// </summary>
public static class SecretBox
{
    private const int NonceBytes = 12;   // GCM standard
    private const int TagBytes = 16;

    public static (byte[] Cipher, byte[] Nonce, byte[] Tag) Seal(string plaintext, string aad)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagBytes];

        using var aes = new AesGcm(Key(), TagBytes);
        aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(aad));
        return (cipher, nonce, tag);
    }

    public static string Open(byte[] cipher, byte[] nonce, byte[] tag, string aad)
    {
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(Key(), TagBytes);
        aes.Decrypt(nonce, cipher, tag, plain, Encoding.UTF8.GetBytes(aad));   // tamper ⇒ throws
        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] Key()
    {
        var raw = Environment.GetEnvironmentVariable("NABD_SECRET_KEY");
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException(
                "NABD_SECRET_KEY is not set — refusing to touch encrypted config (NFR-08: fail-shut).");

        byte[] key;
        try { key = Convert.FromBase64String(raw); }
        catch (FormatException)
        {
            throw new InvalidOperationException("NABD_SECRET_KEY must be base64 (32 bytes).");
        }

        return key.Length == 32
            ? key
            : throw new InvalidOperationException(
                $"NABD_SECRET_KEY must decode to 32 bytes (got {key.Length}).");
    }
}
