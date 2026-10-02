using Suru.Compiler;
using Suru.Lib;

namespace Suru.Tests.Compiler;

/// <summary>
/// The structure behind block scope, shared by semantic analysis and codegen. Tested
/// directly because both stages depend on the same shadowing behaviour.
/// <para>
/// The scope data is a <c>string</c> throughout, standing in for the loop's two basic blocks
/// codegen carries, so a payload coming back out of a search is visibly the one that went in.
/// </para>
/// </summary>
public class ScopeStackTests
{
    /// <summary>
    /// A binding, standing in for the two each stage has. <see cref="Value"/> identifies it, and
    /// <see cref="SurvivesFunctionBoundary"/> is the only thing the structure itself reads.
    /// </summary>
    private sealed record Entry(int Value, bool SurvivesFunctionBoundary) : IScopeEntry;

    /// <summary>A binding hidden by the barrier — a variable, in both stages.</summary>
    private static Entry Local(int value) => new(value, SurvivesFunctionBoundary: false);

    /// <summary>A binding the barrier lets through — a function name, in both stages.</summary>
    private static Entry Fn(int value) => new(value, SurvivesFunctionBoundary: true);

    /// <summary>A lookup that found <paramref name="entry"/>, to compare a whole result against.</summary>
    private static Result<Entry, Unbound> Found(Entry entry) => Result.Ok<Entry, Unbound>(entry);

    private static readonly Result<Entry, Unbound> NotFound = Result.Error<Entry, Unbound>(default);

    [Fact]
    public void FindsANameBoundInTheCurrentScope()
    {
        var scopes = new ScopeStack<Entry, string>();
        scopes.Declare("x", Local(1));

        Assert.Equal(Found(Local(1)), scopes.LookupVariable("x"));
    }

    [Fact]
    public void FindsANameBoundFurtherOut()
    {
        var scopes = new ScopeStack<Entry, string>();
        scopes.Declare("x", Local(1));
        scopes.EnterNew();
        scopes.EnterNew();

        Assert.Equal(Found(Local(1)), scopes.LookupVariable("x"));
    }

    [Fact]
    public void AnInnerBindingShadowsAnOuterOne()
    {
        var scopes = new ScopeStack<Entry, string>();
        scopes.Declare("x", Local(1));
        scopes.EnterNew();
        scopes.Declare("x", Local(2));

        Assert.Equal(Found(Local(2)), scopes.LookupVariable("x"));
    }

    [Fact]
    public void ExitingRestoresTheShadowedBinding()
    {
        var scopes = new ScopeStack<Entry, string>();
        scopes.Declare("x", Local(1));
        scopes.EnterNew();
        scopes.Declare("x", Local(2));
        scopes.Exit();

        Assert.Equal(Found(Local(1)), scopes.LookupVariable("x"));
    }

    [Fact]
    public void ABindingDoesNotOutliveItsScope()
    {
        var scopes = new ScopeStack<Entry, string>();
        scopes.EnterNew();
        scopes.Declare("x", Local(1));
        scopes.Exit();

        Assert.Equal(NotFound, scopes.LookupVariable("x"));
    }

    [Fact]
    public void DeclaredHereLooksNoFurtherThanTheInnermostScope()
    {
        var scopes = new ScopeStack<Entry, string>();
        scopes.Declare("x", Local(1));
        scopes.EnterNew();

        // Visible, but shadowing it is not a redeclaration.
        Assert.True(scopes.LookupVariable("x").IsOk);
        Assert.False(scopes.DeclaredHere("x"));
    }

    [Fact]
    public void DeclaredHereSeesANameBoundInTheInnermostScope()
    {
        var scopes = new ScopeStack<Entry, string>();
        scopes.EnterNew();
        scopes.Declare("x", Local(1));

        Assert.True(scopes.DeclaredHere("x"));
    }

    [Fact]
    public void AnUnboundNameIsNotFound()
    {
        Assert.Equal(NotFound, new ScopeStack<Entry, string>().LookupVariable("x"));
    }

    [Fact]
    public void TheOutermostScopeCannotBeExited()
    {
        var scopes = new ScopeStack<Entry, string>();
        scopes.EnterNew();
        scopes.Exit();

        Assert.Throws<InvalidOperationException>(scopes.Exit);
    }

    [Fact]
    public void FindsTheEnclosingScopeOfAKindAndItsData()
    {
        var scopes = new ScopeStack<Entry, string>();
        scopes.EnterNew(ScopeKind.Loop, "outer");
        scopes.EnterNew();

        Assert.True(scopes.TryFindEnclosing(ScopeKind.Loop, out var data));
        Assert.Equal("outer", data);
    }

    /// <summary>The innermost one wins, which is what makes a <c>break</c> leave the nearest loop.</summary>
    [Fact]
    public void TheNearestEnclosingScopeOfAKindWins()
    {
        var scopes = new ScopeStack<Entry, string>();
        scopes.EnterNew(ScopeKind.Loop, "outer");
        scopes.EnterNew(ScopeKind.Loop, "inner");

        Assert.True(scopes.TryFindEnclosing(ScopeKind.Loop, out var data));
        Assert.Equal("inner", data);
    }

