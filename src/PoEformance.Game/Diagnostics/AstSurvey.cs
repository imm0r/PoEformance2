using System.Globalization;
using PoEformance.Game.Entities;
using PoEformance.Game.Files;

namespace PoEformance.Game.Diagnostics;

/// <summary>One <c>.ast</c> file, read, and how a monster got to it.</summary>
/// <param name="Path">The file, as the <c>.ao</c> that named it spelled it.</param>
/// <param name="Skeleton">What was read.</param>
/// <param name="Named">The struct and key that pointed at it, e.g. <c>ClientAnimationController.skeleton</c>.</param>
public sealed record AstRead(string Path, AnimationSkeleton Skeleton, string Named);

/// <summary>
/// How a set of per-file counts is spread: the smallest, the middle one, the largest.
/// </summary>
/// <remarks>
/// THE MEDIAN AND NOT THE MEAN, because the question this answers is "what does a typical rig look
/// like" and one boss with six hundred animations moves a mean and not a median.
/// </remarks>
/// <param name="Least">The smallest count seen.</param>
/// <param name="Middle">The median.</param>
/// <param name="Most">The largest.</param>
/// <param name="Total">Every count added up.</param>
/// <param name="Files">How many counts there were.</param>
public readonly record struct AstSpread(int Least, int Middle, int Most, long Total, int Files)
{
    /// <summary>Takes the spread of a list of counts. The list is sorted in place.</summary>
    public static AstSpread Of(List<int>? counts)
    {
        if (counts is not { Count: > 0 })
        {
            return default;
        }

        counts.Sort();

        long total = 0;
        foreach (int one in counts)
        {
            total += one;
        }

        return new AstSpread(counts[0], counts[counts.Count / 2], counts[^1], total, counts.Count);
    }

    /// <summary>Reads as "12 least, 49 middle, 118 most (9114 over 186 files)".</summary>
    public string Say
        => Files == 0
            ? "none"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{Least} least, {Middle} middle, {Most} most ({Total} over {Files} files)");
}

/// <summary>
/// What the monsters' skeleton files hold, counted over a real install.
/// </summary>
/// <param name="Monsters">How many monsters the <c>.ao</c> walk looked at.</param>
/// <param name="Files">How many <c>.ao</c> files that walk read.</param>
/// <param name="Naming">How many of those named at least one <c>.ast</c>.</param>
/// <param name="Asked">How many distinct <c>.ast</c> files were asked for.</param>
/// <param name="Read">How many of those the install had and this reader walked.</param>
/// <param name="Tiled">Of those, how many tile their track region exactly - see <see cref="AstSurvey.Tiling"/>.</param>
/// <param name="Named">Struct and key that named a skeleton, to how many times.</param>
/// <param name="Versions">File version, to how many files carried it.</param>
/// <param name="Rates">Framerate, to how many animations run at it.</param>
/// <param name="Kinds">The unexplained kind byte, to how many animations carry it.</param>
/// <param name="Animations">Animation name, to how many times it appeared.</param>
/// <param name="Bones">How many bones a rig has.</param>
/// <param name="Hung">How many animations a rig carries.</param>
/// <param name="Keyframes">What every rig's keyframes come to unpacked, in bytes.</param>
/// <param name="Faults">Files that did not read or did not add up, with what was wrong.</param>
/// <param name="CutShort">Whether a cap stopped the walk - see <see cref="AstSurvey.MostSkeletons"/>.</param>
public sealed record AstSurveyResult(
    int Monsters,
    int Files,
    int Naming,
    int Asked,
    int Read,
    int Tiled,
    IReadOnlyDictionary<string, int> Named,
    IReadOnlyDictionary<string, int> Versions,
    IReadOnlyDictionary<string, int> Rates,
    IReadOnlyDictionary<string, int> Kinds,
    IReadOnlyDictionary<string, int> Animations,
    AstSpread Bones,
    AstSpread Hung,
    long Keyframes,
    IReadOnlyList<string> Faults,
    bool CutShort = false)
{
    /// <summary>Nothing walked - no install, or no monster reaches a skeleton.</summary>
    public static AstSurveyResult Nothing { get; }
        = new(0, 0, 0, 0, 0, 0, new Dictionary<string, int>(), new Dictionary<string, int>(),
            new Dictionary<string, int>(), new Dictionary<string, int>(), new Dictionary<string, int>(),
            default, default, 0, []);
}

