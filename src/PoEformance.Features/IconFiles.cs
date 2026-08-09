namespace PoEformance.Features;

/// <summary>
/// Where a chosen icon's file is, and whether it is worth trying again.
/// </summary>
/// <remarks>
/// The policy half of custom icons, kept apart from the decoding half because the two fail
/// for different reasons and only this one has decisions in it. Loading a picture is a
/// library call; deciding WHICH file a path means and whether to reach for the disk again is
/// where a mistake costs something.
///
/// AN ICON IS THE ONE SETTING THAT CAN STOP WORKING AFTER IT WAS SAVED. Every other choice is
/// a number or a colour and is as valid tomorrow as today; a path points at a file that can be
/// moved, renamed, deleted, or replaced with something that is not a picture - and eventually
/// it will be, because the person who set it will tidy that folder.
///
/// So a failure is remembered rather than retried. The render thread asks sixty times a
/// second, and a missing file looked for sixty times a second is a disk hammered to learn
/// nothing: the answer cannot change until the path does, or until somebody asks for a fresh
/// look.
/// </remarks>
public sealed class IconFiles
{
    private readonly HashSet<string> _givenUp = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _problems = [];

    /// <summary>Where a relative path is looked for. Beside the tool, by default.</summary>
    public string Root { get; set; } = AppContext.BaseDirectory;

    /// <summary>
    /// What went wrong, for whoever set a path that does not work.
    /// </summary>
    /// <remarks>
    /// Reported rather than only logged, because the symptom of a bad path is a marker drawn
    /// its ordinary way - which is exactly what NOT setting an icon looks like. Without this
    /// the setting appears to do nothing at all, and the file that moved is never suspected.
    /// </remarks>
    public IReadOnlyList<string> Problems => _problems;

    /// <summary>
    /// The file to load for a path, or empty when there is nothing to try.
    /// </summary>
    /// <remarks>
    /// Empty covers "no icon chosen" and "this one already failed" alike, because the caller
    /// does the same thing for both: draw the built-in shape.
    /// </remarks>
    public string NextToTry(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || _givenUp.Contains(path))
        {
            return string.Empty;
        }

        string trimmed = path.Trim();
        return Path.IsPathRooted(trimmed) ? trimmed : Path.Combine(Root, trimmed);
    }

    /// <summary>Records that a path did not work, so it is not reached for again.</summary>
    public void Failed(string path, string reason)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(reason);

        _givenUp.Add(path);
        Noted(path, reason);
    }

    /// <summary>
    /// Records that a path could not be read THIS TIME, without giving up on it.
    /// </summary>
    /// <remarks>
    /// THE ONE ANSWER THAT CHANGES BY ITSELF, which is why it is not a verdict. Everything else
    /// this class remembers is settled - a file that is not there, or is not a picture, will
    /// say the same tomorrow - and remembering it is what stops the render thread asking sixty
    /// times a second.
    ///
    /// A file that is open in something else is different in kind: it is being written, right
    /// now, most often by this very tool fetching the icon that is about to be drawn. Treated
    /// as a verdict, the picture that lost that race by a millisecond is gone until the tool is
    /// restarted - and it is the pictures being fetched while the stash is first drawn that
    /// lose it, which on a first run is all of them.
    ///
    /// So it is reported and not remembered. The next look is a frame away.
    /// </remarks>
    public void Busy(string path, string reason)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(reason);

        Noted(path, reason);
    }

    /// <summary>Says a path worked, which retires whatever was last said about it.</summary>
    /// <remarks>
    /// Otherwise a busy moment leaves a complaint on screen about a picture that is now being
    /// drawn perfectly well, and the list only ever grows.
    /// </remarks>
    public void Worked(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        _problems.RemoveAll(problem => problem.StartsWith(Named(path), StringComparison.Ordinal));
    }

    /// <summary>
    /// Whether a read failed because somebody has the file open, rather than because it cannot
    /// be read at all.
    /// </summary>
    /// <remarks>
    /// THE DECISION THIS CLASS EXISTS FOR, in its smallest form: a verdict, or a moment.
    ///
    /// Windows reports both as an IOException and words this one as "used by another process",
    /// which misleads on its own account - here it is usually the SAME process, writing the
    /// icon that is about to be drawn. The codes are what tell them apart: 0x20 is a sharing
    /// violation and 0x21 a lock one. Everything else keeps the settled answer, which is to
    /// give up on the path rather than ask the disk sixty times a second.
    /// </remarks>
    public static bool Momentary(IOException failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        // The low word of the HRESULT is the Win32 code; the rest says it came from Win32.
        return (failure.HResult & 0xFFFF) is 0x20 or 0x21;
    }

    private void Noted(string path, string reason)
    {
        string problem = Named(path) + reason;
        if (!_problems.Contains(problem, StringComparer.Ordinal))
        {
            _problems.Add(problem);
        }
    }

    private static string Named(string path) => $"{Path.GetFileName(path)}: ";

    /// <summary>Whether a path has been given up on.</summary>
    public bool GaveUpOn(string path) => _givenUp.Contains(path);

    /// <summary>
    /// Forgets every verdict, so replaced files are picked up.
    /// </summary>
    /// <remarks>
    /// The way to ask again after fixing a path, and the reason a failure can be remembered
    /// for good: there IS a way to retry, it is just not sixty times a second.
    /// </remarks>
    public void Forget()
    {
        _givenUp.Clear();
        _problems.Clear();
    }
}