    [Fact]
    public void AnEnclosingScopeOfAKindDoesNotOutliveItself()
    {
        var scopes = new ScopeStack<Entry, string>();
        scopes.EnterNew(ScopeKind.Loop, "loop");
        scopes.Exit();

        Assert.False(scopes.TryFindEnclosing(ScopeKind.Loop, out _));
    }

    /// <summary>
    /// The barrier, and the reason the kinds exist at all: a <c>break</c> written inside a
    /// function called from a loop does not belong to that loop, and the search stops before it
    /// can say otherwise. Nothing enters a function scope yet — this is the rule the day
    /// something does.
    /// </summary>
    [Fact]
    public void TheSearchStopsAtAFunctionScope()
    {
        var scopes = new ScopeStack<Entry, string>();
        scopes.EnterNew(ScopeKind.Loop, "loop");
        scopes.EnterNew(ScopeKind.Function);
        scopes.EnterNew();

        Assert.False(scopes.TryFindEnclosing(ScopeKind.Loop, out _));
    }

    /// <summary>
    /// A loop inside the function is found, so the barrier hides only what is outside it.
    /// </summary>
    [Fact]
    public void TheSearchStillFindsALoopInsideTheFunction()
    {
        var scopes = new ScopeStack<Entry, string>();
        scopes.EnterNew(ScopeKind.Loop, "caller");
        scopes.EnterNew(ScopeKind.Function);
        scopes.EnterNew(ScopeKind.Loop, "callee");

        Assert.True(scopes.TryFindEnclosing(ScopeKind.Loop, out var data));
        Assert.Equal("callee", data);
    }

    /// <summary>Asking for a function scope finds the enclosing one and looks no further out.</summary>
    [Fact]
    public void TheSearchFindsTheEnclosingFunctionItself()
    {
        var scopes = new ScopeStack<Entry, string>();
        scopes.EnterNew(ScopeKind.Function, "outer");
        scopes.EnterNew(ScopeKind.Function, "inner");
        scopes.EnterNew();

        Assert.True(scopes.TryFindEnclosing(ScopeKind.Function, out var data));
        Assert.Equal("inner", data);
    }

    /// <summary>
    /// The barrier as a variable lookup sees it: a function body cannot read the locals of
    /// whatever encloses it, so there are no closures to explain and no frame to reach into.
    /// </summary>
    [Fact]
    public void AVariableIsHiddenBeyondAFunctionScope()
    {
        var scopes = new ScopeStack<Entry, string>();
        scopes.Declare("x", Local(1));
        scopes.EnterNew(ScopeKind.Function);

        Assert.Equal(NotFound, scopes.LookupVariable("x"));
    }

    /// <summary>
    /// The same lookup, and the reason the tag is per entry rather than per scope: a function
    /// name is declared in the scope <i>around</i> its body, so it has to survive the crossing
    /// or a function could not call itself.
    /// </summary>
    [Fact]
    public void ABindingThatSurvivesTheBoundaryIsStillFound()
    {
        var scopes = new ScopeStack<Entry, string>();
        scopes.Declare("f", Fn(1));
        scopes.EnterNew(ScopeKind.Function);

        Assert.Equal(Found(Fn(1)), scopes.LookupVariable("f"));
    }

    /// <summary>A parameter is declared in the function scope itself — this side of the barrier.</summary>
    [Fact]
    public void ABindingInTheFunctionScopeItselfIsFound()
    {
        var scopes = new ScopeStack<Entry, string>();
        scopes.EnterNew(ScopeKind.Function);
        scopes.Declare("a", Local(1));
        scopes.EnterNew();

        Assert.Equal(Found(Local(1)), scopes.LookupVariable("a"));
    }

    /// <summary>The function lookup has no barrier at all, which is what makes recursion work.</summary>
    [Fact]
    public void AFunctionIsFoundThroughAnyNumberOfFunctionScopes()
    {
        var scopes = new ScopeStack<Entry, string>();
        scopes.Declare("f", Fn(1));
        scopes.EnterNew(ScopeKind.Function);
        scopes.EnterNew(ScopeKind.Function);

        Assert.Equal(Found(Fn(1)), scopes.LookupFunction("f"));
    }

    /// <summary>
    /// The function lookup does not filter by the tag either — it is the caller's business what
    /// it found. A variable of that name is a mistake the stage reports, not one the walk hides.
    /// </summary>
    [Fact]
    public void TheFunctionLookupReturnsWhateverTheNameIsBoundTo()
    {
        var scopes = new ScopeStack<Entry, string>();
        scopes.Declare("x", Local(1));
        scopes.EnterNew(ScopeKind.Function);

        Assert.Equal(Found(Local(1)), scopes.LookupFunction("x"));
    }

    /// <summary>
    /// A hidden binding does not stop the walk: an outer name that does survive is still found,
    /// so a local shadowing a function leaves that function visible from a nested body.
    /// </summary>
    [Fact]
    public void TheWalkContinuesPastABindingTheBarrierHides()
    {
        var scopes = new ScopeStack<Entry, string>();
        scopes.Declare("f", Fn(1));
        scopes.EnterNew();
        scopes.Declare("f", Local(2));
        scopes.EnterNew(ScopeKind.Function);

        Assert.Equal(Found(Fn(1)), scopes.LookupVariable("f"));
    }
}
