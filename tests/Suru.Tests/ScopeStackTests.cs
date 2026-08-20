using Suru.Compiler;

namespace Suru.Tests;

/// <summary>
/// The structure behind block scope, shared by semantic analysis and codegen. Tested
/// directly because both stages depend on the same shadowing behaviour.
/// </summary>
public class ScopeStackTests
{
    [Fact]
    public void FindsANameBoundInTheCurrentScope()
    {
        var scopes = new ScopeStack<int>();
        scopes.Declare("x", 1);

        Assert.True(scopes.TryLookup("x", out var value));
        Assert.Equal(1, value);
    }

    [Fact]
    public void FindsANameBoundFurtherOut()
    {
        var scopes = new ScopeStack<int>();
        scopes.Declare("x", 1);
        scopes.EnterNew();
        scopes.EnterNew();

        Assert.True(scopes.TryLookup("x", out var value));
        Assert.Equal(1, value);
    }

    [Fact]
    public void AnInnerBindingShadowsAnOuterOne()
    {
        var scopes = new ScopeStack<int>();
        scopes.Declare("x", 1);
        scopes.EnterNew();
        scopes.Declare("x", 2);

        Assert.True(scopes.TryLookup("x", out var value));
        Assert.Equal(2, value);
    }

    [Fact]
    public void ExitingRestoresTheShadowedBinding()
    {
        var scopes = new ScopeStack<int>();
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
        var scopes = new ScopeStack<int>();
        scopes.EnterNew();
        scopes.Declare("x", 1);
        scopes.Exit();

        Assert.False(scopes.TryLookup("x", out _));
    }

    [Fact]
    public void DeclaredHereLooksNoFurtherThanTheInnermostScope()
    {
        var scopes = new ScopeStack<int>();
        scopes.Declare("x", 1);
        scopes.EnterNew();

        // Visible, but shadowing it is not a redeclaration.
        Assert.True(scopes.TryLookup("x", out _));
        Assert.False(scopes.DeclaredHere("x"));
    }

    [Fact]
    public void DeclaredHereSeesANameBoundInTheInnermostScope()
    {
        var scopes = new ScopeStack<int>();
        scopes.EnterNew();
        scopes.Declare("x", 1);

        Assert.True(scopes.DeclaredHere("x"));
    }

    [Fact]
    public void AnUnboundNameIsNotFound()
    {
        Assert.False(new ScopeStack<int>().TryLookup("x", out var value));
        Assert.Equal(0, value);
    }

    [Fact]
    public void TheOutermostScopeCannotBeExited()
    {
        var scopes = new ScopeStack<int>();
        scopes.EnterNew();
        scopes.Exit();

        Assert.Throws<InvalidOperationException>(scopes.Exit);
    }
}
