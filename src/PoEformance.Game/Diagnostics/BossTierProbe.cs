using System.Buffers.Binary;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;

namespace PoEformance.Game.Diagnostics;

/// <summary>One node's boss-bearing map, reduced to the bytes that might carry its tier.</summary>
/// <param name="MapId">The map, as the game spells it.</param>
/// <param name="Map">Its row in EndgameMaps, whole.</param>
/// <param name="Atlas">The node's row in EndgameMapAtlas, whole.</param>
public readonly record struct BossMap(string MapId, byte[] Map, byte[] Atlas);

/// <summary>
/// Finds where the game keeps the difference between a Powerful and a Deadly map boss.
/// </summary>
/// <remarks>
/// WHAT IS BEING LOOKED FOR, and why it cannot be looked up. The game draws two different
/// symbols - gold for "Powerful Map Boss", red for "Deadly Map Boss" - with two different
/// tooltips, on maps like Ice Cave against The Iron Citadel. This tool labels both from
/// EndgameMapContent row 0, which is named "Powerful Map Boss", so the distinction is lost.
///
/// THREE CANDIDATES HAVE ALREADY BEEN STRUCK OFF by measurement rather than by reading:
/// EndgameMapContent has 70 rows and none is named Deadly; EndgameMaps.MapContentArray marks
/// thirteen maps and every one is Delirium or Expedition; and the badge id carries no second
/// variant of 0x64. The glossary says a Deadly boss "appears in specific Maps", so it is a
/// property of the MAP or of its atlas position, and this looks at both.
///
/// IT DOES NOT GUESS WHICH COLUMN. It reads the two rows WHOLE for every boss-bearing map on
/// screen and reports only the byte offsets that are not the same across all of them - so a
/// column nobody has named yet shows up as an offset with two values. Which of those values is
/// the Deadly one is the one thing memory cannot say: the person at the screen can see the red
/// icon, so the map ids are printed beside their bytes and they match them up.
///
/// A DIAGNOSTIC, not a feature. It runs from the atlas check and nowhere else.
/// </remarks>
public sealed class BossTierProbe
{
    /// <summary>The badge every map boss carries - EndgameMapContent row 0 plus the base.</summary>
    /// <remarks>Both tiers carry it: the glossary says a Deadly boss IS a Powerful one.</remarks>
    private const uint MapBossBadge = 0x64;

    /// <summary>How many maps are spelled out. Enough to see a pattern, not enough to bury it.</summary>
    private const int MostMaps = 48;

    /// <summary>How many differing offsets are worth printing before the report is noise.</summary>
    private const int MostOffsets = 20;

    private readonly IMemoryReader _reader;
    private readonly int _dataStorage;
    private readonly int _data;
    private readonly int _mapRow;
    private readonly int _atlasRow;
    private readonly int _mapRowSize;
    private readonly int _atlasRowSize;

    public BossTierProbe(IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;

        StructDef node = schema.Structs["AtlasNode"];
        StructDef data = schema.Structs["AtlasNodeData"];

        _dataStorage = (int)node.Constants["DataStoragePtr"];
        _data = (int)node.Constants["DataPtr"];

        // +0x290 IS READ AS A FOREIGN REFERENCE HERE, and the schema calls it the map id's string
        // wrapper. Both readings fit the bytes - a reference is a row then a table, a wrapper is
        // two pointers - and the layout probe resolved the word after it as the EndgameMaps table,
        // which is what a reference would put there. So it is tried, and a row that does not read
        // is simply left out rather than argued with.
        _mapRow = data.OffsetOf("MapDataPtr");
        _atlasRow = data.OffsetOf("AtlasRowPtr");
        _mapRowSize = (int)schema.Structs["EndgameMapsRow"].Constants["ComputedRowSize"];
        _atlasRowSize = (int)schema.Structs["EndgameMapAtlasRow"].Constants["ComputedRowSize"];
    }

    /// <summary>The report, or a line saying why there is none.</summary>
    /// <param name="nodes">Every node read, with its badges and its map id.</param>
    public IReadOnlyList<string> Probe(IReadOnlyList<(ulong Address, string MapId, IReadOnlyList<uint> Badges)> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var maps = new Dictionary<string, BossMap>(StringComparer.Ordinal);
        foreach ((ulong address, string mapId, IReadOnlyList<uint> badges) in nodes)
        {
            if (mapId.Length == 0 || maps.ContainsKey(mapId) || !Carries(badges))
            {
                continue;
            }

            if (Rows(address) is { } read)
            {
                maps[mapId] = read with { MapId = mapId };
            }
        }

        if (maps.Count < 2)
        {
            return
            [
                $"BOSS TIERS - {maps.Count} boss-bearing map(s) readable; two are needed to compare."
                    + " Scroll the atlas so a GOLD and a RED boss node have both been drawn, then check again.",
            ];
        }

        var said = new List<string>
        {
            $"BOSS TIERS - {maps.Count} maps carrying badge 0x{MapBossBadge:X2} (\"Powerful Map Boss\","
                + " which the glossary says a Deadly boss also is)",
        };

        said.AddRange(Differing("EndgameMaps", maps.Values, one => one.Map, _mapRowSize));
        said.AddRange(Differing("EndgameMapAtlas", maps.Values, one => one.Atlas, _atlasRowSize));
        return said;
    }

