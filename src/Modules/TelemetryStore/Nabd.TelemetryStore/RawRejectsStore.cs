namespace Nabd.TelemetryStore;

using Nabd.TelemetryStore.Contracts;

/// <summary>
/// The rejection-forensics writer (P3 spec 06): ONE parameterized INSERT into
/// telemetry.raw_rejects (0019) — append-only by grant (nabd_app holds
/// INSERT+SELECT; no UPDATE/DELETE on raw forensics). A failure here
/// PROPAGATES — the same ack law as the readings store: never swallowed.
/// </summary>
public sealed class RawRejectsStore(TelemetryStoreDb db) : IRawRejectStore
{
    public async Task StoreAsync(RawRejectDto reject, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var cmd = new Npgsql.NpgsqlCommand("""
            INSERT INTO telemetry.raw_rejects
                (time, received_at, tenant_id, device_id, reason, payload)
            VALUES (@t, @r, @tn, @d, @reason, @p)
            """, conn);

        cmd.Parameters.AddWithValue("t", reject.TimeUtc);
        cmd.Parameters.AddWithValue("r", reject.ReceivedAtUtc);
        cmd.Parameters.AddWithValue("tn", (object?)reject.TenantId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("d", (object?)reject.DeviceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("reason", reject.Reason);
        cmd.Parameters.AddWithValue("p", reject.Payload);

        await cmd.ExecuteNonQueryAsync(ct);
    }
}