/// <summary>
/// Reads every skeleton the monsters' <c>.ao</c> files name, and reports what is in them.
/// </summary>
/// <remarks>
/// WRITTEN TO MEASURE, NOT TO DECIDE, exactly as <see cref="AoSurvey"/> was and for the same
/// reason: a moving monster in the Monster Book is a week of work if the files support it and a
/// dead end if they do not, and which of those it is can be settled in one run on a machine that
/// has the game. The three questions:
///
///   1. DOES THE CHAIN EXIST. One real file settled that a monster's .ao carries
///      <c>ClientAnimationController { skeleton = "Art/Models/…/rig.ast" }</c>. One file is a
///      sample of one. This counts which struct and key name a skeleton over the whole table, so
///      a reader written against that one file is not written against an exception.
///   2. DOES THE READER HOLD. AnimationSkeleton was measured against ONE rig, at version 12, and
///      its gates for versions 8, 10 and 11 are a published diagram's word rather than a
///      measurement. The version table here says whether anything but 12 is even shipped, and the
///      fault count says whether the layout survives contact with the rest of the install.
///   3. IS THE TRACK REGION WHAT IT LOOKS LIKE. See <see cref="Tiling"/>. This is the one number
///      to read first, because playback lives or dies on it.
///
/// IT REUSES THE .ao WALK RATHER THAN REPEATING IT. Two breadth-first walks over the same graph
/// with the same extends-and-attachments rules is two things to keep in step, and the second one
/// drifts. This hands AoSurvey a callback and tallies what goes past, holding nothing.
/// </remarks>
public static class AstSurvey
{
    /// <summary>What a skeleton file is called.</summary>
    public const string Suffix = ".ast";

    private static readonly string[] Units = ["KB", "MB", "GB", "TB"];

    /// <summary>How many faults are kept. Past this the count is what matters, not the text.</summary>
    public const int MostFaults = 40;

    /// <summary>
    /// How many distinct skeletons are read before the walk stops.
    /// </summary>
    /// <remarks>
    /// A CAP ON MEGABYTES RATHER THAN ON FILES. There is no ranged read of a file in a bundle, so
    /// each skeleton costs its whole self decompressed - and one monster rig is six and a half
    /// megabytes of keyframes for a header this only wants the first fourteen kilobytes of. A few
    /// hundred rigs is a minute and a gigabyte through the collector; a pathological install
    /// should not be an afternoon.
    /// </remarks>
    public const int MostSkeletons = 2_000;

