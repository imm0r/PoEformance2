using PoEformance.Game.World;

namespace PoEformance.Features;

/// <summary>How far along one endgame map is, on the way to its boss having its own picture.</summary>
public enum BossIconState
{
    /// <summary>Nothing written down and nothing derivable. The work still to do.</summary>
    Open,

    /// <summary>An entry exists, but the sheet carries no cell under the name it points at.</summary>
    Waiting,

    /// <summary>A picture resolves for this map. The marker wears it.</summary>
    Done,

    /// <summary>Somebody looked and there is no boss here.</summary>
    Skipped,
}

/// <summary>One endgame map's row in the list.</summary>
/// <param name="Id">The area id the game uses, which is what an entry is keyed by.</param>
/// <param name="Name">What the game calls it, in English, or the id when the file has no name.</param>
/// <param name="Tags">The curated kind words - "tower", "hideout", "expedition" - or none.</param>
/// <param name="State">Where it stands.</param>
/// <param name="Family">The picture family it resolves to, or empty.</param>
/// <param name="Boss">What the boss is called, where anybody wrote it down.</param>
/// <param name="Tiles">Arena tiles seen in it that nothing could name. Fills the tile field.</param>
public readonly record struct BossIconTask(
    string Id,
    string Name,
    IReadOnlyList<string> Tags,
    BossIconState State,
    string Family,
    string Boss,
    IReadOnlyList<string> Tiles);

/// <summary>
/// Which endgame maps still need a boss picture made for them.
/// </summary>
/// <remarks>
/// WHY THIS CAN BE A LIST AT ALL, which was not obvious until it was asked: the set of maps is
/// KNOWN, exactly and from the game. EndgameMaps.dat is the table an atlas node points at, and
/// its 173 rows are every map the atlas can hold - see EndgameMapCatalogue, and AtlasMapNames
/// for the shipped copy of the same set. So "which bosses are still missing" is a set
/// difference rather than a guess, and the answer is a list somebody can work through instead
/// of a thing that is noticed one map at a time while playing.
///
/// MEASURED BEFORE IT WAS BUILT: of those 173 ids, NOT ONE resolves a picture from its area id.
/// The sheet's 27 boss families are named for campaign arenas and act bosses - GrimTangleBoss,
/// IsleOfKinBoss, the eight G4_* - and the endgame maps are named MapGrimhaven, MapBluff,
/// MapAugury. The game simply does not draw a minimap icon for most of its map bosses. That is
/// what the model pane's export is for, and it is why this list starts as long as it does.
///
/// THE TAGS DO NOT DECIDE ANYTHING, deliberately. Six of the 173 are hideouts and seven are
/// towers, which plainly have no boss - but the hubs carry no tag at all and neither do the
/// maps, and a rule that hid rows by tag would hide exactly the maps a new league adds. So
/// every row is listed, the obviously boss-less ones are hidden by DEFAULT rather than
/// dropped, and a row nobody needs is ticked off by hand into the file's "skip" list, which is
/// a record of somebody having looked rather than of somebody's rule.
/// </remarks>
public static class BossIconPlan
{
    /// <summary>The tags that are hidden until somebody asks for everything.</summary>
    /// <remarks>
    /// A hideout is a hideout and a Precursor tower is a tower: neither has ever had a boss in
    /// it. Hidden rather than skipped, because hiding is this file's opinion while skipping is
    /// the user's - and the checkbox that shows them costs nothing to tick.
    /// </remarks>
    public static readonly string[] Quiet = ["hideout", "tower"];

