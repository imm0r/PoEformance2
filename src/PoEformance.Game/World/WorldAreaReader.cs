using PoEformance.Core.Memory;
using PoEformance.Core.Schema;

namespace PoEformance.Game.World;

/// <summary>What kind of area the player is in.</summary>
/// <param name="Id">The area's internal id, e.g. <c>G1_2</c>, <c>MapRiverhold</c>.</param>
/// <param name="Act">Act number; endgame areas are not part of an act.</param>
public readonly record struct AreaInfo(string Id, string Name, int Act, bool IsTown, bool IsHideout)
{
    /// <summary>Nothing resolved - treated as hostile, so a failed read never blanks the overlay.</summary>
    public static AreaInfo Unknown { get; } = new(string.Empty, string.Empty, 0, false, false);

    /// <summary>True when there is something here to fight.</summary>
    /// <remarks>
    /// Town and hideout only. Both are read from the game's own data - IsTown is a field,
    /// IsHideout is the reference's id test - so this cannot drift into hiding an overlay
    /// somewhere it is wanted. An area that did not resolve counts as hostile: the failure
    /// mode of a read that went wrong should be "the overlay is still there", not "the
    /// overlay silently stopped working".
    /// </remarks>
    public bool IsHostile => !IsTown && !IsHideout;

    /// <summary>A short description for the status readout.</summary>
    public string Describe()
    {
        if (Id.Length == 0)
        {
            return "unknown";
        }

        string kind = IsHideout ? "hideout" : IsTown ? "town" : "hostile";
        return $"{Name} [{Id}] act {Act}, {kind}";
    }
}

/// <summary>
/// Reads the current area's row out of the game's own area table.
/// </summary>
/// <remarks>
/// TWO hops, which is the part worth knowing: WorldData holds a pointer to a details
/// struct, and the details struct holds the pointer to the WorldAreas.dat ROW. Reading the
/// row fields straight off WorldData lands in the middle of the camera structure instead.
///
/// Ported from the AHK tool's <c>ReadWorldAreaDat</c>, including its two derived rules:
/// hideouts are identified by the id (there is no flag for it), and a couple of league hubs
/// count as towns despite the field saying otherwise.
/// </remarks>
public sealed class WorldAreaReader
{
    private readonly IMemoryReader _reader;
    private readonly int _detailsPtr;
    private readonly int _rowPtr;
    private readonly int _idPtr;
    private readonly int _namePtr;
    private readonly int _act;
    private readonly int _isTown;

    public WorldAreaReader(IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;

        _detailsPtr = schema.Structs["WorldData"].OffsetOf("WorldAreaDetailsPtr");
        _rowPtr = schema.Structs["WorldAreaDetails"].OffsetOf("RowPtr");

        StructDef row = schema.Structs["WorldAreaDat"];
        _idPtr = row.OffsetOf("IdPtr");
        _namePtr = row.OffsetOf("NamePtr");
        _act = row.OffsetOf("Act");
        _isTown = row.OffsetOf("IsTown");
    }

    /// <summary>Reads the current area, or <see cref="AreaInfo.Unknown"/> if it does not resolve.</summary>
    public AreaInfo Read(ulong worldData)
    {
        ulong details = _reader.ReadPointer(worldData + (ulong)_detailsPtr);
        if (details == 0)
        {
            return AreaInfo.Unknown;
        }

        ulong row = _reader.ReadPointer(details + (ulong)_rowPtr);
        if (row == 0)
        {
            return AreaInfo.Unknown;
        }

        string id = ReadString(row + (ulong)_idPtr);
        if (id.Length == 0)
        {
            return AreaInfo.Unknown;
        }

        string name = ReadString(row + (ulong)_namePtr);
        int act = _reader.Read<int>(row + (ulong)_act);
        bool isTown = _reader.Read<byte>(row + (ulong)_isTown) != 0;

        // The reference's two derived rules. "map" excludes the map-device hideouts, whose
        // ids contain both words and which are very much hostile.
        bool isHideout = id.Contains("hideout", StringComparison.OrdinalIgnoreCase)
                         && !id.Contains("map", StringComparison.OrdinalIgnoreCase);
        isTown = isTown
                 || id.Equals("HeistHub", StringComparison.OrdinalIgnoreCase)
                 || id.Equals("KalguuranSettlersLeague", StringComparison.OrdinalIgnoreCase);

        return new AreaInfo(id, name, act, isTown, isHideout);
    }

    /// <summary>Reads a wide string through a pointer field, empty when it does not resolve.</summary>
    private string ReadString(ulong pointerField)
    {
        ulong text = _reader.ReadPointer(pointerField);
        return text == 0 ? string.Empty : _reader.ReadUnicodeString(text, 64);
    }
}
