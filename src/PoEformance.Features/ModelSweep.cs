using System.Globalization;
using System.Numerics;
using System.Text;
using PoEformance.Game.Entities;

namespace PoEformance.Features;

/// <summary>
/// One line per monster saying what its model is made of and what became of every piece.
/// </summary>
/// <remarks>
/// WHY A SWEEP AT ALL. Every attachment bug this month was found the same way: somebody played,
/// saw a boot on the floor, and asked. That finds the monsters somebody happens to fight, and
/// four of the fixes it produced each broke a monster the other had just fixed - because the
/// case that would have caught it was on a monster nobody had looked at. There are 2733 of them
/// and the files behind them are all on disk; the ones that are wrong are wrong now, whether or
/// not anybody has walked past.
///
/// SO THE WHOLE TABLE IS WALKED AND WHAT COMES OUT IS FACTS, not judgements: per piece its
/// socket, how the socket was answered, how many of its bones found one of its carrier's, how
/// many fell back, what its box ended up being. Those are the numbers that settled Bahlak,
/// Malgor, the Frostborn Fiend, the Fallen Knight, Gulzal, Tycho and Brughor, and every one of
/// them was reconstructed by hand from a text dump of one monster.
///
/// IT IS THE REAL WALK, not one beside it. <see cref="MonsterModels.Of"/> places the pieces and
/// now says what it did - see <see cref="PartFit"/>. A sweep with a walk of its own would be
/// answering about itself, which is the trap the dump fell into when it read four lines of
/// Gulzal's hammer and reported no rig, no pairing and no materials while all three sat in the
/// axe it extends.
///
/// NO PICTURES ARE DECODED. <see cref="Withheld"/> hands the walk a reader that returns nothing
/// for a .dds, so every .ao, .ast, .sm, .smd and .mat is read exactly as it would be and the
/// 2048-square sheets - which are the bulk of a monster and none of this question - are not.
/// That is also why the sweep says nothing about textures: it did not look.
///
/// FLAGS ARE FOR PICKING WHAT TO LOOK AT, and nothing else. Which monsters get a full text dump
/// is a convenience; no flag here decides how anything is drawn, so a threshold in one cannot
/// put a piece in the wrong place. That distinction is the whole reason they are allowed to be
/// rules of thumb at all - see <see cref="Odd"/>.
/// </remarks>
public static class ModelSweep
{
    /// <summary>
    /// Walks every monster the table names and reports what became of each one's pieces.
    /// </summary>
    /// <param name="read">How to get a file out of the install, by path.</param>
    /// <param name="table">The monster table, from the install - the shipped export has no .ao column.</param>
    /// <param name="match">Only monsters whose path or name contains this, or empty for all.</param>
    /// <param name="lines">Called once per monster with its record, ready to be written.</param>
    /// <param name="along">Called every <see cref="Step"/> monsters with how far it has got.</param>
    public static SweepResult Of(
        Func<string, byte[]?>? read,
        MonsterVarieties? table,
        string match,
        Action<string>? lines = null,
        Action<int, int>? along = null)
    {
        if (read is null || table is null || table.Count == 0)
        {
            return SweepResult.Nothing;
        }

        Func<string, byte[]?> counted = Withheld(read);

        // SORTED BY PATH, so two runs of the same install produce the same file and a diff
        // between two builds is the change and nothing else. A dictionary's order is not a
        // promise, and a corpus that reshuffles itself is a corpus nobody can diff.
        List<KeyValuePair<string, MonsterVariety>> wanted =
        [
            .. table.All
                .Where(one => Wanted(one.Key, one.Value, match))
                .OrderBy(one => one.Key, StringComparer.Ordinal),
        ];

        var said = new SweepTally();
        var flagged = new List<string>();
        var at = 0;

        foreach ((string path, MonsterVariety one) in wanted)
        {
            at++;
            if (at % Step == 0)
            {
                along?.Invoke(at, wanted.Count);
            }

            if (one.AoFiles is not { Count: > 0 })
            {
                said.Nameless++;
                continue;
            }

            MonsterModel model = MonsterModels.Of(counted, one);
            string[] odd = [.. Odd(model)];

            said.Walked++;
            said.Pieces += model.Fitted.Count;
            foreach (string flag in odd)
            {
                said.Flags.TryGetValue(flag, out int many);
                said.Flags[flag] = many + 1;
            }

            if (odd.Length > 0)
            {
                said.Odd++;
                flagged.Add(path);
            }

            lines?.Invoke(Line(path, one, model, odd));
        }

        along?.Invoke(wanted.Count, wanted.Count);
        return new SweepResult(said, flagged);
    }

