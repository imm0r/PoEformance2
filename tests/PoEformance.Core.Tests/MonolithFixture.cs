using PoEformance.Core.Schema;

namespace PoEformance.Core.Tests;

/// <summary>
/// A Runecraft monolith laid into fake memory the way the reference read a real one: the device
/// entity with its StateMachine, the station registered as a listener on it, the rune table the
/// anchor points into, and the area's content tags.
/// </summary>
/// <remarks>
/// Built THROUGH THE SHIPPED SCHEMA, so the fixture moves with the file rather than vouching for
/// a copy of it. A DECOY LISTENER comes first in the vector - a station owned by another device -
/// because that is the shape several clustered monoliths produce, and a walk that took the first
/// node would read the neighbour's holes. The names of the device's states are laid out as the
/// AHK decoder found them: inline std::strings, StateNameSize apart.
/// </remarks>
internal sealed class MonolithFixture
{
    public const ulong Device = 0x0000_0400_0000_0000;
    public const ulong OtherDevice = Device + 0x0800;
    private const ulong Details = Device + 0x1000;
    private const ulong Lookup = Device + 0x2000;
    private const ulong Vector = Device + 0x3000;
    private const ulong Bucket = Device + 0x4000;
    private const ulong ComponentNames = Device + 0x5000;
    private const ulong PathChars = Device + 0x6000;

    public const ulong Machine = Device + 0x10_000;
    private const ulong Definition = Device + 0x11_000;
    private const ulong StateNames = Device + 0x12_000;
    private const ulong StateValues = Device + 0x13_000;
    private const ulong Listeners = Device + 0x14_000;
    public const ulong DecoyNode = Device + 0x15_000;
    public const ulong Node = Device + 0x15_100;
    private const ulong DecoyStation = Device + 0x16_000;
    public const ulong Station = Device + 0x17_000;
    private const ulong Holder = Device + 0x18_000;
    private const ulong HolderTable = Device + 0x18_100;
    public const ulong RuneTable = Device + 0x19_000;
    private const ulong GlowVector = Device + 0x1A_000;
    public const ulong RecipeRow = Device + 0x1B_000;
    private const ulong Strings = Device + 0x1C_000;
    public const ulong Area = Device + 0x1D_000;
    private const ulong AreaTagsVector = Device + 0x1E_000;

    /// <summary>The device's entity id - the number the game shows.</summary>
    public const uint DeviceId = 1297;

    public const string DevicePath = "Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter";

    private readonly OffsetSchema _schema;

