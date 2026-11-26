namespace Nabd.Feedback;

/// <summary>
/// Feedback — the learning channel (P5 spec 06, plan/01 §7.3 L3).
/// Offers: submit a stamped message. Reads: none (the owner's read is the admin lane).
/// Emits: nothing. Listens: nothing.
/// </summary>
public static class FeedbackModule
{
    public const string RoomName = "Feedback";
}
