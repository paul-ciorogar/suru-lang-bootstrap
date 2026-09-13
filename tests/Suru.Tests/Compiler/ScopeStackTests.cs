using Suru.Compiler;

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
    [Fact]
    public void FindsANameBoundInTheCurrentScope()
    {
        var scopes = new ScopeStack<int, string>();
        scopes.Declare("x", 1);

        Assert.True(scopes.TryLookup("x", out var value));
        Assert.Equal(1, value);
    }

    [Fact]
    public void FindsANameBoundFurtherOut()
    {
        var scopes = new ScopeStack<int, string>();
        scopes.Declare("x", 1);
        scopes.EnterNew();
        scopes.EnterNew();

        Assert.True(scopes.TryLookup("x", out var value));
        Assert.Equal(1, value);
    }

    [Fact]
    public void AnInnerBindingShadowsAnOuterOne()
    {
        var scopes = new ScopeStack<int, string>();
        scopes.Declare("x", 1);
        scopes.EnterNew();
        scopes.Declare("x", 2);

        Assert.True(scopes.TryLookup("x", out var value));
        Assert.Equal(2, value);
    }

    [Fact]
    public void ExitingRestoresTheShadowedBinding()
    {
        var scopes = new ScopeStack<int, string>();
        scopes.Declare("x", 1);
        scopes.EnterNew();
        scopes.Declare("x", 2);
        scopes.Exit();

        Assert.True(scopes.TryLookup("x", out var value));
        Assert.Equal(1, value);
    }

    [Fact]
    public void ABindingDoesNotOutliveItsScope()
    {
        var scopes = new ScopeStack<int, string>();
        scopes.EnterNew();
        scopes.Declare("x", 1);
        scopes.Exit();

        Assert.False(scopes.TryLookup("x", out _));
    }

    [Fact]
    public void DeclaredHereLooksNoFurtherThanTheInnermostScope()
    {
        var scopes = new ScopeStack<int, string>();
        scopes.Declare("x", 1);
        scopes.EnterNew();

        // Visible, but shadowing it is not a redeclaration.
        Assert.True(scopes.TryLookup("x", out _));
        Assert.False(scopes.DeclaredHere("x"));
    }

    [Fact]
    public void DeclaredHereSeesANameBoundInTheInnermostScope()
    {
        var scopes = new ScopeStack<int, string>();
        scopes.EnterNew();
        scopes.Declare("x", 1);

        Assert.True(scopes.DeclaredHere("x"));
    }

    [Fact]
    public void AnUnboundNameIsNotFound()
    {
        Assert.False(new ScopeStack<int, string>().TryLookup("x", out var value));
        Assert.Equal(0, value);
    }

    [Fact]
    public void TheOutermostScopeCannotBeExited()
    {
        var scopes = new ScopeStack<int, string>();
        scopes.EnterNew();
        scopes.Exit();

        Assert.Throws<InvalidOperationException>(scopes.Exit);
    }

    [Fact]
    public void FindsTheEnclosingScopeOfAKindAndItsData()
    {
        var scopes = new ScopeStack<int, string>();
        scopes.EnterNew(ScopeKind.Loop, "outer");
        scopes.EnterNew();

        Assert.True(scopes.TryFindEnclosing(ScopeKind.Loop, out var data));
        Assert.Equal("outer", data);
    }

    /// <summary>The innermost one wins, which is what makes a <c>break</c> leave the nearest loop.</summary>
    [Fact]
    public void TheNearestEnclosingScopeOfAKindWins()
    {
        var scopes = new ScopeStack<int, string>();
        scopes.EnterNew(ScopeKind.Loop, "outer");
        scopes.EnterNew(ScopeKind.Loop, "inner");

        Assert.True(scopes.TryFindEnclosing(ScopeKind.Loop, out var data));
        Assert.Equal("inner", data);
    }

    [Fact]
    public void AnEnclosingScopeOfAKindDoesNotOutliveItself()
    {
        var scopes = new ScopeStack<int, string>();
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
        var scopes = new ScopeStack<int, string>();
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
        var scopes = new ScopeStack<int, string>();
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
        var scopes = new ScopeStack<int, string>();
        scopes.EnterNew(ScopeKind.Function, "outer");
        scopes.EnterNew(ScopeKind.Function, "inner");
        scopes.EnterNew();

        Assert.True(scopes.TryFindEnclosing(ScopeKind.Function, out var data));
        Assert.Equal("inner", data);
    }

    /// <summary>Names are unaffected: only <see cref="ScopeStack{T, S}.TryFindEnclosing"/> has a barrier today.</summary>
    [Fact]
    public void ANameIsStillFoundThroughAFunctionScope()
    {
        var scopes = new ScopeStack<int, string>();
        scopes.Declare("x", 1);
        scopes.EnterNew(ScopeKind.Function);

        Assert.True(scopes.TryLookup("x", out var value));
        Assert.Equal(1, value);
    }
}