    private static bool Carries(IReadOnlyList<uint> badges)
    {
        foreach (uint raw in badges)
        {
            if ((raw & 0xFFFF) == MapBossBadge)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The offsets at which these rows are not all the same, and what each map has there.
    /// </summary>
    /// <remarks>
    /// THE WHOLE POINT IS THAT NO COLUMN IS NAMED. A row is compared byte by byte against the
    /// others, so a field nobody has written down shows up as an offset carrying two values -
    /// and the map ids beside it are what lets somebody who can SEE the red icon say which value
    /// means what. Offsets that are the same everywhere say nothing and are not printed.
    /// </remarks>
    private static IReadOnlyList<string> Differing(
        string what, IEnumerable<BossMap> maps, Func<BossMap, byte[]> rowOf, int size)
    {
        BossMap[] all = [.. maps.Where(one => rowOf(one).Length == size)];
        if (all.Length < 2)
        {
            return
            [
                $"  {what}: {all.Length} of these rows read whole - not enough to compare."
                    + " A recording answers only the reads its build performed.",
            ];
        }

        var offsets = new List<int>();
        for (int at = 0; at < size; at++)
        {
            byte first = rowOf(all[0])[at];
            foreach (BossMap one in all)
            {
                if (rowOf(one)[at] != first)
                {
                    offsets.Add(at);
                    break;
                }
            }
        }

        if (offsets.Count == 0)
        {
            return [$"  {what}: every byte of 0x{size:X} is the same on all of them - the tier is not in this row"];
        }

        var said = new List<string>
        {
            $"  {what}: {offsets.Count} of 0x{size:X} bytes differ"
                + (offsets.Count > MostOffsets ? $" (first {MostOffsets} shown)" : string.Empty),
        };

        List<int> shown = [.. offsets.Take(MostOffsets)];
        said.Add("    " + string.Join(" ", shown.Select(at => $"+{at:X3}")));

        foreach (BossMap one in all.OrderBy(one => one.MapId, StringComparer.Ordinal).Take(MostMaps))
        {
            said.Add($"    {one.MapId,-38} "
                + string.Join(" ", shown.Select(at => $"{rowOf(one)[at]:X2}  ")));
        }

        if (all.Length > MostMaps)
        {
            said.Add($"    ... and {all.Length - MostMaps} more");
        }

        return said;
    }

    /// <summary>Both rows behind a node, or null where either will not read.</summary>
    private BossMap? Rows(ulong node)
    {
        if (!_reader.TryRead(node + (ulong)_dataStorage, out ulong storage) || storage == 0
            || !_reader.TryRead(storage + (ulong)_data, out ulong body) || body == 0)
        {
            return null;
        }

        // EITHER ROW IS ENOUGH, and requiring both is what made the first version of this report
        // nothing at all. Measured against the committed captures: the EndgameMaps row reads on
        // every boss node, and the atlas row on NONE - not because the pointer is bad but because
        // no build has ever read that row WHOLE, and a recording holds only the reads its build
        // performed. Reading it here is what puts it in the next capture.
        byte[]? map = Row(body + (ulong)_mapRow, _mapRowSize);
        byte[]? atlas = Row(body + (ulong)_atlasRow, _atlasRowSize);
        return map is null && atlas is null ? null : new BossMap(string.Empty, map ?? [], atlas ?? []);
    }

    private byte[]? Row(ulong slot, int size)
    {
        if (!_reader.TryRead(slot, out ulong at) || !MemoryReaderExtensions.IsPlausiblePointer(at))
        {
            return null;
        }

        var row = new byte[size];
        return _reader.TryRead(at, row.AsSpan()) ? row : null;
    }

    /// <summary>Unused today, kept so a reader of this file is not left wondering.</summary>
    /// <remarks>A row's first eight bytes are usually a pointer; this names it for a future line.</remarks>
    internal static ulong Head(byte[] row) => BinaryPrimitives.ReadUInt64LittleEndian(row);
}