    /// <summary>How often the progress callback is told, in monsters.</summary>
    private const int Step = 25;

    /// <summary>Whether a monster is in what was asked for.</summary>
    private static bool Wanted(string path, MonsterVariety one, string match)
        => match.Length == 0
            || path.Contains(match, StringComparison.OrdinalIgnoreCase)
            || (one.Name ?? string.Empty).Contains(match, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The install's reader with the pictures taken out.
    /// </summary>
    /// <remarks>
    /// THE CHEAPEST HONEST WAY TO SKIP THEM. A flag through the walk would be a second code path
    /// that the game never runs and that could therefore be wrong without anybody noticing; a
    /// reader that answers "not here" for a .dds is the walk itself, unmodified, reading an
    /// install that happens to have no pictures in it. Everything that decides where a piece
    /// goes is in the other five file types and is read exactly as always.
    ///
    /// IT IS ALSO MOST OF THE TIME. Doryani is 32 MB across 15 files and the sheets are nearly
    /// all of that; over 2733 monsters the difference is hours.
    /// </remarks>
    private static Func<string, byte[]?> Withheld(Func<string, byte[]?> read)
        => path => path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) ? null : read(path);

    /// <summary>
    /// What is worth a second look on one monster, as short names a report can count.
    /// </summary>
    /// <remarks>
    /// EVERY ONE OF THESE HAS COST A DAY ALREADY, which is the whole reason they are the list:
    ///
    ///   dropped      a piece whose socket the monster's rig has no bone for - it is left out
    ///   unfitted     a "&lt;root&gt;" piece with a rig and no bone in common, left at the origin;
    ///                Malgor's ship's wheel
    ///   torn         a "&lt;root&gt;" piece PART of whose bones matched nothing, so part of it
    ///                took the monster's root - the ground between his feet - while the rest went
    ///                where it belonged; Bahlak's feather bundle, the beam from his chest to the
    ///                floor. The rig's own root is left out of that count, because it never
    ///                matches by design - see PartFit.Strays.
    ///
    ///                AND ONLY ON A "&lt;root&gt;" PIECE, which the first corpus is what settled.
    ///                It counted socketed pieces too and reported 306 monsters; 434 of the 483
    ///                pieces behind that number were socketed, and on a SOCKETED piece a stray
    ///                goes nowhere: Correcting fills EVERY bone with the socket's own transform,
    ///                matched or not, so the geometry lands in the same place either way and all
    ///                a stray changes is which body bone it animates with - which is the old
    ///                rigid binding, not a fault. The real count is 31 monsters. A flag that
    ///                names ten times too many is a flag nobody reads.
    ///   apart        a piece whose box does not meet the body's at all - the shape of every
    ///                report that began "hier liegt noch etwas auf dem Boden"
    ///   malformed    an attachment_bones line naming a group nobody declares; Tycho's SkirtLayers
    ///   paths        a merged rig writing its bones as a|b|c; Brughor's corpse armour
    ///   deep         a piece hung off a piece, which was fitted to the wrong rig until 0.1.21
    ///   past         a mesh indexed past its own rig
    ///   unread       a manifest or mesh that did not read
    ///   capped       the walk hit its own limit, so the monster is not fully described
    ///
    /// "apart" IS THE ONE RULE OF THUMB HERE and it is allowed to be one because of what it is
    /// for: it picks which monsters get a text dump. A piece can legitimately sit clear of the
    /// body - a thrown weapon, a tether - so this will name some monsters that are fine. Naming
    /// a fine monster costs a file; missing a broken one costs a month of screenshots.
    /// </remarks>
    private static IEnumerable<string> Odd(MonsterModel model)
    {
        if (model.Fitted.Count == 0)
        {
            yield break;
        }

        var dropped = false;
        var unfitted = false;
        var fell = false;
        var apart = false;
        var malformed = false;
        var paths = false;
        var deep = false;
        var past = false;
        var unread = false;

        // THE MONSTER WITHOUT WHAT HE IS WEARING - see MonsterModel.BodyLeast. The JOINED box
        // is the union of the body and every piece, so a piece held up against that one is
        // inside it by construction and this test could never fail. It never did: the first run
        // over 2792 monsters flagged not one, which is what a vacuous check looks like from the
        // outside and is exactly the kind a sweep is supposed to catch.
        (Vector3 least, Vector3 most) = (model.BodyLeast, model.BodyMost);

        foreach (PartFit one in model.Fitted)
        {
            dropped |= one.Place == PartPlace.Dropped;
            unread |= one.Kind == PartKind.Unread;
            malformed |= one.Malformed;
            paths |= one.Paths > 0;
            deep |= one.Depth > 1;
            past |= one.Past;
            // ON A "<root>" PIECE ONLY - see below. 434 of the 483 torn pieces in the first
            // corpus were socketed, where this says nothing at all.
            fell |= one.Kind == PartKind.Skin && one.Place == PartPlace.Root && one.Torn;
            unfitted |= one.Kind == PartKind.Rigid && one.Bones > 1 && one.Matched == 0;
            apart |= one.Kind != PartKind.None && !one.Lost && Apart(one, least, most);
        }

        if (dropped)
        {
            yield return "dropped";
        }

        if (unfitted)
        {
            yield return "unfitted";
        }

        if (fell)
        {
            yield return "torn";
        }

        if (apart)
        {
            yield return "apart";
        }

        if (malformed)
        {
            yield return "malformed";
        }

        if (paths)
        {
            yield return "paths";
        }

        if (deep)
        {
            yield return "deep";
        }

        if (past)
        {
            yield return "past";
        }

        if (unread)
        {
            yield return "unread";
        }

        if (model.Parts >= MostParts)
        {
            yield return "capped";
        }
    }

