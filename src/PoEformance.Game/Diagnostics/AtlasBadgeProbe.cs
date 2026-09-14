using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Ui;

namespace PoEformance.Game.Diagnostics;

/// <summary>
/// Asks of every badge on the atlas whether it belongs to the MAP or to the NODE, and reads the
/// game's own name for the ones that belong to the map.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS. data/atlas-maps.json carries curated words - tower, arbiter, quest, hideout -
/// that the WorldAreas Tags column does not have (see WorldAreaCatalogue), and the obvious next
/// place to look is the icon the game draws over a node. Three captures say that is two different
/// things wearing one coat:
///
/// - MOST badges are the node's ROLLED CONTENT. The same map id carries different badges on
///   different nodes - MapPit reads eight distinct badge sets across its nodes in one capture -
///   so nothing there can be a property of the map.
/// - A FEW are the map itself: every uncompleted node of that map carries them, and no node of
///   any other map does.
///
/// THE DISCRIMINATOR IS COMPUTED HERE RATHER THAN LISTED. A hard-coded list of "the special ids"
/// is exactly the kind of knowledge that rots silently when a league adds one, so this measures
/// the property instead: a badge is STABLE when, for every map id that carries it at all, EVERY
/// UNCOMPLETED node of that map id carries it. That is checkable on any capture, it needs no file
/// to compare against, and a new id that behaves the same way is reported without anybody editing
/// this.
///
/// THE "UNCOMPLETED" IN THAT RULE IS MEASURED, not a hedge to make the numbers work. A node that
/// has been run drops its marker, on all three captures and without exception: the claimed
/// MapHideoutFelled_Claimable carries nothing while five unclaimed Canal hideouts carry 0x203E9,
/// and every Completed unique reads bare beside its Locked twin. Counting completed nodes would
/// therefore report the unique marker as content, which is the wrong answer arrived at honestly.
///
/// And the marker is the NODE rather than the entitlement, which the game mechanic disguises:
/// finishing a claimable hideout unlocks that category for the character once and for good, so
/// "the marker means this hideout is still unclaimed" is the natural guess and it is wrong. The
/// owner already held Canal and Limestone when the last capture was taken, and six Canal
/// claimables in it are still marked. Running THAT node is what clears it.
///
/// It costs almost nothing: the badge ids come from the reader, which has just read them, and the
/// only memory this touches is one badge child per DISTINCT id - a handful of reads, not a
/// thousand child walks.
/// </remarks>
public sealed class AtlasBadgeProbe
{
    /// <summary>Most distinct badge ids reported. A full atlas shows about fifty.</summary>
    private const int MostIds = 96;

    /// <summary>Most badge children walked under one node - the reader's own guard, repeated.</summary>
    private const int MostContents = 64;

    /// <summary>Map ids listed per badge before the line is cut short.</summary>
    private const int MostNamed = 8;

    /// <summary>
    /// How many map ids a badge may cover and still be worth naming.
    /// </summary>
    /// <remarks>
    /// Stability alone is not quite enough: a content that happens to sit on one node of one map
    /// is trivially stable, and so is a badge every map carries. What is interesting is the
    /// middle - a handful of maps, all of their nodes - so the narrow ones are printed in full
    /// and the rest are counted.
    /// </remarks>
    private const int NarrowMapIds = 24;

    private readonly IMemoryReader _reader;
    private readonly UiElementReader _elements;

    private readonly int[] _badgeChildPath;
    private readonly int _badgeContentId;
    private readonly int _badgeContentName;
    private readonly int _badgeContentNameGh;

    public AtlasBadgeProbe(IMemoryReader reader, OffsetSchema schema, UiElementReader elements)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(elements);
        _reader = reader;
        _elements = elements;

