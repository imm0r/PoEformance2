using PoEformance.Game.Files;

namespace PoEformance.Game.World;

/// <summary>
/// What a slot asks of the tile under it, or what a tile is, in the terms no placement changes: the size, the tag, and the edge and ground types sorted - equal by value, so slots of a kind share one verdict row.
/// </summary>
internal sealed record SlotWant(int Width, int Height, string Tag, string[] Edges, string[] Grounds)
{
    public bool Equals(SlotWant? other)
        => other is not null
            && Width == other.Width
            && Height == other.Height
            && string.Equals(Tag, other.Tag, StringComparison.OrdinalIgnoreCase)
            && Edges.AsSpan().SequenceEqual(other.Edges, StringComparer.OrdinalIgnoreCase)
            && Grounds.AsSpan().SequenceEqual(other.Grounds, StringComparer.OrdinalIgnoreCase);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Width);
        hash.Add(Height);
        hash.Add(Tag, StringComparer.OrdinalIgnoreCase);
        foreach (string edge in Edges)
        {
            hash.Add(edge, StringComparer.OrdinalIgnoreCase);
        }

        foreach (string one in Grounds)
        {
            hash.Add(one, StringComparer.OrdinalIgnoreCase);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// The one rule a slot judges a tile by - the search's, its scorer's and the tile placing's alike, so the three never disagree about a tile.
/// </summary>
/// <remarks>
/// THE PART OF A SLOT NO PLACEMENT CHANGES: the size as an unordered pair, the tag where the slot
/// names one, and the edge and corner ground types as SETS of four - because which side of a turned
/// tile meets which side of a turned room is a convention nothing here has settled for tiles.
///
/// AN UNNAMED TYPE IN THE SLOT IS ANY, as nought is in the ground stamp (RoomFinder: a corner type
/// nought names nothing). The first rule asked the two sets to be equal, an unnamed type to meet an
/// unnamed one, and Sinter Rift measured that against a place the doodads had fixed: of the boss
/// room's 17 big slots 15 were alike by it and 17 with an unnamed type as any - the two misses were
/// asks that left a ground unnamed where the laid tile named desert_dune_titan_trim, the same pattern
/// as every miss of the entrance rooms' best candidates. A type the tile names and the slot does not
/// is the generator's choice, not a disagreement; a type the slot names and the tile lacks is one.
/// </remarks>
internal static class SlotWants
{
    /// <summary>No definition to judge by - the tile does not count.</summary>
    public const byte Unknown = 1;

    /// <summary>The tile is not what the slot asks for.</summary>
    public const byte Unlike = 2;

    /// <summary>The tile is what the slot asks for.</summary>
    public const byte Alike = 3;

    /// <summary>What a slot asks, in the terms above.</summary>
    public static SlotWant Of(RoomLayout room, RoomSlot slot)
    {
        ArgumentNullException.ThrowIfNull(room);
        return new SlotWant(
            slot.Width,
            slot.Height,
            room.Named(slot.Tag),
            Sorted([room.Named(slot.Edge(0)), room.Named(slot.Edge(1)), room.Named(slot.Edge(2)), room.Named(slot.Edge(3))]),
            Sorted([room.Named(slot.Ground(0)), room.Named(slot.Ground(1)), room.Named(slot.Ground(2)), room.Named(slot.Ground(3))]));
    }

    /// <summary>A tile file's identity in the terms a slot asks in, or null where it carries too little to judge by.</summary>
    public static SlotWant? Of(TileIdentity? tile)
        => tile is { Edges.Count: 4, Grounds.Count: 4 }
            ? new SlotWant(tile.Width, tile.Height, tile.Tag, Sorted(tile.Edges), Sorted(tile.Grounds))
            : null;

    /// <summary>A slot's verdict on a tile - unknown where the tile has nothing to judge by.</summary>
    public static byte Verdict(SlotWant wanted, SlotWant? laid)
        => laid is null ? Unknown : IsAlike(wanted, laid) ? Alike : Unlike;

    /// <summary>Whether a tile is what a slot asks for - see the class remarks.</summary>
    public static bool IsAlike(SlotWant wanted, SlotWant laid)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        ArgumentNullException.ThrowIfNull(laid);
        return ((laid.Width == wanted.Width && laid.Height == wanted.Height) || (laid.Width == wanted.Height && laid.Height == wanted.Width))
            && (wanted.Tag.Length == 0 || string.Equals(wanted.Tag, laid.Tag, StringComparison.OrdinalIgnoreCase))
            && Covers(laid.Edges, wanted.Edges)
            && Covers(laid.Grounds, wanted.Grounds);
    }

    /// <summary>What a slot asks for, or what a tile is, in a line's words.</summary>
    public static string Said(SlotWant one)
    {
        ArgumentNullException.ThrowIfNull(one);
        return FormattableString.Invariant(
            $"{one.Width}x{one.Height}{(one.Tag.Length > 0 ? " " + one.Tag : string.Empty)} edges [{string.Join(", ", one.Edges.Select(Short))}] grounds [{string.Join(", ", one.Grounds.Select(Short))}]");
    }

    /// <summary>A file's name without its folder or extension, or a dash for none.</summary>
    public static string Short(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.Length == 0 ? "-" : Path.GetFileNameWithoutExtension(path.Replace('\\', '/'));
    }

    /// <summary>
    /// Whether every type the slot names is among the tile's, each of the tile's answering one ask - both sorted the same way, so one pass over the two does it.
    /// </summary>
    private static bool Covers(string[] laid, string[] wanted)
    {
        var at = 0;
        foreach (string want in wanted)
        {
            if (want.Length == 0)
            {
                continue;
            }

            while (at < laid.Length && StringComparer.OrdinalIgnoreCase.Compare(laid[at], want) < 0)
            {
                at++;
            }

            if (at >= laid.Length || !string.Equals(laid[at], want, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            at++;
        }

        return true;
    }

    private static string[] Sorted(IReadOnlyList<string> four)
    {
        string[] sorted = [.. four.Select(one => one.Replace('\\', '/'))];
        Array.Sort(sorted, StringComparer.OrdinalIgnoreCase);
        return sorted;
    }
}
