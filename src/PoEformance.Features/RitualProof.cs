using System.Text;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Features;

/// <summary>What was predicted for one map on the line, and what the game actually rolled.</summary>
/// <param name="Position">Which pick it was. 0 is the start, which is never rolled.</param>
/// <param name="At">Where it is on the atlas.</param>
/// <param name="Rank">Which candidate it was, which is half of what the roll is seeded from.</param>
/// <param name="Predicted">What the model says it should have rolled. Empty when it could not say.</param>
/// <param name="Actual">What the game shows on it. Empty when the text could not be read.</param>
public readonly record struct RitualProofRow(
    int Position, (int X, int Y) At, int Rank, string Predicted, string Actual)
{
    /// <summary>Whether both are known, which is the only case worth a verdict.</summary>
    public bool Comparable => Predicted.Length > 0 && Actual.Length > 0;

    /// <summary>
    /// Whether the prediction holds.
    /// </summary>
    /// <remarks>
    /// CONTAINS rather than equals: the game shows a node's mods as one block of text, so a
    /// two-reward map reads as both lines together, and the predicted first mod is a part of it
    /// rather than the whole. Demanding equality would report every correct two-reward node as
    /// a failure.
    /// </remarks>
    public bool Holds => Comparable && Actual.Contains(Predicted, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Checks the predicted ritual rewards against the ones the game actually rolled.
/// </summary>
/// <remarks>
/// THE POINT OF THIS IS THAT NONE OF THE PREDICTION IS CONFIRMED. The generator matches the
/// reference exactly - that is tested - but whether the reference still matches PoE2, and
/// whether the offsets it reads the line through are right for this client, can only be
/// answered by the game. This is what answers it: draw one ritual line, and every map on it
/// reports what was predicted, what was rolled, and whether they agree.
///
/// Ported in spirit from the reference's roll log, which dumps ground truth for later analysis.
/// Recording predicted and actual SIDE BY SIDE instead makes the answer readable at a glance
/// rather than after a session of offline comparison - which matters, because the person who
/// can run it is not the person who can analyse it.
///
/// Writes nothing until a line has actually rolled something.
/// </remarks>
public sealed class RitualProof
{
    /// <summary>Where the record goes, next to the other settings.</summary>
    public static string DefaultPath { get; } =
        Path.Combine(AppContext.BaseDirectory, "config", "ritual-proof.txt");

    private readonly HashSet<string> _written = [];

    /// <summary>Whether to check at all. Off by default - it is a verification aid, not a feature.</summary>
    public bool Enabled { get; set; }

    /// <summary>How many states have been recorded this session.</summary>
    public int Recorded { get; private set; }

    /// <summary>How many maps agreed, and how many did not, across everything recorded.</summary>
    public (int Held, int Differed) Tally { get; private set; }

    /// <summary>The last thing it had to say, for showing in a window.</summary>
    public string Last { get; private set; } = string.Empty;

    /// <summary>
    /// Works out what should have been rolled for each map on the line, in order.
    /// </summary>
    /// <remarks>
    /// The map at position i was rolled when i maps were committed, and its rank is its place
    /// among the candidates of the one before it - with the maps already taken removed and the
    /// rest in position order. That is the same rule the planner walks forward with, run
    /// backwards over a line that has already been drawn.
    ///
    /// The FIRST map is never rolled: taking it only starts the line.
    /// </remarks>
    public static IReadOnlyList<RitualProofRow> Expected(
        RitualLineState line,
        IReadOnlyList<RitualMod> pool,
        IReadOnlyList<string> actual)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(actual);

        var rows = new List<RitualProofRow>();
        var taken = new HashSet<(int X, int Y)>();

        for (int i = 0; i < line.Committed.Count; i++)
        {
            (int X, int Y) at = line.Committed[i];
            string was = i < actual.Count ? actual[i] : string.Empty;

            if (i == 0)
            {
                taken.Add(at);
                rows.Add(new RitualProofRow(0, at, -1, string.Empty, was));
                continue;
            }

            (int X, int Y) from = line.Committed[i - 1];
            int rank = -1;
            if (line.Candidates.TryGetValue(from, out IReadOnlyList<(int X, int Y)>? next))
            {
                List<(int X, int Y)> ranked =
                    [.. next.Where(one => !taken.Contains(one)).OrderBy(one => one.X).ThenBy(one => one.Y)];
                rank = ranked.IndexOf(at);
            }

            string predicted = string.Empty;
            if (rank >= 0 && pool.Count > 0)
            {
                (RitualMod? first, RitualMod? second) = RitualMods.Rewards(
                    line.LineId, (uint)taken.Count, (uint)rank, pool, line.SecondRewardChance);

                if (first is { } one)
                {
                    predicted = second is { } two ? $"{one.Text} | {two.Text}" : one.Text;
                }
            }

            taken.Add(at);
            rows.Add(new RitualProofRow(i, at, rank, predicted, was));
        }

        return rows;
    }

    /// <summary>
    /// Records one state of the line, if it is new and has anything to say.
    /// </summary>
    /// <remarks>
    /// Deduplicated by the whole state, because this is called every tick while a line is on
    /// screen and the same picture would otherwise fill the file in a minute.
    /// </remarks>
    public void Look(
        RitualLineState line,
        IReadOnlyList<RitualMod> pool,
        IReadOnlyList<string> actual,
        string? path = null)
    {
        if (!Enabled || line is null || actual is null)
        {
            return;
        }

        IReadOnlyList<RitualProofRow> rows = Expected(line, pool, actual);
        if (!rows.Any(row => row.Comparable))
        {
            return;
        }

        var signature = new StringBuilder();
        signature.Append(line.LineId);
        foreach (RitualProofRow row in rows)
        {
            signature.Append(';').Append(row.At.X).Append(',').Append(row.At.Y).Append('=').Append(row.Actual);
        }

        if (!_written.Add(signature.ToString()))
        {
            return;
        }

        int held = rows.Count(row => row.Holds);
        int differed = rows.Count(row => row.Comparable && !row.Holds);
        Recorded++;
        Tally = (Tally.Held + held, Tally.Differed + differed);
        Last = differed == 0
            ? $"{held} of {held} agreed"
            : $"{held} agreed, {differed} DID NOT - the model does not match this client";

        Write(rows, line, path ?? DefaultPath);
    }

    /// <summary>Writes one state, as something a person can read.</summary>
    /// <remarks>
    /// Never throws: this is a diagnostic, and a full disk must not take the overlay with it.
    /// </remarks>
    private static void Write(IReadOnlyList<RitualProofRow> rows, RitualLineState line, string path)
    {
        try
        {
            var said = new StringBuilder();
            said.Append("line ").Append(line.LineId)
                .Append(", ").Append(line.Committed.Count).AppendLine(" maps committed");

            foreach (RitualProofRow row in rows)
            {
                said.Append("  #").Append(row.Position)
                    .Append(" at ").Append(row.At.X).Append(',').Append(row.At.Y)
                    .Append("  rank ").Append(row.Rank < 0 ? "-" : row.Rank.ToString());

                if (row.Position == 0)
                {
                    said.AppendLine("   (the start - never rolled)");
                    continue;
                }

                said.AppendLine()
                    .Append("      predicted: ").AppendLine(row.Predicted.Length > 0 ? row.Predicted : "(nothing)")
                    .Append("      actual:    ").AppendLine(row.Actual.Length > 0 ? row.Actual : "(unreadable)");

                if (row.Comparable)
                {
                    said.Append("      ").AppendLine(row.Holds ? "AGREES" : "DIFFERS <<<");
                }
            }

            said.AppendLine();

            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(path, said.ToString());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Nothing to do about it, and nothing worth breaking a frame over.
        }
    }
}