        StructDef node = schema.Structs["AtlasNode"];
        _badgeChildPath = [(int)node.Constants["BadgeChild0"], (int)node.Constants["BadgeChild1"]];
        _badgeContentId = (int)node.Constants["BadgeContentId"];
        _badgeContentName = (int)node.Constants["BadgeContentName"];
        _badgeContentNameGh = (int)node.Constants["BadgeContentNameGameHelper"];
    }

    /// <summary>What every badge on this atlas is, sorted with the map-shaped ones first.</summary>
    public IReadOnlyList<string> Probe(IReadOnlyList<ProbedNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var said = new List<string>();
        if (nodes.Count == 0)
        {
            return said;
        }

        said.Add(string.Empty);
        said.Add("BADGE PROBE - which badges belong to the MAP and which to the node that rolled them:");

        // Every UNCOMPLETED node of every map, so that "all of them carry it" can be asked at all.
        // Completed nodes are counted apart because they have already dropped whatever they had.
        var nodesPerMap = new Dictionary<string, int>(StringComparer.Ordinal);
        var carriers = new Dictionary<uint, Dictionary<string, int>>();
        int withBadges = 0, supplied = 0, completed = 0;
        foreach (ProbedNode node in nodes)
        {
            if (node.MapId.Length == 0)
            {
                continue;
            }

            if (node.Completed)
            {
                completed++;
                continue;
            }

            nodesPerMap[node.MapId] = nodesPerMap.GetValueOrDefault(node.MapId) + 1;
            if (node.BadgeIds is not { } badges)
            {
                continue;
            }

            supplied++;
            if (badges.Count > 0)
            {
                withBadges++;
            }

            foreach (uint id in badges)
            {
                Dictionary<string, int> per = carriers.TryGetValue(id, out Dictionary<string, int>? found)
                    ? found
                    : carriers[id] = new Dictionary<string, int>(StringComparer.Ordinal);
                per[node.MapId] = per.GetValueOrDefault(node.MapId) + 1;
            }
        }

        if (supplied == 0)
        {
            said.Add("  the caller passed no badge ids, so there is nothing to sort - see ProbedNode.BadgeIds");
            return said;
        }

        said.Add($"  {supplied} uncompleted nodes with a map id, {withBadges} of them carrying a badge,"
            + $" {nodesPerMap.Count} distinct maps, {carriers.Count} distinct badge ids"
            + $" ({completed} completed nodes set aside - they have dropped their markers)");

        // Stable first, then by reach: the answer being looked for is a badge that a few maps
        // carry on all of their nodes, and that ordering puts it at the top whatever its id is.
        List<(uint Id, Dictionary<string, int> Per, bool Stable)> ranked =
        [
            .. carriers
                .Select(pair => (Id: pair.Key, Per: pair.Value, Stable: IsStable(pair.Value, nodesPerMap)))
                .OrderByDescending(entry => entry.Stable)
                .ThenBy(entry => entry.Per.Count)
                .ThenBy(entry => entry.Id),
        ];

        foreach ((uint id, Dictionary<string, int> per, bool stable) in ranked.Take(MostIds))
        {
            said.AddRange(One(id, per, stable, nodes));
        }

        if (ranked.Count > MostIds)
        {
            said.Add($"  ...and {ranked.Count - MostIds} more badge ids");
        }

        return said;
    }

    /// <summary>
    /// Whether a badge marks the map rather than the roll.
    /// </summary>
    /// <remarks>
    /// The whole measurement, in one line: for every map that carries this badge at all, does
    /// EVERY UNCOMPLETED node of that map carry it? Content fails this the moment a second node of
    /// the same map rolls something else, which on a full atlas is immediately.
    /// </remarks>
    private static bool IsStable(Dictionary<string, int> per, Dictionary<string, int> nodesPerMap)
    {
        foreach ((string mapId, int carrying) in per)
        {
            if (carrying != nodesPerMap.GetValueOrDefault(mapId))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>One badge id: how it behaves, and what the game calls it.</summary>
    private List<string> One(uint id, Dictionary<string, int> per, bool stable, IReadOnlyList<ProbedNode> nodes)
    {
        int carried = per.Values.Sum();
        var said = new List<string>
        {
            $"  0x{id:X8}  {carried,4} nodes over {per.Count,3} maps  "
                + (stable
                    ? "STABLE - every uncompleted node of those maps carries it"
                    : "varies between nodes of the same map"),
        };

        // Only the narrow ones get a name read. A badge three hundred nodes carry is content
        // whatever it is called, and the read would be spent saying so.
        if (stable && per.Count <= NarrowMapIds)
        {
            said.Add($"      maps: {string.Join(", ", per.Keys.Order(StringComparer.Ordinal).Take(MostNamed))}"
                + (per.Count > MostNamed ? $", +{per.Count - MostNamed} more" : string.Empty));
            said.AddRange(Name(id, per, nodes));
        }

        return said;
    }

    /// <summary>
    /// The game's own words for a badge, off the first child that carries it.
    /// </summary>
    /// <remarks>
    /// Both candidate slots, because that is the pair a capture is for: +0x2E8 held the name on
    /// 0.5.5 and GameHelper2's +0x278 was null, and this is the reading that says whether the
    /// same holds for the map-shaped ids rather than only for the content one already sampled.
    /// The string is what could retire data/atlas-content.json for this family - the file already
    /// does not know several of these ids.
    /// </remarks>
    private List<string> Name(uint id, Dictionary<string, int> per, IReadOnlyList<ProbedNode> nodes)
    {
        foreach (ProbedNode node in nodes)
        {
            if (node.BadgeIds is not { } badges || !badges.Contains(id) || !per.ContainsKey(node.MapId))
            {
                continue;
            }

            ulong holder = node.Element;
            foreach (int step in _badgeChildPath)
            {
                holder = _elements.Child(holder, step);
                if (holder == 0)
                {
                    break;
                }
            }

            if (holder == 0)
            {
                continue;
            }

            foreach (ulong badge in _elements.Children(holder, MostContents))
            {
                if (!_reader.TryRead(badge + (ulong)_badgeContentId, out uint mine) || mine != id)
                {
                    continue;
                }

                return
                [
                    $"      badge 0x{badge:X} on {node.MapId}"
                        + $"  +0x{_badgeContentName:X} -> {ProbeBytes.Text(_reader, badge + (ulong)_badgeContentName)}"
                        + $"  +0x{_badgeContentNameGh:X} -> {ProbeBytes.Text(_reader, badge + (ulong)_badgeContentNameGh)}",
                ];
            }
        }

        // Not a failure worth hiding: the byte vector carries ids whose child the game has not
        // drawn, and a badge with no child has no string to read however stable it is.
        return [$"      no drawn badge child carries 0x{id:X8}, so the game's own name for it is not in reach here"];
    }
}
