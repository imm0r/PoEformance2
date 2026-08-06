using PoEformance.Features;

namespace PoEformance.Core.Tests;

/// <summary>
/// A custom icon is the one setting that can stop working after it was saved.
/// </summary>
/// <remarks>
/// Every other choice is a number or a colour and is as valid tomorrow as today. An icon is a
/// path, and paths point at files that get moved, renamed, deleted, or replaced with something
/// that is not a picture - so what happens THEN is the part worth pinning down.
/// </remarks>
public class IconFileTests
{
    private static IconFiles Under(string root) => new() { Root = root };

    [Fact]
    public void NoIconMeansNothingToLoad()
    {
        var files = Under("/tools");

        Assert.Equal(string.Empty, files.NextToTry(null));
        Assert.Equal(string.Empty, files.NextToTry(""));
        Assert.Equal(string.Empty, files.NextToTry("   "));
    }

    [Fact]
    public void ARelativePathIsLookedForBesideTheTool()
    {
        // Where somebody would put a file they made for this. Resolved against the tool
        // rather than the WORKING directory, which is whatever launched it - a shortcut, an
        // explorer window, a debugger - and is not a place anybody keeps their icons.
        Assert.Equal(
            Path.Combine("/tools", "shrine.png"),
            Under("/tools").NextToTry("shrine.png"));
    }

    [Fact]
    public void AFullPathIsTakenAsGiven()
        => Assert.Equal(
            Path.Combine(Path.GetTempPath(), "somewhere-else.png"),
            Under("/tools").NextToTry(Path.Combine(Path.GetTempPath(), "somewhere-else.png")));

    [Fact]
    public void SurroundingSpaceIsIgnored()
    {
        // Paths get pasted, and a pasted path brings a space or a newline with it.
        Assert.Equal(Path.Combine("/tools", "shrine.png"), Under("/tools").NextToTry("  shrine.png  "));
    }

    [Fact]
    public void APathThatFailedIsNotReachedForAgain()
    {
        // The one that costs something invisibly. The render thread asks sixty times a
        // second, so a missing file is sixty disk hits a second to learn what it already
        // knows - and the answer cannot change until the path does.
        var files = Under("/tools");
        Assert.NotEqual(string.Empty, files.NextToTry("gone.png"));

        files.Failed("gone.png", "not found");

        Assert.Equal(string.Empty, files.NextToTry("gone.png"));
        Assert.True(files.GaveUpOn("gone.png"));
    }

    [Fact]
    public void GivingUpOnOneDoesNotGiveUpOnTheRest()
    {
        var files = Under("/tools");
        files.Failed("gone.png", "not found");

        Assert.NotEqual(string.Empty, files.NextToTry("there.png"));
    }

    [Fact]
    public void WhatWentWrongIsSaidRatherThanSwallowed()
    {
        // Without this the setting looks like it does nothing: a bad path draws the ordinary
        // shape, which is exactly what having no icon looks like. Nobody suspects the file.
        var files = Under("/tools");
        files.Failed("broken.png", "not a known image format");

        Assert.Contains(files.Problems, problem =>
            problem.Contains("broken.png", StringComparison.Ordinal)
            && problem.Contains("not a known image format", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSameProblemIsSaidOnce()
    {
        // Sixty a second otherwise, and a list that scrolls is a list nobody reads.
        var files = Under("/tools");
        files.Failed("gone.png", "not found");
        files.Failed("gone.png", "not found");

        Assert.Single(files.Problems);
    }

    [Fact]
    public void AskingAgainIsPossibleAfterFixingIt()
    {
        // Which is what lets a failure be remembered for good: there IS a way to retry, it is
        // just not on every frame.
        var files = Under("/tools");
        files.Failed("gone.png", "not found");
        files.Forget();

        Assert.False(files.GaveUpOn("gone.png"));
        Assert.NotEqual(string.Empty, files.NextToTry("gone.png"));
        Assert.Empty(files.Problems);
    }
}
