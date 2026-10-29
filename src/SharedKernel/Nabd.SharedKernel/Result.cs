namespace Nabd.SharedKernel;

/// <summary>
/// Result object per 03 §8 "Errors": modules return Result objects; exceptions only at boundaries.
/// Keep it this small — it does not want to become a validation framework.
/// </summary>
public class Result<T>
{
        internal Result(T? value, bool isSuccess, string? error)
    {
        Value = value;
        IsSuccess = isSuccess;
        Error = error;
    }

    public T? Value { get; }
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