    /// <summary>
    /// Every endgame map with where it stands, in the order they should be worked through.
    /// </summary>
    /// <remarks>
    /// THE SHEET IS THE ARBITER of whether something is done, exactly as it is when a marker is
    /// drawn - the caller passes the same lookup PoiLayer uses, so a row says "done" when and
    /// only when the marker really would wear a picture. A written entry whose art has not been
    /// laid into icons.png yet is a state of its own (<see cref="BossIconState.Waiting"/>)
    /// rather than either of the two it sits between: the pictures exist, the entry exists, and
    /// what is left is a paste into the sheet.
    /// </remarks>
    /// <param name="maps">The endgame maps, from AtlasMapNames - the file's own list.</param>
    /// <param name="icons">What has been written down, and what has been ticked off.</param>
    /// <param name="carried">Whether the sheet holds a cell under a name. IconNames.CellFor.</param>
    public static List<BossIconTask> Of(
        AtlasMapNames maps, BossIcons icons, Func<string, bool> carried)
    {
        ArgumentNullException.ThrowIfNull(maps);
        ArgumentNullException.ThrowIfNull(icons);
        ArgumentNullException.ThrowIfNull(carried);

        var rows = new List<BossIconTask>(maps.All.Count);
        foreach ((string id, AtlasMapInfo info) in maps.All)
        {
            rows.Add(Row(id, info, icons, carried));
        }

        // Open first and done last, because the list is a queue of work rather than a report;
        // by name within a state, which is the order somebody reads an atlas in.
        rows.Sort(static (left, right) => left.State != right.State
            ? left.State.CompareTo(right.State)
            : string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));

        return rows;
    }

    /// <summary>How many rows of the plan are in each state.</summary>
    public static (int Open, int Waiting, int Done, int Skipped) Count(IReadOnlyList<BossIconTask> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        (int open, int waiting, int done, int skipped) = (0, 0, 0, 0);
        foreach (BossIconTask row in rows)
        {
            switch (row.State)
            {
                case BossIconState.Open: open++; break;
                case BossIconState.Waiting: waiting++; break;
                case BossIconState.Done: done++; break;
                default: skipped++; break;
            }
        }

        return (open, waiting, done, skipped);
    }

    /// <summary>Whether a row is one of the kinds that are hidden until asked for.</summary>
    public static bool IsQuiet(BossIconTask row)
    {
        foreach (string tag in row.Tags)
        {
            if (Array.Exists(Quiet, quiet => string.Equals(quiet, tag, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>One map worked out: what it resolves to, and what that makes it.</summary>
    /// <remarks>
    /// THE AREA IS ALL THIS HAS, which is worth being explicit about. A marker resolves from the
    /// area id AND the arena's tile path, and the tile is the better key of the two - but a tile
    /// path only exists once somebody has loaded the map, so a list of work still to do cannot
    /// be built from it. A map whose tile entry alone carries its picture therefore reads as
    /// open here until the area entry is written too, which is what the export writes anyway.
    /// </remarks>
    private static BossIconTask Row(
        string id, AtlasMapInfo info, BossIcons icons, Func<string, bool> carried)
    {
        string written = icons.FamilyFor(id);
        string family = written;
        bool has = written.Length > 0 && Has(written, carried);

        if (!has)
        {
            // The derived names, on the same terms the marker uses them: only ever accepted
            // when the sheet really carries a picture under exactly that name.
            foreach (string candidate in icons.Candidates(id, string.Empty))
            {
                if (Has(candidate, carried))
                {
                    family = candidate;
                    has = true;
                    break;
                }
            }
        }

        BossIconState state = has ? BossIconState.Done
            : icons.Skipped(id) ? BossIconState.Skipped
            : written.Length > 0 ? BossIconState.Waiting
            : BossIconState.Open;

        return new BossIconTask(
            id,
            info.Name.Length > 0 ? info.Name : id,
            info.Tags,
            state,
            family,
            icons.NameOf(family),
            icons.Unnamed(id));
    }

    /// <summary>Whether the sheet carries a family's picture, in either of its two states.</summary>
    private static bool Has(string family, Func<string, bool> carried)
        => carried(BossIcons.Named(family, cleared: false))
            || carried(BossIcons.Named(family, cleared: true))
            || carried(family);
}
