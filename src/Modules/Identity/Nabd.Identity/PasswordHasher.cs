using System.Security.Cryptography;
using Konscious.Security.Cryptography;

namespace Nabd.Identity;

/// <summary>
/// Password hashing per plan/07 §3 row 1 — argon2id, per-user salt.
/// Stored string is PHC-style: argon2id$v=19$m=…,t=…,p=…$salt$hash (self-describing,
/// so future parameter upgrades verify old strings and re-hash on next successful login).
/// NEVER used for machine secrets — device tokens stay a keyed fast hash (plan/07 §4 mine).
/// </summary>
public static class PasswordHasher
{
    private const int SaltSize = 16;        // bytes, per user
    private const int HashSize = 32;        // bytes
    private const int MemoryKib = 65536;    // 64 MiB — §3's modern default slot
    private const int Iterations = 3;
    private const int Parallelism = 1;      // one lane per container core solitaire at P1

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Compute(password, salt);
        return $"argon2id$v=19$m={MemoryKib},t={Iterations},p={Parallelism}" +
               $"${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        // parse; anything malformed counts as a failed match (fail-shut, no exceptions out)
        var parts = stored.Split('$');
        if (parts.Length != 6) return false;

        var memory = ParseParam(parts[3], 'm');
        var time   = ParseParam(parts[3], 't');
        var lanes  = ParseParam(parts[3], 'p');
        if (memory is null || time is null || lanes is null) return false;

        var salt = Convert.FromBase64String(parts[4]);
        var expected = Convert.FromBase64String(parts[5]);
        var actual = ComputeExact(password, salt, memory.Value, time.Value, lanes.Value);

        // constant-time compare regardless of mismatch position (plan/07 §4 note)
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Compute(string password, byte[] salt)
        => ComputeExact(password, salt, MemoryKib, Iterations, Parallelism);

    private static byte[] ComputeExact(string password, byte[] salt, int memory, int time, int lanes)
    {
        var argon2 = new Argon2id(System.Text.Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memory,
            Iterations = time,
            DegreeOfParallelism = lanes
        };
        return argon2.GetBytes(HashSize);
    }

    private static int? ParseParam(string source, char name)
    {
        // $m=65536,t=3,p=1 → the int after "name="; 0/NAN = failure
        foreach (var piece in source.Split(','))
        {
            if (piece.Length >= 2 && piece[0] == name && piece[1] == '=' &&
                int.TryParse(piece[2..], out var val) && val > 0)
                return val;
        }
        return null;
    }
}
