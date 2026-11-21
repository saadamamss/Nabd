// Nabd.Secrets — the secret door (P4 spec 08, extended by spec 09). NOT a chat message.
//
// Usage:
//   set -a; source .env; set +a
//   dotnet run --project src/Tools/Nabd.Secrets -- email \
//     --host smtp.gmail.com --port 587 --user you@gmail.com --from you@gmail.com [--sender Nabd]
//   dotnet run --project src/Tools/Nabd.Secrets -- telegram [--username your_bot]
//
// Secrets are read from the environment (never argv: ps and shell history remember),
// sealed with NABD_SECRET_KEY, and written as the OWNER — the app has no write grant
// on notifications.channel_config. The bot USERNAME is not a secret: it comes from
// NABD_TELEGRAM_BOT_USERNAME (or --username, which wins) and is stored in `config`.

using System.Text.Json;
using Npgsql;
using Nabd.Notifications;

int exit;
if (args.Length == 0 || (args[0] != "email" && args[0] != "telegram"))
{
    Console.Error.WriteLine(
        "usage: nabd-secrets email --host H --port P --user U --from F [--sender NAME]\n" +
        "       nabd-secrets telegram [--username BOT]   (default: NABD_TELEGRAM_BOT_USERNAME)");
    exit = 2;
}
else if (args[0] == "email")
{
    exit = await WriteEmailAsync(args);
}
else
{
    exit = await WriteTelegramAsync(args);
}
return exit;

static async Task<int> WriteEmailAsync(string[] args)
{
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

    var config = JsonSerializer.Serialize(new { host, port, user, from, senderName = sender });
    var (cipher, nonce, tag) = SecretBox.Seal(password, "email");

    await using var conn = await OpenOwnerAsync();
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
}

static async Task<int> WriteTelegramAsync(string[] args)
{
    // Non-secret, so the .env line is the natural default; the flag overrides it.
    string? username = Environment.GetEnvironmentVariable("NABD_TELEGRAM_BOT_USERNAME");

    try
    {
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--username": username = args[++i]; break;
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

    if (string.IsNullOrWhiteSpace(username))
    {
        Console.Error.WriteLine(
            "the bot's @name is required — set NABD_TELEGRAM_BOT_USERNAME in .env or pass --username");
        return 2;
    }

    var token = Environment.GetEnvironmentVariable("NABD_TELEGRAM_BOT_TOKEN")
        ?? throw new InvalidOperationException("NABD_TELEGRAM_BOT_TOKEN must be set (read from env, never argv).");

    var config = JsonSerializer.Serialize(new { username });
    var (cipher, nonce, tag) = SecretBox.Seal(token, "telegram");

    await using var conn = await OpenOwnerAsync();
    await using var cmd = new NpgsqlCommand("""
        INSERT INTO notifications.channel_config
            (channel, enabled, config, secret_enc, secret_nonce, secret_tag, updated_at)
        VALUES ('telegram', true, @c::jsonb, @e, @n, @t, now())
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

    Console.WriteLine($"telegram channel written: @{username} (token sealed, not printed)");
    return 0;
}

static async Task<NpgsqlConnection> OpenOwnerAsync()
{
    var dbUser = Environment.GetEnvironmentVariable("POSTGRES_USER")
        ?? throw new InvalidOperationException("POSTGRES_USER must be set (the owner lane).");
    var dbPass = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD")
        ?? throw new InvalidOperationException("POSTGRES_PASSWORD must be set.");
    var dbName = Environment.GetEnvironmentVariable("POSTGRES_DB") ?? "nabd";
    var dbHost = Environment.GetEnvironmentVariable("NABD_DB_HOST") ?? "localhost";
    var dbPort = Environment.GetEnvironmentVariable("NABD_DB_PORT") ?? "5432";

    var conn = new NpgsqlConnection(
        $"Host={dbHost};Port={dbPort};Database={dbName};Username={dbUser};Password={dbPass}");
    await conn.OpenAsync();
    return conn;
}