    /// <summary>What <c>MonsterModels</c> stops at, so a capped walk can say so.</summary>
    private const int MostParts = 48;

    /// <summary>Whether a piece's box does not meet the monster's at all, on any axis.</summary>
    private static bool Apart(PartFit one, Vector3 least, Vector3 most)
        => one.Most.X < least.X || one.Least.X > most.X
            || one.Most.Y < least.Y || one.Least.Y > most.Y
            || one.Most.Z < least.Z || one.Least.Z > most.Z;

    /// <summary>
    /// One monster as one line of JSON - the record the corpus is made of.
    /// </summary>
    /// <remarks>
    /// JSON LINES AND NOT A REPORT, because the point is asking questions nobody has thought of
    /// yet. A report answers the questions its author had; a line per monster with the numbers on
    /// it can be grepped, counted and sorted by whoever comes next, and it stays diffable between
    /// two builds because it is one monster per line in a fixed order.
    ///
    /// WRITTEN BY HAND rather than through a serialiser, because this assembly ships Native AOT
    /// and reflection-based JSON is exactly what that does not do. The shape is small and fixed.
    /// </remarks>
    private static string Line(string path, MonsterVariety one, MonsterModel model, IReadOnlyList<string> odd)
    {
        var said = new StringBuilder(256);
        said.Append("{\"path\":").Append(Quoted(path));
        said.Append(",\"name\":").Append(Quoted(one.Name ?? string.Empty));
        said.Append(",\"ready\":").Append(model.Ready ? "true" : "false");

        if (model.Why.Length > 0)
        {
            said.Append(",\"why\":").Append(Quoted(model.Why));
        }

        said.Append(",\"rig\":").Append(Say(model.Rig is { Ready: true } rig ? rig.Bones.Count : 0));
        said.Append(",\"sections\":").Append(Say(model.Sections));
        said.Append(",\"parts\":").Append(Say(model.Parts));
        said.Append(",\"box\":").Append(Box(model.Mesh.Least, model.Mesh.Most));

        // THE MONSTER WITHOUT WHAT HE WEARS, which is what "apart" is measured against. The
        // joined box above is not: held up against THAT one every piece is inside by
        // construction. The first corpus carried only the joined box, so it could not reproduce
        // its own flag - a record that cannot check itself is the diagnostic reading less than
        // the walk, one layer further out.
        said.Append(",\"body\":").Append(Box(model.BodyLeast, model.BodyMost));

        if (odd.Count > 0)
        {
            said.Append(",\"odd\":[");
            for (var at = 0; at < odd.Count; at++)
            {
                said.Append(at > 0 ? "," : string.Empty).Append(Quoted(odd[at]));
            }

            said.Append(']');
        }

        said.Append(",\"fits\":[");
        for (var at = 0; at < model.Fitted.Count; at++)
        {
            said.Append(at > 0 ? "," : string.Empty).Append(Fit(model.Fitted[at]));
        }

        return said.Append("]}").ToString();
    }

