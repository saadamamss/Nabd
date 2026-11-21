// Nabd.Secrets — the secret door (P4 spec 08, D8h). NOT a chat message.
//
// Usage:
//   set -a; source .env; set +a
//   dotnet run --project src/Tools/Nabd.Secrets -- email \
//     --host smtp.gmail.com --port 587 --user you@gmail.com --from you@gmail.com --sender Nabd
//
// The password is read from NABD_SMTP_PASS (never argv: ps and shell history remember),
// sealed with NABD_SECRET_KEY, and written as the OWNER — the app has no write grant
// on notifications.channel_config.

using System.Text.Json;
using Npgsql;
using Nabd.Notifications;

if (args.Length == 0 || args[0] != "email")
{
    Console.Error.WriteLine("usage: nabd-secrets email --host H --port P --user U --from F [--sender NAME]");
    return 2;
}

string? host = null, user = null, from = null, sender = "Nabd";
var port = 587;

try
{
    for (var i = 1; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--host":   host = args[++i]; break;
            case "--port":   port = int.Parse(args[++i]); break;
            case "--user":   user = args[++i]; break;
            case "--from":   from = args[++i]; break;
            case "--sender": sender = args[++i]; break;
            default:
                Console.Error.WriteLine($"unknown flag: {args[i]}");
                return 2;
        }
    }
}
catch (IndexOutOfRangeException)
{
    Console.Error.WriteLine("a flag is missing its value");
    return 2;
}

if (host is null || from is null)
{
    Console.Error.WriteLine("--host and --from are required");
    return 2;
}

var password = Environment.GetEnvironmentVariable("NABD_SMTP_PASS")
    ?? throw new InvalidOperationException("NABD_SMTP_PASS must be set (read from env, never argv).");

var dbUser = Environment.GetEnvironmentVariable("POSTGRES_USER")
    ?? throw new InvalidOperationException("POSTGRES_USER must be set (the owner lane).");
var dbPass = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD")
    ?? throw new InvalidOperationException("POSTGRES_PASSWORD must be set.");
var dbName = Environment.GetEnvironmentVariable("POSTGRES_DB") ?? "nabd";
var dbHost = Environment.GetEnvironmentVariable("NABD_DB_HOST") ?? "localhost";
var dbPort = Environment.GetEnvironmentVariable("NABD_DB_PORT") ?? "5432";

var config = JsonSerializer.Serialize(new { host, port, user, from, senderName = sender });
var (cipher, nonce, tag) = SecretBox.Seal(password, "email");

await using var conn = new NpgsqlConnection(
    $"Host={dbHost};Port={dbPort};Database={dbName};Username={dbUser};Password={dbPass}");
await conn.OpenAsync();
await using var cmd = new NpgsqlCommand("""
    INSERT INTO notifications.channel_config
        (channel, enabled, config, secret_enc, secret_nonce, secret_tag, updated_at)
    VALUES ('email', true, @c::jsonb, @e, @n, @t, now())
    ON CONFLICT (channel) DO UPDATE SET
        enabled = true,
        config = EXCLUDED.config,
        secret_enc = EXCLUDED.secret_enc,
        secret_nonce = EXCLUDED.secret_nonce,
        secret_tag = EXCLUDED.secret_tag,
        updated_at = now();
    """, conn);
cmd.Parameters.AddWithValue("c", config);
cmd.Parameters.AddWithValue("e", cipher);
cmd.Parameters.AddWithValue("n", nonce);
cmd.Parameters.AddWithValue("t", tag);
await cmd.ExecuteNonQueryAsync();

Console.WriteLine($"email channel written: host={host} port={port} from={from} (secret sealed, not printed)");
return 0;
