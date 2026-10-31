namespace Nabd.SharedKernel;

/// <summary>
/// Result object per 03 §8 "Errors": modules return Result objects; exceptions only at boundaries.
/// Keep it this small — it does not want to become a validation framework.
/// </summary>
public class Result<T>
{
    // failure carries no payload — so the stored field must be nullable
    private readonly T? _value;

    internal Result(T? value, bool isSuccess, string? error)
    {
        _value = value;
        IsSuccess = isSuccess;
        Error = error;
    }

    /// <summary>
    /// The payload of a SUCCESS — guaranteed non-null for every consumer (reading it
    /// on a failure is a caller bug with a loud message, never a silent null).
    /// </summary>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Result is a failure: {Error}");

    public bool IsSuccess { get; }
    public string? Error { get; }

    public static Result<T> Success(T value) => new(value, true, null);
    public static Result<T> Failure(string error) => new(default, false, error);
}

public static class Result
{
    public static Result<Unit> Success() => new(Unit.Value, true, null);
    public static Result<Unit> Failure(string error) => new(default, false, error);
}

/// <summary>Placeholder unit type for void operations (top-level so Result&lt;Unit&gt; works outside this file).</summary>
public readonly struct Unit
{
    public static readonly Unit Value = default;
}
