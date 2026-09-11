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
    /// <summary>How far into each component the value hunt looks.</summary>
    /// <remarks>
    /// A component is a handful of fields behind a vtable, and the ones already mapped put
    /// everything interesting inside the first few dozen bytes - Charges ends at +0x40. Far
    /// enough to cover an unmapped one, short enough that eight components a flask stay a
    /// few hundred reads rather than a scan.
    /// </remarks>
    private const int HuntBytes = 0x100;

    /// <summary>And how far into each structure a component points at.</summary>
    private const int FollowBytes = 0x80;

    /// <summary>
    /// Hits printed before the rest are counted instead.
    /// </summary>
    /// <remarks>
    /// A small candidate is noisy once pointers are followed - 8 is one of the commonest
    /// values in any heap - and a hundred lines of it would bury the one hit that matters.
    /// The count still says how many there were.
    /// </remarks>
    private const int MostHits = 24;

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
    /// WHAT THIS IS FOR. Everything in ChargesInternal is a BASE value - the base type's
    /// numbers, never the item's own rolls. A flask that rolled "15% reduced Charges per use"
    /// still reads its base 10 while the game's tooltip says "Consumes 8 of 60 Charges on
    /// use", and a flask with +27% maximum charges reads its base 70 while sitting full at 88.
    /// Auto-flask's usability gate is built on the per-use number, so it refuses a flask at 8
    /// charges the game would let you drink.
    ///
    /// NEITHER REFERENCE ANSWERS WHERE THE REAL NUMBERS ARE. GameHelper2's ChargesOffsets and
    /// the AHK tool's PoE2Offsets.Charges both stop at the same two fields the belt reader
    /// uses, and neither project has ever heard of the Flask or LocalStats components at all.
    /// The absence of an answer there is not evidence of absence in the game, so this looks:
    ///
    ///   1. the item's COMPONENTS, which is what named Flask and LocalStats in the first place;
    ///   2. windows of Charges, ChargesInternal, and those two unread components;
    ///   3. a HUNT for the modified numbers by value, across every component the item has -
    ///      see <see cref="ReportValueHunt"/>, which is what finds the field wherever it is;
    ///   4. the item's resolved STATS, which are the inputs if it turns out to be computed.
    ///
    /// Read it with the flask's own tooltip open: "Consumes N of M Charges on use" is the
    /// answer, and the hunt says whether N and M are anywhere on the item.
    /// </remarks>
    private void ReportChargeCost(FlaskBelt belt, TextWriter output)
    {
        StructDef charges = _schema.Structs["ChargesComponent"];
        int internalPtr = charges.OffsetOf("ChargesInternalPtr");
        int current = charges.OffsetOf("Current");
        StructDef internals = _schema.Structs["ChargesInternal"];
        int perUse = internals.OffsetOf("PerUseCharges");
        int maxCharges = internals.OffsetOf("MaxCharges");

        output.WriteLine();
        output.WriteLine("  charge costs - the numbers above are the BASE TYPE's, not this flask's");
        output.WriteLine("  The tooltip says the real ones: \"Consumes N of M Charges on use\". The hunt");
        output.WriteLine("  below says whether N and M are anywhere on the item; if they are in none of");
        output.WriteLine("  its components, the game computes them and the stats are the inputs.");
        output.WriteLine();
        output.WriteLine("  ONE READING CANNOT TELL A COUNT FROM A MAXIMUM. Every slot holding the charge");
        output.WriteLine("  count is listed under \"control ok\" below, and on a FULL flask the current");
        output.WriteLine("  count and the flask's real maximum are the same number - so they read");
        output.WriteLine("  identically however long you stare at them. Drinking separates them in one");
        output.WriteLine("  action: run --flaskwatch, drink, and the slot that DROPS is the count while");
        output.WriteLine("  one that STAYS is the maximum.");

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
            //
            // OUT TO +0x88, because the first run of this stopped at +0x48 and the hunt then
            // found the charge count AGAIN at +0x58 - a field nothing maps, in the window's
            // blind spot. Whatever that slot is, it is the best lead there is: see the note
            // ReportChargeCost prints about telling it apart from Current.
            output.WriteLine($"    Charges 0x{component:X}  (Current at +0x{current:X})");
            foreach (string line in AddressPeek.Describe(_reader, component + (ulong)current, component, current, 0x70))
            {
                output.WriteLine("    " + line);
            }

            int baseMax = 0;
            int basePerUse = 0;
            ulong descriptor = _reader.ReadPointer(component + (ulong)internalPtr);
            if (MemoryReaderExtensions.IsPlausiblePointer(descriptor))
            {
                baseMax = _reader.Read<int>(descriptor + (ulong)maxCharges);
                basePerUse = _reader.Read<int>(descriptor + (ulong)perUse);

                output.WriteLine($"    ChargesInternal 0x{descriptor:X}"
                    + $"  (MaxCharges {baseMax} at +0x{maxCharges:X}, PerUseCharges {basePerUse} at +0x{perUse:X})");
                foreach (string line in AddressPeek.Describe(_reader, descriptor + (ulong)perUse, descriptor, perUse, 0x30))
                {
                    output.WriteLine("    " + line);
                }
            }

            // The two the component walk named and nothing in either reference has ever read.
            // Whole windows rather than a hunt, because an unmapped struct is worth looking at
            // even once the hunt says the number is not in it.
            foreach (string unread in new[] { "Flask", "LocalStats" })
            {
                ulong at = item.Component(unread);
                if (at == 0)
                {
                    continue;
                }

                output.WriteLine($"    {unread} 0x{at:X}  (nothing maps this yet)");
                foreach (string line in AddressPeek.Describe(_reader, at, at, 0, 0x60))
                {
                    output.WriteLine("    " + line);
                }
            }

            IReadOnlyList<ItemStat> stats = ReportItemStats(flask.Entity, output);

            // The current charge count is the CONTROL: it is a number this walk already read,
            // it lives at Charges+0x18, and that is inside the range the hunt sweeps. So a hunt
            // that cannot find it is broken, and says so in its own output rather than
            // reporting a confident "nowhere" that would send somebody down the wrong path.
            ReportValueHunt(item, Wanted(basePerUse, baseMax, stats), flask.Charges, output);
        }
    }

    /// <summary>
    /// The numbers a modified flask WOULD hold, to hunt the item for.
    /// </summary>
    /// <remarks>
    /// NOT A CLAIM ABOUT THE GAME'S ARITHMETIC, and that distinction is what makes this
    /// acceptable where computing the cost for display would not be. These are values to LOOK
    /// FOR: a wrong one costs a missed hit, never a wrong answer, and the memory decides.
    ///
    /// Both roundings go in for the same reason. The one sample in hand is 10 x 0.85 = 8.5
    /// shown as 8, which rules out rounding up and leaves floor and half-to-even
    /// indistinguishable - so the hunt asks for both and reports which one is actually there.
    /// </remarks>
    private static IReadOnlyList<int> Wanted(int basePerUse, int baseMax, IReadOnlyList<ItemStat> stats)
    {
        var wanted = new SortedSet<int>();
        foreach (ItemStat stat in stats)
        {
            // Matched on the stat id's shape rather than on a table of them, because the point
            // is coverage of whatever a flask happens to carry.
            (int from, bool percent) = stat.Id switch
            {
                var id when id.Contains("charges_used", StringComparison.Ordinal) => (basePerUse, true),
                var id when id.Contains("extra_max_charges", StringComparison.Ordinal) => (baseMax, false),
                var id when id.Contains("max_charges", StringComparison.Ordinal) => (baseMax, true),
                _ => (0, false),
            };

            if (from <= 0)
            {
                continue;
            }

            if (!percent)
            {
                wanted.Add(from + stat.Value);
                continue;
            }

            double scaled = from * (1.0 + (stat.Value / 100.0));
            wanted.Add((int)Math.Floor(scaled));
            wanted.Add((int)Math.Round(scaled, MidpointRounding.AwayFromZero));

            // AND THE SAME NUMBER AT FINER GRANULARITY, which is not a wild guess about
            // encodings: this game genuinely stores flask quantities in tenths - its own stat
            // table has local_flask_deciseconds_to_recover. A cost the game keeps as 85 and
            // renders as 8 would be invisible to a hunt that only asks for 8.
            wanted.Add((int)Math.Round(scaled * 10, MidpointRounding.AwayFromZero));
            wanted.Add((int)Math.Round(scaled * 100, MidpointRounding.AwayFromZero));
        }

        // The bases themselves are never the finding - they are what is already read.
        wanted.Remove(basePerUse);
        wanted.Remove(baseMax);
        return [.. wanted];
    }

    /// <summary>
    /// Hunts every component of the item for the values asked for.
    /// </summary>
    /// <remarks>
    /// THE PART THAT ANSWERS THE QUESTION WHEREVER THE FIELD TURNS OUT TO BE. Dumping windows
    /// only helps for a struct somebody already suspects; this asks "is this number anywhere on
    /// this item", which is the actual question and does not need the answer guessed first.
    ///
    /// Four-byte reads on a four-byte stride, so a value straddling the middle of a qword is
    /// found too - ChargesInternal writes every field as two int32s in one qword, so that is
    /// not a hypothetical shape here.
    ///
    /// IT CARRIES ITS OWN CONTROL, because the dangerous answer here is the confident negative.
    /// "NOWHERE on this item" is what would send somebody off to reimplement the game's
    /// arithmetic, and a hunt that quietly found nothing - wrong range, wrong stride, a
    /// component list that came back empty - produces exactly that sentence. So a value this
    /// walk has ALREADY read, at an offset inside the swept range, goes in beside the ones
    /// being looked for, and a run that misses it says the hunt is broken instead.
    /// </remarks>
    private void ReportValueHunt(Entity item, IReadOnlyList<int> wanted, int control, TextWriter output)
    {
        if (wanted.Count == 0)
        {
            output.WriteLine("    hunt: nothing to look for - no charge-modifying stat on this one.");
            return;
        }

        var hits = new List<string>();
        var controls = new List<string>();
        foreach ((string name, ulong at) in item.Components.OrderBy(one => one.Key, StringComparer.Ordinal))
        {
            if (!MemoryReaderExtensions.IsPlausiblePointer(at))
            {
                continue;
            }

            Sweep(name, at, HuntBytes, wanted, control, hits, controls);

            // ONE LEVEL DEEPER. A component is mostly pointers - LocalStats keeps the item's
            // own stats in a vector hung off +0x20, not inline - so sweeping only the
            // component bodies asks a narrower question than "is this number on this item".
            for (int offset = 0; offset < HuntBytes; offset += 8)
            {
                ulong target = _reader.ReadPointer(at + (ulong)offset);

                // Skip the module: every component starts with a vtable and carries more
                // function pointers, and following those sweeps the game's own code for a
                // number, which is all noise.
                if (!MemoryReaderExtensions.IsPlausiblePointer(target)
                    || (target >= _reader.ModuleBase && target < _reader.ModuleBase + _reader.ModuleSize))
                {
                    continue;
                }

                Sweep($"{name}+0x{offset:X}->", target, FollowBytes, wanted, control, hits, controls);
            }
        }

        output.WriteLine($"    hunt for {string.Join(", ", wanted)}");
        output.WriteLine(hits.Count == 0
            ? "      NOWHERE on this item, one level of pointers included."
            : "      " + string.Join("  |  ", hits.Take(MostHits))
                + (hits.Count > MostHits ? $"  (+{hits.Count - MostHits} more)" : string.Empty));

        // COUNTED INDEPENDENTLY of the hits, which is a correction rather than a nicety: the
        // first version reported a hit OR a control per slot, and on a full flask whose
        // modified maximum equals its current charges the two are the SAME NUMBER - so the
        // control read as failed while the hunt had in fact worked perfectly.
        output.WriteLine(controls.Count == 0
            ? $"      CONTROL FAILED: {control} charges is on this item and the hunt missed it,"
                + " so read the line above as \"the hunt does not work\", not as an answer."
            : $"      control ok: found the {control} charges it already holds at"
                + $" {string.Join(", ", controls.Take(MostHits))}");
    }

    /// <summary>Reads one region four bytes at a time, collecting whatever it was asked for.</summary>
    private void Sweep(
        string label,
        ulong at,
        int bytes,
        IReadOnlyList<int> wanted,
        int control,
        List<string> hits,
        List<string> controls)
    {
        for (int offset = 0; offset < bytes; offset += 4)
        {
            if (!_reader.TryRead(at + (ulong)offset, out int found))
            {
                continue;
            }

            if (wanted.Contains(found))
            {
                hits.Add($"{label}+0x{offset:X} = {found}");
            }

            if (found == control)
            {
                controls.Add($"{label}+0x{offset:X}");
            }
        }
    }

    /// <summary>
    /// Samples every flask's Charges component while somebody drinks, and prints what moved.
    /// </summary>
    /// <remarks>
    /// THE ONE QUESTION A SINGLE READING CANNOT ANSWER. The charge count turns up at more than
    /// one offset in the Charges component - Current at +0x18, and something nothing maps at
    /// +0x58 - and on a FULL flask the current count and the flask's real maximum are the same
    /// number, so every slot holding either reads identically. No amount of staring at one
    /// snapshot separates them.
    ///
    /// Drinking separates them in one action: the current count drops, a maximum does not. So
    /// this is the <c>--peekwatch</c> protocol aimed at the belt - "do the thing in the game and
    /// the slots that moved will print" - and it replaces asking somebody to run the report
    /// twice and diff two walls of hex by eye, which is what this needed before and is a
    /// miserable way to answer a question.
    ///
    /// THE ADDRESSES ARE RESOLVED ONCE and then sampled, deliberately: re-walking the belt every
    /// tick would cost an entity read per flask per sample, and the components do not move while
    /// the items sit in the belt. What DOES invalidate them is rearranging the belt, so a read
    /// that starts failing is reported rather than silently counted as "no change".
    /// </remarks>
    public void Watch(ulong gameStatesStatic, TextWriter output, Func<bool> stop, int sampleMs = 100)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(stop);

        int current = _schema.Structs["ChargesComponent"].OffsetOf("Current");

        var watching = new List<(int Slot, string Path, ulong At, int Slots, ulong?[] Last, AddressPeek.PeekWatchLog Log)>();
        foreach (EquippedFlask flask in Belt(gameStatesStatic))
        {
            ulong component = _entities.Read(flask.Entity)?.Component("Charges") ?? 0;
            if (component == 0)
            {
                continue;
            }

            // From the component head out past +0x58, which is the slot this exists to identify.
            (ulong start, int slots, _) = AddressPeek.Window(component + (ulong)current, current, 0x70);
            watching.Add((flask.Slot, Shorten(flask.Path), start, slots, AddressPeek.Sample(_reader, start, slots), new AddressPeek.PeekWatchLog()));
        }

        output.WriteLine();
        if (watching.Count == 0)
        {
            output.WriteLine("  flask watch: no flask with a Charges component - nothing to sample.");
            return;
        }

        output.WriteLine("  flask watch - DRINK A FLASK NOW, then press any key.");
        output.WriteLine("  The slot that DROPS is the current count. One that STAYS while another drops");
        output.WriteLine("  is this flask's real maximum, which is the number the belt display is missing.");
        output.WriteLine();

        while (!stop())
        {
            Thread.Sleep(sampleMs);

            for (int i = 0; i < watching.Count; i++)
            {
                (int slot, string path, ulong at, int slots, ulong?[] last, AddressPeek.PeekWatchLog log) = watching[i];
                ulong?[] sample = AddressPeek.Sample(_reader, at, slots);

                foreach (AddressPeek.SlotChange change in log.Observe(last, sample))
                {
                    if (change.Print)
                    {
                        output.WriteLine($"  slot {slot}  " + AddressPeek.Line(_reader, at + (ulong)(change.Slot * 8), at, change.Before, change.After));
                    }
                }

                watching[i] = (slot, path, at, slots, sample, log);
            }
        }

        foreach ((int slot, string path, ulong at, _, _, AddressPeek.PeekWatchLog log) in watching)
        {
            output.WriteLine();
            output.WriteLine($"  slot {slot}  {path}");
            foreach (string line in log.Summary(at, at))
            {
                output.WriteLine("  " + line);
            }
        }
    }

    /// <summary>The belt, or nothing when the chain does not reach it.</summary>
    private IReadOnlyList<EquippedFlask> Belt(ulong gameStatesStatic)
    {
        GameChainAddresses chain = GameChain.Resolve(_reader, _schema, gameStatesStatic);
        if (!chain.InGame)
        {
            return [];
        }

        ulong localPlayerStruct = chain.AreaInstance + (ulong)_schema.Structs["AreaInstance"].OffsetOf("PlayerInfo");
        ulong serverData = _reader.ReadPointer(
            localPlayerStruct + (ulong)_schema.Structs["LocalPlayerStruct"].OffsetOf("ServerDataPtr"));

        return new FlaskBeltReader(_reader, _schema).Read(serverData).Flasks;
    }

    /// <summary>The item's resolved stats - the inputs, if the cost turns out to be computed.</summary>
    /// <remarks>
    /// The game's OWN answer for what the mods came to, not a recomputation, which is why it
    /// is worth printing even though it is not the cost itself. A flask carrying
    /// "15% reduced Charges per use" shows up here as local_charges_used_+% = -15.
    /// </remarks>
    private IReadOnlyList<ItemStat> ReportItemStats(ulong entity, TextWriter output)
    {
        if (_items is null)
        {
            output.WriteLine("    stats: the schema has no item structs, so they cannot be read.");
            return [];
        }

        InspectedItem item = _items.Read(entity);
        if (item.Stats.Count == 0)
        {
            output.WriteLine("    stats: none resolved.");
            return item.Stats;
        }

        output.WriteLine("    stats");
        foreach (ItemStat stat in item.Stats)
        {
            output.WriteLine($"      key {stat.Key,-6} {stat.Value,6}  {stat.Id}");
        }

        return item.Stats;
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