    /// <summary>
    /// Reads every skeleton the given monsters reach, through their <c>.ao</c> files.
    /// </summary>
    /// <param name="files">The open install. Null gives <see cref="AstSurveyResult.Nothing"/>.</param>
    /// <param name="table">The monster table - the install's own, since the export has no AOFiles.</param>
    /// <param name="match">Only monsters whose path or name contains this, or null for all of them.</param>
    /// <param name="keep">
    /// Skeletons whose full read is kept for printing, or null to keep none. EACH ONE HOLDS ITS
    /// FILE: unpacking an animation later reads back through those bytes, so a kept skeleton is
    /// its whole megabytes. Pass a list for the one monster a detail dump prints, never for the
    /// install.
    /// </param>
    public static AstSurveyResult Read(
        GameFiles? files,
        MonsterVarieties? table,
        string? match = null,
        List<AstRead>? keep = null)
    {
        if (files is null || table is null || table.Count == 0)
        {
            return AstSurveyResult.Nothing;
        }

        var named = new Dictionary<string, int>(StringComparer.Ordinal);
        var wanted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var naming = 0;

        AoSurveyResult walk = AoSurvey.Read(
            files,
            table,
            match,
            keep: null,
            each: one =>
            {
                var any = false;
                foreach ((AoStruct block, AoEntry entry) in one.Object.Entries())
                {
                    foreach (string reference in AoSurvey.Referenced(entry))
                    {
                        if (!string.Equals(AoSurvey.Extension(reference), Suffix, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        string key = $"{block.Name}.{entry.Key}";
                        Note(named, key);

                        // FIRST NAMER WINS, and which one it was barely matters - the count above
                        // is the answer to "how is a skeleton named". This is only so a detail
                        // dump can say how the file was arrived at.
                        wanted.TryAdd(AoSurvey.Bare(reference), key);
                        any = true;
                    }
                }

                if (any)
                {
                    naming++;
                }
            });

        var versions = new Dictionary<string, int>(StringComparer.Ordinal);
        var rates = new Dictionary<string, int>(StringComparer.Ordinal);
        var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
        var animations = new Dictionary<string, int>(StringComparer.Ordinal);
        var bones = new List<int>();
        var hung = new List<int>();
        var faults = new List<string>();

        long keyframes = 0;
        var asked = 0;
        var read = 0;
        var tiled = 0;

        foreach ((string path, string key) in wanted)
        {
            if (asked >= MostSkeletons)
            {
                break;
            }

            asked++;
            AnimationSkeleton one = AnimationSkeleton.Read(files, path);
            if (!one.Ready)
            {
                if (faults.Count < MostFaults)
                {
                    faults.Add($"{path}: {(one.Why.Length > 0 ? one.Why : "the install has no such file")}");
                }

                continue;
            }

            read++;
            keep?.Add(new AstRead(path, one, key));

            Note(versions, Say(one.Version));
            bones.Add(one.Bones.Count);
            hung.Add(one.Animations.Count);
            keyframes += one.TrackBytes;

            string wrong = Tiling(one);
            if (wrong.Length == 0)
            {
                tiled++;
            }
            else if (faults.Count < MostFaults)
            {
                faults.Add($"{path}: {wrong}");
            }

            foreach (SkeletonAnimation move in one.Animations)
            {
                Note(rates, Say(move.Rate));
                Note(kinds, "0x" + move.Kind.ToString("x2", CultureInfo.InvariantCulture));
                Note(animations, move.Name);
            }
        }

        return new AstSurveyResult(
            walk.Monsters, walk.Read, naming, asked, read, tiled,
            named, versions, rates, kinds, animations,
            AstSpread.Of(bones), AstSpread.Of(hung), keyframes, faults,
            CutShort: walk.CutShort || asked >= MostSkeletons);
    }

    /// <summary>
    /// Whether an animation's frames tile the track region exactly, or what is wrong with them.
    /// </summary>
    /// <remarks>
    /// THE CHECK THE WHOLE FORMAT TURNS ON, and the one the file itself settles - which is the
    /// difference between this and a check that a wrong reader passes. Every animation header
    /// carries an offset and a length into the UNPACKED track region; on the rig this was measured
    /// against they chained end to end, 0 then 42117 then 92686, and the last one's end was exactly
    /// the size the embedded bundle's own header claims. Nothing in the reader arranges that. A
    /// header layout off by one byte scatters the offsets into nonsense and this says so.
    ///
    /// IT IS ALSO WHAT MAKES PLAYBACK CHEAP. A region that tiles means an animation's keyframes are
    /// a CONTIGUOUS range, so playing a walk cycle unpacks the bundle chunks that range falls in
    /// and no others - forty kilobytes out of twelve megabytes. A region that did not tile would
    /// mean unpacking the lot.
    /// </remarks>
    /// <returns>Empty where it tiles, otherwise what did not add up.</returns>
    public static string Tiling(AnimationSkeleton? one)
    {
        if (one is not { Ready: true })
        {
            return "nothing read";
        }

        var cursor = 0;
        foreach (SkeletonAnimation move in one.Animations)
        {
            if (move.At != cursor)
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"{Called(move.Name)} starts at {move.At}, where the one before it ended at {cursor}");
            }

            cursor += move.Length;
        }

        return cursor == one.TrackBytes
            ? string.Empty
            : string.Create(
                CultureInfo.InvariantCulture,
                $"the headers cover {cursor} bytes and the bundle holds {one.TrackBytes}");
    }

    /// <summary>Prints what the survey found, most common first.</summary>
    public static void Report(AstSurveyResult? result, TextWriter output, int most = 25)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (result is null || result.Asked == 0)
        {
            output.WriteLine("ast  nothing to read - no install, or no monster names a skeleton.");
            return;
        }

        output.WriteLine(
            $"ast  {Say(result.Monsters)} monsters walked; {Say(result.Files)} .ao files read,"
            + $" {Say(result.Naming)} of them naming a skeleton");
        output.WriteLine(
            $"     {Say(result.Asked)} distinct .ast files asked for, {Say(result.Read)} read");

        if (result.CutShort)
        {
            output.WriteLine(
                "     STOPPED EARLY - a cap was reached, so the counts below are a sample of the"
                + " install rather than all of it.");
        }

        // FIRST AND ON ITS OWN LINE, because it is the finding. Everything else here describes
        // what the files hold; this says whether the reader is holding them the right way up, and
        // a run where it is not makes the rest of the numbers a description of a bug.
        if (result.Read > 0)
        {
            output.WriteLine(
                result.Tiled == result.Read
                    ? $"     ALL {Say(result.Read)} TILE THEIR TRACK REGION EXACTLY - every animation's"
                        + " frames are a contiguous range, and the headers account for the bundle exactly."
                    : $"     {Say(result.Tiled)} of {Say(result.Read)} tile their track region exactly."
                        + " The rest are in the faults below, and are the thing to look at.");
        }

