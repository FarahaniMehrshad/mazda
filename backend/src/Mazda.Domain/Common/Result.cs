namespace Mazda.Domain.Common;

public readonly record struct Error(string Code, string Message)
{
    public static readonly Error None = new(string.Empty, string.Empty);
    public static Error NotFound(string what) => new("NotFound", $"{what} was not found.");
    public static Error Validation(string message) => new("Validation", message);
    public static Error Conflict(string message) => new("Conflict", message);
    public static Error External(string message) => new("External", message);
}

public class Result
{
    protected Result(bool success, Error error)
    {
        if (success && error != Error.None) throw new InvalidOperationException();
        if (!success && error == Error.None) throw new InvalidOperationException();
        IsSuccess = success;
        Error = error;
    }

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public Error Error { get; }

    public static Result Success() => new(true, Error.None);
    public static Result Failure(Error error) => new(false, error);
    public static Result<T> Success<T>(T value) => new(value, true, Error.None);
    public static Result<T> Failure<T>(Error error) => new(default!, false, error);
}

public class Result<T> : Result
{
    private readonly T _value;
    internal Result(T value, bool success, Error error) : base(success, error) => _value = value;
    public T Value => IsSuccess ? _value : throw new InvalidOperationException("Cannot access value of a failed result.");
}