    public MonolithFixture(
        OffsetSchema schema,
        int holes = 5,
        int anchorRune = 20,
        int anchorHole = 2,
        int[]? glow = null,
        int mode = 1,
        bool empowered = false,
        string? selected = null,
        bool panelOpen = false,
        bool anchorless = false,
        bool rerolled = false,
        int activated = 1,
        int[]? areaTags = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        _schema = schema;
        glow ??= [anchorHole];
        areaTags ??= [54];

        // A module the panel-open pointer can land in, so the module check is exercised.
        Reader.ModuleSize = 0x100_0000;

        PlaceEntity(Device, DeviceId, DevicePath, [("StateMachine", Machine), ("Render", Device + 0x700)]);

        StructDef machine = schema.Structs["StateMachine"];
        int nameSize = (int)machine.Constants["StateNameSize"];

        // The listener vector: the decoy first, the real node second.
        Reader.Place<ulong>(Machine + (ulong)machine.OffsetOf("ListenerVec"), Listeners);
        Reader.Place<ulong>(Machine + (ulong)machine.OffsetOf("ListenerVec") + 8, Listeners + 16);
        Reader.Place<ulong>(Listeners, DecoyNode);
        Reader.Place<ulong>(Listeners + 8, Node);

        StructDef station = schema.Structs["RuneStation"];
        ulong sub = (ulong)station.Constants["ListenerSubOffset"];
        Reader.Place<ulong>(DecoyNode, DecoyStation + sub);
        Reader.Place<ulong>(Node, Station + sub);
        Reader.Place<ulong>(DecoyStation + (ulong)station.OffsetOf("OwnerEntityPtr"), OtherDevice);
        Reader.Place<int>(DecoyStation + (ulong)station.OffsetOf("HoleCount"), 99);

        // The states, named as the decoder finds them: inline std::strings, one per value.
        Reader.Place<ulong>(Machine + (ulong)machine.OffsetOf("StatesPtr"), Definition);
        Reader.Place<ulong>(Definition + (ulong)machine.Constants["StateNamesBase"], StateNames);
        string[] names = ["sockets", "activated", "is_rerolled"];
        long[] values = [Math.Min(holes, 6), activated, rerolled ? 1 : 0];
        Reader.Place(StateNames, new byte[names.Length * nameSize]);
        for (int i = 0; i < names.Length; i++)
        {
            PlaceInlineStdString(StateNames + (ulong)(i * nameSize), names[i]);
        }

        Reader.Place<ulong>(Machine + (ulong)machine.OffsetOf("StatesValuesFirst"), StateValues);
        Reader.Place<ulong>(Machine + (ulong)machine.OffsetOf("StatesValuesLast"), StateValues + (ulong)(values.Length * 8));
        for (int i = 0; i < values.Length; i++)
        {
            Reader.Place(StateValues + (ulong)(i * 8), values[i]);
        }

        // The station itself.
        int runeRow = (int)schema.Structs["Expedition2RunesRow"].Constants["ComputedRowSize"];
        Reader.Place(Station, new byte[0x100]);
        Reader.Place<ulong>(Station + (ulong)station.OffsetOf("OwnerEntityPtr"), Device);
        Reader.Place<ulong>(
            Station + (ulong)station.OffsetOf("AnchorRuneRowPtr"),
            anchorless ? 0UL : RuneTable + (ulong)(anchorRune * runeRow));
        Reader.Place<ulong>(Station + (ulong)station.OffsetOf("AnchorHolderPtr"), Holder);
        Reader.Place<ulong>(Holder + (ulong)station.Constants["HolderTablePtr"], HolderTable);
        Reader.Place<ulong>(HolderTable, RuneTable);
        Reader.Place(RuneTable, new byte[34 * runeRow]);
        Reader.Place<int>(Station + (ulong)station.OffsetOf("HoleCount"), holes);
        Reader.Place<int>(Station + (ulong)station.OffsetOf("AnchorHoleIndex"), anchorHole);
        Reader.Place<ulong>(Station + (ulong)station.OffsetOf("GlowSockets"), GlowVector);
        Reader.Place<ulong>(Station + (ulong)station.OffsetOf("GlowSockets") + 8, GlowVector + (ulong)(glow.Length * 4));
        for (int i = 0; i < glow.Length; i++)
        {
            Reader.Place<int>(GlowVector + (ulong)(i * 4), glow[i]);
        }

        Reader.Place<int>(Station + (ulong)station.OffsetOf("RecipeMode"), mode);
        Reader.Place<byte>(Station + (ulong)station.OffsetOf("RunesEmpowered"), empowered ? (byte)1 : (byte)0);

        if (selected is not null)
        {
            Reader.Place<ulong>(Station + (ulong)station.OffsetOf("SelectedRecipeRowPtr"), RecipeRow);
            Reader.Place<ulong>(RecipeRow + (ulong)schema.Structs["Expedition2RecipesRow"].OffsetOf("IdPtr"), Strings);
            Reader.Place(Strings, new byte[0x200]);
            Reader.PlaceUtf16(Strings, selected);
        }

        Reader.Place<ulong>(
            Station + (ulong)station.OffsetOf("PanelListenerPtr"),
            panelOpen ? Reader.ModuleBase + 0x1000 : 0UL);

        // The area's content tags.
        int tagsAt = schema.Structs["AreaInstance"].OffsetOf("ContentTags");
        Reader.Place(Area, new byte[0x200]);
        Reader.Place<ulong>(Area + (ulong)tagsAt, areaTags.Length > 0 ? AreaTagsVector : 0UL);
        Reader.Place<ulong>(Area + (ulong)tagsAt + 8, areaTags.Length > 0 ? AreaTagsVector + (ulong)(areaTags.Length * 4) : 0UL);
        for (int i = 0; i < areaTags.Length; i++)
        {
            Reader.Place<int>(AreaTagsVector + (ulong)(i * 4), areaTags[i]);
        }
    }

