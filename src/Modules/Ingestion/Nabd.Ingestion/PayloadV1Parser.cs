namespace Nabd.Ingestion;

using Nabd.Ingestion.Contracts;

/// <summary>
/// RED STUB (P3 spec 03 scaffolding): the payload-v1 parser — the owner's real
/// implementation replaces this from study/implementation-code.md. The real one
/// parses the byte-exact shape the simulator composes (plan/05 §5):
///   {"v":1,"ts":"...Z","seq":N,"rr":[{"t":<number>,"c":"<key>"}, ...]}
/// and reports ANY structural violation (bad JSON, wrong version, missing
/// fields, bad seq, count bound) as DLQ_INVALID-class failure.
/// </summary>
public static class PayloadV1Parser
{
    public static bool TryParse(ReadOnlySpan<byte> bytes, out ReadingBatch? batch, out string? error)
    {
        batch = null;
        error = "RED stub — not implemented";
        return false;
    }
}
