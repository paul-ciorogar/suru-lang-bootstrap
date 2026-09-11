using System.Diagnostics.CodeAnalysis;

namespace Suru.Lib;

/// <summary>
/// Either a value of <typeparamref name="T"/> or a failure of <typeparamref name="S"/>.
/// A caller that never asks which one it holds cannot forget to handle the failure:
/// <see cref="Map{TNext}"/> and <see cref="AndThen{TNext}"/> carry a failure through
/// untouched, and every way back to a bare value names what happens when there isn't one.
/// </summary>
public interface Result<T, S>
{
    bool IsOk { get; }

    bool IsError { get; }

    /// <summary>Applies <paramref name="f"/> to the value; a failure passes through unchanged.</summary>
    Result<TNext, S> Map<TNext>(Func<T, TNext> f);

    /// <summary>Applies <paramref name="f"/> to the failure; a value passes through unchanged.</summary>
    Result<T, SNext> MapError<SNext>(Func<S, SNext> f);

    /// <summary>
    /// <see cref="Map{TNext}"/> for a step that can fail too. The one that keeps a chain flat —
    /// <c>Map</c> with a fallible <paramref name="f"/> would give a result inside a result.
    /// </summary>
    Result<TNext, S> AndThen<TNext>(Func<T, Result<TNext, S>> f);

    /// <summary>Handles both cases at once. Every other member here could be written in terms of it.</summary>
    R Match<R>(Func<T, R> ok, Func<S, R> error);

    /// <summary>The value, or <paramref name="fallback"/> if this is a failure.</summary>
    T UnwrapOr(T fallback);

    /// <summary>The value, or one built from the failure — for a fallback that costs something.</summary>
    T UnwrapOrElse(Func<S, T> fallback);

    /// <summary>The failure, or <paramref name="fallback"/> if this is a value.</summary>
    S ErrorOr(S fallback);

    /// <summary>Idiomatic C# unwrap: <c>if (result.TryGetValue(out var value))</c>.</summary>
    bool TryGetValue([MaybeNullWhen(false)] out T value);
}

/// <summary>Entry points that infer both type arguments, so a caller writes neither.</summary>
public static class Result
{
    public static Result<T, S> Ok<T, S>(T value) => new Ok<T, S>(value);

    public static Result<T, S> Error<T, S>(S error) => new Error<T, S>(error);
}

public sealed record Ok<T, S>(T Value) : Result<T, S>
{
    public bool IsOk => true;

    public bool IsError => false;

    public Result<TNext, S> Map<TNext>(Func<T, TNext> f) => new Ok<TNext, S>(f(Value));

    public Result<T, SNext> MapError<SNext>(Func<S, SNext> f) => new Ok<T, SNext>(Value);

    public Result<TNext, S> AndThen<TNext>(Func<T, Result<TNext, S>> f) => f(Value);

    public R Match<R>(Func<T, R> ok, Func<S, R> error) => ok(Value);

    public T UnwrapOr(T fallback) => Value;

    public T UnwrapOrElse(Func<S, T> fallback) => Value;

    public S ErrorOr(S fallback) => fallback;

    public bool TryGetValue([MaybeNullWhen(false)] out T value)
    {
        value = Value;
        return true;
    }
}

public sealed record Error<T, S>(S Failure) : Result<T, S>
{
    public bool IsOk => false;

    public bool IsError => true;

    public Result<TNext, S> Map<TNext>(Func<T, TNext> f) => new Error<TNext, S>(Failure);

    public Result<T, SNext> MapError<SNext>(Func<S, SNext> f) => new Error<T, SNext>(f(Failure));

    public Result<TNext, S> AndThen<TNext>(Func<T, Result<TNext, S>> f) => new Error<TNext, S>(Failure);

    public R Match<R>(Func<T, R> ok, Func<S, R> error) => error(Failure);

    public T UnwrapOr(T fallback) => fallback;

    public T UnwrapOrElse(Func<S, T> fallback) => fallback(Failure);

    public S ErrorOr(S fallback) => Failure;

    public bool TryGetValue([MaybeNullWhen(false)] out T value)
    {
        value = default;
        return false;
    }
}
