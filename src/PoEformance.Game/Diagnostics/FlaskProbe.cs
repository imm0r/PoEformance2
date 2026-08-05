using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Components;
using PoEformance.Game.Entities;

namespace PoEformance.Game.Diagnostics;

/// <summary>
/// Reports every step of the flask-belt walk, so a belt that reads as empty says WHERE it
/// gave up.
/// </summary>
/// <remarks>
/// The belt chain is long - ServerData, the inventory array, one inventory's item list,
/// each item's entity, its Charges component - and a failure anywhere produces the same
/// symptom: no flasks. Guessing which link broke wastes a live session per attempt, so this
/// walks the chain and prints what it found at each step, the same way the drift report and
/// the matrix hunt do for their chains.
///
/// It also dumps the item paths it rejected, which is the fastest way to see that the
/// flask-detection predicate is simply looking for the wrong word.
/// </remarks>
public sealed class FlaskProbe
{
    private readonly IMemoryReader _reader;
    private readonly OffsetSchema _schema;
    private readonly EntityReader _entities;

    public FlaskProbe(IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;
        _schema = schema;
        _entities = new EntityReader(reader, schema);
    }

    public void Report(ulong gameStatesStatic, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);

        output.WriteLine();
        output.WriteLine("flask probe");

        GameChainAddresses chain = GameChain.Resolve(_reader, _schema, gameStatesStatic);
        if (!chain.InGame)
        {
            output.WriteLine("  --    not in an area.");
            return;
        }

        int playerInfo = _schema.Structs["AreaInstance"].OffsetOf("PlayerInfo");
        int serverDataPtr = _schema.Structs["LocalPlayerStruct"].OffsetOf("ServerDataPtr");
        ulong localPlayerStruct = chain.AreaInstance + (ulong)playerInfo;
        ulong serverData = _reader.ReadPointer(localPlayerStruct + (ulong)serverDataPtr);

        output.WriteLine($"  areaInstance      0x{chain.AreaInstance:X}");
        output.WriteLine($"  localPlayerStruct 0x{localPlayerStruct:X}  (inline, AreaInstance+0x{playerInfo:X})");
        output.WriteLine($"  serverData        0x{serverData:X}");
        if (!MemoryReaderExtensions.IsPlausiblePointer(serverData))
        {
            output.WriteLine("  FAIL  ServerData did not resolve - the rest cannot be reached.");
            return;
        }

        int inventoriesOffset = _schema.Structs["ServerDataStructure"].OffsetOf("PlayerInventories");
        ulong first = _reader.ReadPointer(serverData + (ulong)inventoriesOffset);
        ulong last = _reader.ReadPointer(serverData + (ulong)inventoriesOffset + 8);
        output.WriteLine($"  inventories vec   0x{first:X} .. 0x{last:X}  (ServerData+0x{inventoriesOffset:X})");

        StructDef array = _schema.Structs["InventoryArray"];
        int entrySize = (int)array.Constants["EntrySize"];
        int idOffset = array.OffsetOf("InventoryId");
        int ptrOffset = array.OffsetOf("InventoryPtr0");

        if (!MemoryReaderExtensions.IsPlausiblePointer(first) || last <= first)
        {
            output.WriteLine("  FAIL  the inventory vector is empty or implausible.");
            return;
        }

        long count = (long)(last - first) / entrySize;
        output.WriteLine($"  inventories       {count}");
        output.WriteLine();
        output.WriteLine("  id    address             items  flaskLike  first item paths");

        StructDef inventory = _schema.Structs["Inventory"];
        int itemList = inventory.OffsetOf("ItemList");
        int itemListLast = inventory.OffsetOf("ItemListLast");
        int itemPtr = _schema.Structs["InventoryItem"].OffsetOf("Item");
        int slotStart = _schema.Structs["InventoryItem"].OffsetOf("SlotStart");

        for (long i = 0; i < Math.Min(count, 40); i++)
        {
            ulong entry = first + (ulong)(i * entrySize);
            int id = _reader.Read<int>(entry + (ulong)idOffset);
            ulong address = _reader.ReadPointer(entry + (ulong)ptrOffset);
            if (!MemoryReaderExtensions.IsPlausiblePointer(address))
            {
                continue;
            }

            ulong itemsFirst = _reader.ReadPointer(address + (ulong)itemList);
            ulong itemsLast = _reader.ReadPointer(address + (ulong)itemListLast);
            long items = MemoryReaderExtensions.IsPlausiblePointer(itemsFirst) && itemsLast > itemsFirst
                ? (long)(itemsLast - itemsFirst) / 8
                : 0;

            var paths = new List<string>();
            int flaskLike = 0;
            for (long n = 0; n < Math.Min(items, 12); n++)
            {
                ulong itemStruct = _reader.ReadPointer(itemsFirst + (ulong)(n * 8));
                if (!MemoryReaderExtensions.IsPlausiblePointer(itemStruct))
                {
                    continue;
                }

                ulong itemEntity = _reader.ReadPointer(itemStruct + (ulong)itemPtr);
                string path = _entities.Read(itemEntity)?.Path ?? "";
                if (path.Length == 0)
                {
                    continue;
                }

                if (FlaskBeltReader.IsFlask(path))
                {
                    flaskLike++;
                }

                if (paths.Count < 4)
                {
                    int slot = _reader.Read<int>(itemStruct + (ulong)slotStart);
                    paths.Add($"[{slot}] {Shorten(path)}");
                }
            }

            if (items > 0)
            {
                output.WriteLine($"  {id,-5} 0x{address:X12}  {items,5}  {flaskLike,9}  {string.Join(", ", paths)}");
            }
        }

        output.WriteLine();
        FlaskBelt belt = new FlaskBeltReader(_reader, _schema).Read(serverData);
        if (belt.IsUnknown)
        {
            output.WriteLine("  RESULT  no flasks found.");
            output.WriteLine("          If a row above lists flask-looking paths, the belt was found but the");
            output.WriteLine("          detection predicate rejected them - compare the paths against it.");
            return;
        }

        output.WriteLine($"  RESULT  {belt.Flasks.Count} flasks");
        foreach (EquippedFlask flask in belt.Flasks)
        {
            output.WriteLine($"    slot {flask.Slot}  {flask.Charges,4}/{flask.ChargesPerUse,-4} charges"
                + $"  {(flask.CanUse ? "usable" : "NOT usable")}  {Shorten(flask.Path)}");
        }
    }

    /// <summary>Trims the common metadata prefix so a row stays readable.</summary>
    private static string Shorten(string path)
        => path.StartsWith("Metadata/Items/", StringComparison.Ordinal) ? path[15..] : path;
}
