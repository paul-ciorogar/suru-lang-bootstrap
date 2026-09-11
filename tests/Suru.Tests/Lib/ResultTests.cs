using Suru.Lib;

namespace Suru.Tests.Lib;

public class ResultTests
{
    [Fact]
    public void MapsAValue()
    {
        Result<int, string> result = Result.Ok<int, string>(2);

        Assert.Equal(4, result.Map(x => x * 2).UnwrapOr(0));
    }

    [Fact]
    public void MapChangesTheValueType()
    {
        Result<int, string> result = Result.Ok<int, string>(2);

        Assert.Equal("2", result.Map(x => x.ToString()).UnwrapOr(""));
    }

    [Fact]
    public void MapLeavesAnErrorAlone()
    {
        var mapped = false;
        Result<int, string> result = Result.Error<int, string>("boom");

        var next = result.Map(x => { mapped = true; return x * 2; });

        Assert.False(mapped);
        Assert.Equal(0, next.UnwrapOr(0));
        Assert.Equal("boom", next.ErrorOr(""));
    }

    [Fact]
    public void MapErrorIsTheMirrorOfMap()
    {
        Assert.Equal(4, Result.Error<int, string>("boom").MapError(e => e.Length).ErrorOr(0));
        Assert.Equal(1, Result.Ok<int, string>(1).MapError(e => e.Length).UnwrapOr(0));
    }

    [Fact]
    public void AndThenKeepsAChainFlat()
    {
        static Result<int, string> Halve(int x) =>
            x % 2 == 0 ? Result.Ok<int, string>(x / 2) : Result.Error<int, string>("odd");

        Assert.Equal(2, Result.Ok<int, string>(8).AndThen(Halve).AndThen(Halve).UnwrapOr(0));
        Assert.Equal("odd", Result.Ok<int, string>(6).AndThen(Halve).AndThen(Halve).ErrorOr(""));
    }

    [Fact]
    public void AndThenSkipsAnErrorWithoutRunningTheStep()
    {
        var ran = false;

        var next = Result.Error<int, string>("boom").AndThen(x =>
        {
            ran = true;
            return Result.Ok<int, string>(x);
        });

        Assert.False(ran);
        Assert.Equal("boom", next.ErrorOr(""));
    }

    [Fact]
    public void MatchHandlesBothCases()
    {
        Assert.Equal("ok 1", Result.Ok<int, string>(1).Match(x => $"ok {x}", e => $"err {e}"));
        Assert.Equal("err boom", Result.Error<int, string>("boom").Match(x => $"ok {x}", e => $"err {e}"));
    }

    [Fact]
    public void UnwrapOrReturnsTheFallbackOnlyForAnError()
    {
        Assert.Equal(1, Result.Ok<int, string>(1).UnwrapOr(9));
        Assert.Equal(9, Result.Error<int, string>("boom").UnwrapOr(9));
    }

    [Fact]
    public void UnwrapOrElseBuildsTheFallbackFromTheErrorAndOnlyThen()
    {
        var built = 0;

        Assert.Equal(1, Result.Ok<int, string>(1).UnwrapOrElse(e => { built++; return e.Length; }));
        Assert.Equal(4, Result.Error<int, string>("boom").UnwrapOrElse(e => { built++; return e.Length; }));
        Assert.Equal(1, built);
    }

    [Fact]
    public void ErrorOrReturnsTheFallbackOnlyForAValue()
    {
        Assert.Equal("boom", Result.Error<int, string>("boom").ErrorOr("none"));
        Assert.Equal("none", Result.Ok<int, string>(1).ErrorOr("none"));
    }

    [Fact]
    public void IsOkAndIsError()
    {
        Assert.True(Result.Ok<int, string>(1).IsOk);
        Assert.False(Result.Ok<int, string>(1).IsError);
        Assert.False(Result.Error<int, string>("boom").IsOk);
        Assert.True(Result.Error<int, string>("boom").IsError);
    }

    [Fact]
    public void TryGetValueUnwrapsOnlyAValue()
    {
        Assert.True(Result.Ok<int, string>(1).TryGetValue(out var value));
        Assert.Equal(1, value);

        Assert.False(Result.Error<int, string>("boom").TryGetValue(out var missing));
        Assert.Equal(0, missing);
    }
}