        if (result.Faults.Count > 0)
        {
            output.WriteLine($"     {Say(result.Faults.Count)} files did not read or did not add up. First few:");
            foreach (string fault in result.Faults.Take(8))
            {
                output.WriteLine($"       ! {fault}");
            }
        }

        if (result.Bones.Files > 0)
        {
            output.WriteLine($"     bones       {result.Bones.Say}");
            output.WriteLine($"     animations  {result.Hung.Say}");
            output.WriteLine($"     keyframes   {Bytes(result.Keyframes)} unpacked over {Say(result.Read)} files");
        }

        Table(output, "how a skeleton is named", result.Named, most);
        Table(output, "file versions", result.Versions, most);
        Table(output, "framerates", result.Rates, most);
        Table(output, "the unexplained kind byte", result.Kinds, most);
        Table(output, "animation names", result.Animations, most);
    }

    /// <summary>Prints one monster's skeletons in full - every bone and every animation.</summary>
    public static void Detail(
        IReadOnlyList<AstRead>? reads, TextWriter output, int mostAnimations = 80)
    {
        ArgumentNullException.ThrowIfNull(output);

        foreach (AstRead one in reads ?? [])
        {
            AnimationSkeleton skeleton = one.Skeleton;
            string wrong = Tiling(skeleton);

            output.WriteLine();
            output.WriteLine(
                $"  {one.Path}   (version {Say(skeleton.Version)}, {Say(skeleton.Bones.Count)} bones,"
                + $" {Say(skeleton.Animations.Count)} animations, {Say(skeleton.Lights)} lights)");
            output.WriteLine($"    named by {one.Named}");
            output.WriteLine(
                $"    keyframes at byte {Say(skeleton.TracksAt)}, {Bytes(skeleton.TrackBytes)} unpacked"
                + $" - {(wrong.Length == 0 ? "tiles exactly" : wrong)}");

            output.WriteLine("    bones:");
            foreach (SkeletonBone bone in skeleton.Bones)
            {
                output.WriteLine(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"      {bone.Name,-28} sibling {bone.Sibling,4}  child {bone.Child,4}"
                        + $"  at ({bone.Bind.M41:F2}, {bone.Bind.M42:F2}, {bone.Bind.M43:F2})"));
            }

            output.WriteLine("    animations:");
            var printed = 0;
            foreach (SkeletonAnimation move in skeleton.Animations)
            {
                if (printed++ >= mostAnimations)
                {
                    output.WriteLine($"      … and {Say(skeleton.Animations.Count - mostAnimations)} more");
                    break;
                }

                string blend = move.Parent.Length > 0 ? $"  from {move.Parent}" : string.Empty;
                output.WriteLine(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"      {move.Name,-40} {move.Rate,3} fps  {move.Tracks,3} tracks"
                        + $"  kind 0x{move.Kind:x2}  at {move.At,9} for {move.Length,8}{blend}"));
            }
        }
    }

    /// <summary>A name to print, which on a drifted walk may be empty or rubbish.</summary>
    private static string Called(string name) => name.Length > 0 ? name : "an unnamed animation";

    private static void Note(Dictionary<string, int> into, string what)
    {
        if (what.Length == 0)
        {
            return;
        }

        into[what] = into.TryGetValue(what, out int had) ? had + 1 : 1;
    }

    private static void Table(
        TextWriter output, string what, IReadOnlyDictionary<string, int> counts, int most)
    {
        if (counts.Count == 0)
        {
            return;
        }

        output.WriteLine($"     {what} ({Say(counts.Count)} distinct):");
        foreach ((string name, int on) in counts
            .OrderByDescending(one => one.Value)
            .ThenBy(one => one.Key, StringComparer.Ordinal)
            .Take(most))
        {
            output.WriteLine($"       {on,6} {name}");
        }

        if (counts.Count > most)
        {
            output.WriteLine($"       … and {Say(counts.Count - most)} more");
        }
    }

    /// <summary>A byte count a person can read at a glance.</summary>
    public static string Bytes(long count)
    {
        if (count < 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{count} B");
        }

        double size = count;
        foreach (string unit in Units)
        {
            size /= 1024d;
            if (size < 1024d || string.Equals(unit, Units[^1], StringComparison.Ordinal))
            {
                return string.Create(CultureInfo.InvariantCulture, $"{size:F1} {unit}");
            }
        }

        return string.Create(CultureInfo.InvariantCulture, $"{count} B");
    }

    private static string Say(int value) => value.ToString(CultureInfo.InvariantCulture);
}