    public FakeMemoryReader Reader { get; } = new();

    /// <summary>
    /// The SHIPPED schema, not the frozen one the session fixtures replay through: the station
    /// and the listener vector exist only in the current file.
    /// </summary>
    public static OffsetSchema ShippedSchema()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "schema", "poe2.offsets.json")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return SchemaJson.Load(Path.Combine(dir!.FullName, "schema", "poe2.offsets.json"));
    }

    /// <summary>Points the panel-open slot at a HEAP address rather than into the module.</summary>
    public void PanelListenerOffModule()
        => Reader.Place<ulong>(Station + (ulong)_schema.Structs["RuneStation"].OffsetOf("PanelListenerPtr"), Device + 0x5_0000);

    /// <summary>Moves the anchor row off the stride, as a drifted offset would read.</summary>
    public void MisalignAnchor()
        => Reader.Place<ulong>(Station + (ulong)_schema.Structs["RuneStation"].OffsetOf("AnchorRuneRowPtr"), RuneTable + 0x68 + 4);

    /// <summary>Lays out an entity as the schema describes it: details, component vector, bucket, names.</summary>
    private void PlaceEntity(ulong entity, uint id, string path, (string Name, ulong Component)[] components)
    {
        StructDef eDef = _schema.Structs["Entity"];
        StructDef dDef = _schema.Structs["EntityDetails"];
        StructDef lDef = _schema.Structs["ComponentLookup"];
        StructDef bDef = _schema.Structs["StdBucket"];
        StructDef entryDef = _schema.Structs["ComponentLookupEntry"];
        int entrySize = (int)entryDef.Constants["Size"];

        Reader.Place(entity, new byte[0x100]);
        Reader.Place<ulong>(entity + (ulong)eDef.OffsetOf("EntityDetailsPtr"), Details);
        Reader.Place<uint>(entity + (ulong)eDef.OffsetOf("Id"), id);
        Reader.Place<ulong>(entity + (ulong)eDef.OffsetOf("ComponentsVec"), Vector);
        Reader.Place<ulong>(entity + (ulong)eDef.OffsetOf("ComponentsVecLast"), Vector + (ulong)(components.Length * 8));
        for (int i = 0; i < components.Length; i++)
        {
            Reader.Place<ulong>(Vector + (ulong)(i * 8), components[i].Component);
        }

        Reader.PlaceStdWString(Details + (ulong)dDef.OffsetOf("Path"), path, PathChars);
        Reader.Place<ulong>(Details + (ulong)dDef.OffsetOf("ComponentLookupPtr"), Lookup);

        ulong bucket = Lookup + (ulong)lDef.OffsetOf("Bucket");
        Reader.Place<int>(bucket + (ulong)bDef.OffsetOf("Capacity"), components.Length * 2);
        Reader.Place<ulong>(bucket + (ulong)bDef.OffsetOf("Data"), Bucket);
        Reader.Place<ulong>(bucket + (ulong)bDef.OffsetOf("DataLast"), Bucket + (ulong)(components.Length * entrySize));
        for (int i = 0; i < components.Length; i++)
        {
            ulong entry = Bucket + (ulong)(i * entrySize);
            ulong name = ComponentNames + (ulong)(i * 0x40);
            Reader.Place<ulong>(entry + (ulong)entryDef.OffsetOf("NamePtr"), name);
            Reader.Place<int>(entry + (ulong)entryDef.OffsetOf("Index"), i);
            Reader.PlaceUtf8(name, components[i].Name);
        }
    }

    /// <summary>An MSVC std::string short enough to live in its own header: bytes, size, capacity 15.</summary>
    private void PlaceInlineStdString(ulong at, string text)
    {
        var header = new byte[32];
        System.Text.Encoding.ASCII.GetBytes(text, header.AsSpan(0, 15));
        System.Runtime.InteropServices.MemoryMarshal.Write(header.AsSpan(16), (long)text.Length);
        System.Runtime.InteropServices.MemoryMarshal.Write(header.AsSpan(24), 15L);
        Reader.Place(at, header);
    }
}
