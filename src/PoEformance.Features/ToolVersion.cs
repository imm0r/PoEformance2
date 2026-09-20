namespace PoEformance.Features;

/// <summary>
/// What this build of the tool is called, for the corner of the overlay.
/// </summary>
/// <remarks>
/// WHY A NUMBER AT ALL, WITH <see cref="BuildStamp"/> ALREADY HERE. The stamp answers "which
/// commit is this" exactly and is written by the publish workflow; what it cannot do is be
/// spoken. Asked from the live client whether a fix was in the build that was running, the only
/// way to find out was to compare a picture against a merge time - and the answer that time was
/// "no, and the two of us spent ten minutes finding that out". A number on the title bar is
/// what makes "I have 0.1.4" a sentence, and the commit beside it is what makes it checkable.
///
/// IT IS RAISED ON EVERY PUSH, by hand, and that is deliberate rather than lazy: a number the
/// build system invents moves when nothing was released, and a number nobody maintains stops
/// meaning anything. One edit per push, in one file - see the rule in CLAUDE.md.
///
/// THE COMMIT STILL DECIDES where the two disagree. A version says what was INTENDED to be in
/// a build; the stamp says what actually was. A local build has no stamp and says so.
/// </remarks>
public static class ToolVersion
{
    /// <summary>
    /// This build's number. Raised on every push - see the remarks.
    /// </summary>
    /// <remarks>
    /// STARTED AT 0.1.0 ON A TOOL THAT IS FAR PAST ITS FIRST DAY, and the fresh count is the
    /// honest option: the four hundred merges before this one were never numbered, so any
    /// number claiming to cover them would be invented. What the number has to do is tell two
    /// builds apart from today onwards, and it starts counting where it starts being kept.
    /// </remarks>
    public const string Number = "0.1.1";

    /// <summary>The version with a "v" on it, as it reads on the title bar.</summary>
    public static string Said => "v" + Number;

    /// <summary>
    /// The version and what the build really is: the commit, or that it was built locally.
    /// </summary>
    /// <remarks>
    /// BOTH, BECAUSE THEY FAIL DIFFERENTLY. The number is what somebody reads out; the commit
    /// is what settles an argument about whether a particular change is in. A local build has
    /// no stamp, and saying "local" is the one answer that stops the version number being
    /// mistaken for a released one.
    /// </remarks>
    public static string With(BuildStamp? stamp)
        => stamp is { Known: true } known ? $"{Said} · {known.ShortCommit}" : $"{Said} · local";
}
