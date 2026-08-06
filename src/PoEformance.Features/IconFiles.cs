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

        string problem = $"{Path.GetFileName(path)}: {reason}";
        if (!_problems.Contains(problem, StringComparer.Ordinal))
        {
            _problems.Add(problem);
        }
    }

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
