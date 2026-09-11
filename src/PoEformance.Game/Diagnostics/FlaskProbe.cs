using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Components;
using PoEformance.Game.Entities;
using PoEformance.Game.Items;

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
    private readonly ItemReader? _items;

    public FlaskProbe(IMemoryReader reader, OffsetSchema schema, ItemNames? names = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;
        _schema = schema;
        _entities = new EntityReader(reader, schema);

        // A diagnostic is what somebody runs when the schema is the suspect, so it must not be
        // the thing that dies of it. ItemReader wants a dozen item structs; without them the
        // rest of this probe - the whole belt walk, which needs none of them - still reports.
        try
        {
            _items = new ItemReader(reader, schema, names ?? ItemNames.Empty);
        }
        catch (KeyNotFoundException)
        {
            _items = null;
        }
    }

    /// <summary>
    /// Walks the belt chain and prints what it found at each step.
    /// </summary>
    /// <remarks>
    /// A MISSING SCHEMA FIELD IS A FINDING HERE, NOT A CRASH, and that is not a hypothetical
    /// guard: this probe asked ServerDataStructure for a field that lives on
    /// ServerDataOffsets, so <c>--flasks</c> died with an unhandled KeyNotFoundException after
    /// three lines of output - in the one tool somebody reaches for when the schema is the
    /// suspect. OffsetOf already throws with both struct and field named, which is exactly
    /// the sentence worth printing, so the message is kept and the process is not.
    ///
    /// The probe still STOPS at that point rather than carrying on. Everything below a
    /// missing offset reads through it, and a walk that printed plausible addresses derived
    /// from a field the schema does not have would be worse than no walk at all.
    /// </remarks>
    public void Report(ulong gameStatesStatic, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);

        try
        {
            Walk(gameStatesStatic, output);
        }
        catch (KeyNotFoundException missing)
        {
            output.WriteLine($"  FAIL  {missing.Message}");
            output.WriteLine("        The probe stops here - everything below this reads through");
            output.WriteLine("        that offset. Fix it in schema/poe2.offsets.json and re-run.");
        }
    }

    private void Walk(ulong gameStatesStatic, TextWriter output)
    {
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

        // Is this really ServerData? The league name is a known field on the same base, so
        // a sane string here separates "wrong struct" from "right struct, drifted field" -
        // two failures that otherwise look identical.
        //
        // ON THE OUTER STRUCT. This asked ServerDataStructure - the INNER one, which the
        // inventories are on and the league is not - and had therefore been throwing
        // KeyNotFoundException before the probe printed a single flask. The schema records
        // the same trap on the field itself, and StashReader and StashInspector both ask
        // ServerDataOffsets; this was the one caller that did not.
        //
        // Read SHORT, for StashReader's reason: a league name is a few words, and a long
        // read off a wrong base wanders into whatever follows and comes back looking like a
        // league nobody has heard of rather than like nothing.
        int leagueOffset = _schema.Structs["ServerDataOffsets"].OffsetOf("League");
        string league = _reader.ReadStdWString(serverData + (ulong)leagueOffset, 64).Trim();
        output.WriteLine($"  league            \"{league}\"  (ServerData+0x{leagueOffset:X})"
            + (league.Length is > 0 and < 40 ? "  -> ServerData confirmed" : "  -> SUSPECT"));

        // There are two server-data structs; the inventories live on the inner one.
        var belts = new FlaskBeltReader(_reader, _schema);
        ulong inner = belts.ResolveServerDataStructure(serverData);
        output.WriteLine($"  serverDataStruct  0x{inner:X}"
            + (inner == serverData ? "  (direct)" : $"  (via +0x{_schema.Structs["ServerDataOffsets"].OffsetOf("PlayerServerData"):X} hop)"));

        int inventoriesOffset = _schema.Structs["ServerDataStructure"].OffsetOf("PlayerInventories");
        ulong first = _reader.ReadPointer(inner + (ulong)inventoriesOffset);
        ulong last = _reader.ReadPointer(inner + (ulong)inventoriesOffset + 8);
        output.WriteLine($"  inventories vec   0x{first:X} .. 0x{last:X}  (+0x{inventoriesOffset:X})");

        StructDef array = _schema.Structs["InventoryArray"];
        int entrySize = (int)array.Constants["EntrySize"];
        int idOffset = array.OffsetOf("InventoryId");
        int ptrOffset = array.OffsetOf("InventoryPtr0");

        if (!MemoryReaderExtensions.IsPlausiblePointer(first) || last <= first)
        {
            output.WriteLine("  FAIL  the inventory vector is empty or implausible at that offset.");
            ScanForInventoryArray(serverData, output);
            ScanForInventoryArray(inner, output);
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
        FlaskBelt belt = belts.Read(serverData);
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
            // "usable" means a keypress would do something, so a charm is never usable
            // however full it is - the game triggers those itself.
            string usability = flask.IsCharm
                ? "charm (self-triggering)"
                : flask.CanUse ? "usable" : "NOT usable";

            output.WriteLine($"    slot {flask.Slot}  {flask.Charges,4}/{flask.ChargesPerUse,-4} charges"
                + $"  {usability,-24}  {Shorten(flask.Path)}");
        }

        ReportChargeCost(belt, output);
    }

    /// <summary>
    /// Prints the three places a flask's MODIFIED per-use cost could be.
    /// </summary>
    /// <remarks>
    /// WHAT THIS IS FOR. The per-use cost printed above is the BASE one - ChargesInternal is
    /// shared by every item of a base type, so a flask that rolled "15% reduced Charges per
    /// use" still reads its base 10 while the game's own tooltip says "Consumes 8 of 60
    /// Charges on use". Auto-flask's usability gate is built on that number, so it refuses a
    /// flask at 8 charges the game would let you drink.
    ///
    /// NEITHER REFERENCE ANSWERS WHERE THE REAL NUMBER IS. GameHelper2's ChargesOffsets and
    /// the AHK tool's PoE2Offsets.Charges both stop at the same two fields this reader uses.
    /// The absence of an answer there is not evidence of absence in the game, so this prints
    /// the three places it could be and lets the game settle it:
    ///
    ///   1. the item's COMPONENTS, in case one nobody reads owns the answer;
    ///   2. windows of the Charges component and of ChargesInternal, where the number would
    ///      appear as an i32 beside the ones already identified;
    ///   3. the item's resolved STATS, which are the inputs if it turns out to be computed.
    ///
    /// Read it with the flask's own tooltip open. "Consumes N of M Charges on use" is the
    /// answer; the only question is whether N is in one of these windows or in none of them.
    /// Both flasks matter, not just the modded one: the same base with and without the mod
    /// settles whether ChargesInternal is shared at all.
    /// </remarks>
    private void ReportChargeCost(FlaskBelt belt, TextWriter output)
    {
        StructDef charges = _schema.Structs["ChargesComponent"];
        int internalPtr = charges.OffsetOf("ChargesInternalPtr");
        int current = charges.OffsetOf("Current");
        int perUse = _schema.Structs["ChargesInternal"].OffsetOf("PerUseCharges");

        output.WriteLine();
        output.WriteLine("  per-use cost - the number above is the BASE, not this flask's");
        output.WriteLine("  The game's tooltip says the real one: \"Consumes N of M Charges on use\". If N");
        output.WriteLine("  is in a window below, that slot is the field to read. If N is in none of them,");
        output.WriteLine("  the game computes it and the stats are the inputs.");

        foreach (EquippedFlask flask in belt.Flasks)
        {
            output.WriteLine();
            output.WriteLine($"  slot {flask.Slot}  {Shorten(flask.Path)}  entity 0x{flask.Entity:X}");

            if (flask.Entity == 0)
            {
                output.WriteLine("    the item entity was not recorded - nothing further to read.");
                continue;
            }

            Entity? item = _entities.Read(flask.Entity);
            if (item is null)
            {
                output.WriteLine("    the item entity no longer reads - the belt moved under us.");
                continue;
            }

            // Sorted, because the set is the finding and an unstable order makes two runs
            // impossible to compare.
            output.WriteLine($"    components  {string.Join(", ", item.Components.Keys.Order(StringComparer.Ordinal))}");

            ulong component = item.Component("Charges");
            if (component == 0)
            {
                output.WriteLine("    no Charges component - this one holds none.");
                continue;
            }

            // The window starts at the component head and the marker sits on Current, so the
            // two fields already identified are visible and everything unclaimed is beside them.
            output.WriteLine($"    Charges 0x{component:X}  (Current at +0x{current:X})");
            foreach (string line in AddressPeek.Describe(_reader, component + (ulong)current, component, current, 0x30))
            {
                output.WriteLine("    " + line);
            }

            ulong internals = _reader.ReadPointer(component + (ulong)internalPtr);
            if (MemoryReaderExtensions.IsPlausiblePointer(internals))
            {
                output.WriteLine($"    ChargesInternal 0x{internals:X}  (PerUseCharges at +0x{perUse:X})");
                foreach (string line in AddressPeek.Describe(_reader, internals + (ulong)perUse, internals, perUse, 0x30))
                {
                    output.WriteLine("    " + line);
                }
            }

            ReportItemStats(flask.Entity, output);
        }
    }

    /// <summary>The item's resolved stats - the inputs, if the cost turns out to be computed.</summary>
    /// <remarks>
    /// The game's OWN answer for what the mods came to, not a recomputation, which is why it
    /// is worth printing even though it is not the cost itself. A flask carrying
    /// "15% reduced Charges per use" shows up here as local_charges_used_+% = -15.
    /// </remarks>
    private void ReportItemStats(ulong entity, TextWriter output)
    {
        if (_items is null)
        {
            output.WriteLine("    stats: the schema has no item structs, so they cannot be read.");
            return;
        }

        InspectedItem item = _items.Read(entity);
        if (item.Stats.Count == 0)
        {
            output.WriteLine("    stats: none resolved.");
            return;
        }

        output.WriteLine("    stats");
        foreach (ItemStat stat in item.Stats)
        {
            output.WriteLine($"      key {stat.Key,-6} {stat.Value,6}  {stat.Id}");
        }
    }


    /// <summary>
    /// Hunts the inventory array by SHAPE when the declared offset comes up empty.
    /// </summary>
    /// <remarks>
    /// Same approach that found the camera matrix: describe what the thing IS and search
    /// for it, rather than trusting where it was last patch. An inventory array is a vector
    /// of 0x18-byte entries whose second qword points at a struct that itself holds a
    /// plausible item-list vector - specific enough that a false positive is unlikely, and
    /// it reports the offset ready to paste into the schema.
    /// </remarks>
    private void ScanForInventoryArray(ulong serverData, TextWriter output)
    {
        StructDef array = _schema.Structs["InventoryArray"];
        int entrySize = (int)array.Constants["EntrySize"];
        int idOffset = array.OffsetOf("InventoryId");
        int ptrOffset = array.OffsetOf("InventoryPtr0");
        StructDef inventory = _schema.Structs["Inventory"];
        int itemList = inventory.OffsetOf("ItemList");
        int itemListLast = inventory.OffsetOf("ItemListLast");

        output.WriteLine();
        output.WriteLine("  scanning ServerData for an inventory-shaped vector...");
        output.WriteLine("  offset   entries  withItems  sample ids");

        int hits = 0;
        for (int offset = 0; offset <= 0x2000 && hits < 12; offset += 8)
        {
            ulong first = _reader.ReadPointer(serverData + (ulong)offset);
            ulong last = _reader.ReadPointer(serverData + (ulong)offset + 8);
            if (!MemoryReaderExtensions.IsPlausiblePointer(first) || last <= first)
            {
                continue;
            }

            ulong span = last - first;
            if (span % (ulong)entrySize != 0 || span > 0x4000)
            {
                continue;
            }

            long entries = (long)span / entrySize;
            if (entries is < 2 or > 256)
            {
                continue;
            }

            // Confirm by CONTENT: entries must point at structs holding real item lists.
            int withItems = 0;
            var ids = new List<int>();
            for (long i = 0; i < Math.Min(entries, 24); i++)
            {
                ulong entry = first + (ulong)(i * entrySize);
                ulong target = _reader.ReadPointer(entry + (ulong)ptrOffset);
                if (!MemoryReaderExtensions.IsPlausiblePointer(target))
                {
                    continue;
                }

                ulong itemsFirst = _reader.ReadPointer(target + (ulong)itemList);
                ulong itemsLast = _reader.ReadPointer(target + (ulong)itemListLast);
                if (MemoryReaderExtensions.IsPlausiblePointer(itemsFirst)
                    && itemsLast > itemsFirst
                    && (itemsLast - itemsFirst) % 8 == 0
                    && itemsLast - itemsFirst < 0x8000)
                {
                    withItems++;
                    if (ids.Count < 8)
                    {
                        ids.Add(_reader.Read<int>(entry + (ulong)idOffset));
                    }
                }
            }

            if (withItems >= 2)
            {
                output.WriteLine($"  +0x{offset:X4}  {entries,7}  {withItems,9}  {string.Join(", ", ids)}");
                hits++;
            }
        }

        output.WriteLine(hits == 0
            ? "  nothing inventory-shaped found - ServerData itself is the suspect."
            : "  -> set ServerDataStructure.PlayerInventories to the best offset above.");
    }

    /// <summary>Trims the common metadata prefix so a row stays readable.</summary>
    private static string Shorten(string path)
        => path.StartsWith("Metadata/Items/", StringComparison.Ordinal) ? path[15..] : path;
}