    /// <summary>One piece, as the object inside a monster's <c>fits</c> list.</summary>
    private static string Fit(PartFit one)
    {
        var said = new StringBuilder(160);
        // THE WHOLE PATH, not the file's own name. Two different Cape.ao hang off the Rusted
        // Skeleton Soldier - one under Attachments/ and one under Attachments/Physics/ - and
        // counted by stem they are one file with contradictory answers.
        said.Append("{\"file\":").Append(Quoted(one.File));
        said.Append(",\"socket\":").Append(Quoted(one.Socket.Length > 0 ? one.Socket : "<root>"));
        said.Append(",\"place\":").Append(Quoted(one.Place.ToString()));
        said.Append(",\"kind\":").Append(Quoted(one.Kind.ToString()));
        said.Append(",\"depth\":").Append(Say(one.Depth));
        said.Append(",\"bones\":").Append(Say(one.Bones));
        said.Append(",\"matched\":").Append(Say(one.Matched));

        if (one.Strays > 0)
        {
            said.Append(",\"strays\":").Append(Say(one.Strays));
        }

        if (one.Paired > 0)
        {
            said.Append(",\"paired\":").Append(Say(one.Paired));
        }

        if (one.Malformed)
        {
            said.Append(",\"malformed\":true");
        }

        if (one.Paths > 0)
        {
            said.Append(",\"paths\":").Append(Say(one.Paths));
        }

        if (one.Past)
        {
            said.Append(",\"past\":true");
        }

        if (one.Why.Length > 0)
        {
            said.Append(",\"why\":").Append(Quoted(one.Why));
        }

        if (one.Kind is not (PartKind.None or PartKind.Unread))
        {
            said.Append(",\"box\":").Append(Box(one.Least, one.Most));
        }

        return said.Append('}').ToString();
    }

    /// <summary>A box as four-figure numbers - enough to see where something is, not a checksum.</summary>
    private static string Box(Vector3 least, Vector3 most)
        => $"[{Say(least.X)},{Say(least.Y)},{Say(least.Z)},{Say(most.X)},{Say(most.Y)},{Say(most.Z)}]";

    private static string Say(int said) => said.ToString(CultureInfo.InvariantCulture);

    private static string Say(float said)
        => float.IsFinite(said) ? said.ToString("0.#", CultureInfo.InvariantCulture) : "0";

    /// <summary>A JSON string, with the five things that have to be escaped in one.</summary>
    private static string Quoted(string said)
    {
        var out_ = new StringBuilder(said.Length + 2);
        out_.Append('"');
        foreach (char one in said)
        {
            _ = one switch
            {
                '"' => out_.Append("\\\""),
                '\\' => out_.Append("\\\\"),
                '\n' => out_.Append("\\n"),
                '\r' => out_.Append("\\r"),
                '\t' => out_.Append("\\t"),
                < ' ' => out_.Append(CultureInfo.InvariantCulture, $"\\u{(int)one:x4}"),
                _ => out_.Append(one),
            };
        }

        return out_.Append('"').ToString();
    }
}

/// <summary>What a sweep counted, for the line the console prints.</summary>
public sealed class SweepTally
{
    /// <summary>Monsters whose model was walked.</summary>
    public int Walked { get; set; }

    /// <summary>Monsters the table names no .ao file for, so there was nothing to walk.</summary>
    public int Nameless { get; set; }

    /// <summary>Attachments seen across all of them.</summary>
    public int Pieces { get; set; }

    /// <summary>Monsters carrying at least one flag.</summary>
    public int Odd { get; set; }

    /// <summary>How many monsters carried each flag - see the remarks on <c>Odd</c>.</summary>
    public Dictionary<string, int> Flags { get; } = new(StringComparer.Ordinal);
}

/// <summary>A sweep's answer: what it counted, and which monsters are worth a full dump.</summary>
/// <param name="Tally">The counts.</param>
/// <param name="Flagged">The paths of the monsters carrying at least one flag, in the sweep's order.</param>
public sealed record SweepResult(SweepTally Tally, IReadOnlyList<string> Flagged)
{
    /// <summary>No install, no table, or nothing asked for.</summary>
    public static SweepResult Nothing { get; } = new(new SweepTally(), []);
}
