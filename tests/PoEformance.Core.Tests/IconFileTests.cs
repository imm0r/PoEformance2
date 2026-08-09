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
    public void AFileSomebodyElseHasOpenIsAMomentRatherThanAVerdict()
    {
        // The one answer that changes by itself, and the reason it must not be remembered:
        // for the item art the "somebody else" is this very tool, writing the icon that is
        // about to be drawn. Remembered as a failure, a picture that lost that race by a
        // millisecond is gone until the next run - and on a first look at a stash, every
        // picture is being fetched while it is first drawn.
        var files = Under("/tools");
        files.Busy("icon.png", "used by another process");

        Assert.False(files.GaveUpOn("icon.png"));
        Assert.NotEqual(string.Empty, files.NextToTry("icon.png"));

        // Still said out loud, because a file that is stuck open forever shows no picture and
        // no reason either.
        Assert.Contains(files.Problems, problem => problem.Contains("icon.png", StringComparison.Ordinal));

        // And retired the moment it works, or the complaint outlives the problem.
        files.Worked("icon.png");
        Assert.Empty(files.Problems);
    }

    [Theory]
    [InlineData(unchecked((int)0x80070020), true)]    // sharing violation - being written now
    [InlineData(unchecked((int)0x80070021), true)]    // lock violation
    [InlineData(unchecked((int)0x80070002), false)]   // file not found - settled
    [InlineData(unchecked((int)0x80070005), false)]   // access denied - settled
    [InlineData(0, false)]
    public void ANDWhichKindItIsComesFromTheCodeRatherThanTheWording(int hresult, bool momentary)
    {
        // Windows words a sharing violation as "used by another process", which is both the
        // most common case here and the most misleading - it is usually the same process.
        // The wording is not the test; the code is.
        Assert.Equal(momentary, IconFiles.Momentary(new IOException("whatever it says") { HResult = hresult }));
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
