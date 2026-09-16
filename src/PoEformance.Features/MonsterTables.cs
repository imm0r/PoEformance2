using PoEformance.Game.Entities;
using PoEformance.Game.Files;

namespace PoEformance.Features;

/// <summary>
/// The monster table, read out of the install's own files instead of out of an export.
/// </summary>
/// <remarks>
/// WHAT THIS RETIRES. data/monster-varieties.json is 2733 monsters extracted from these very
/// tables by scripts/monster-varieties.py, off CSVs somebody exported by hand. It goes stale the
/// way every snapshot does - a league's new monsters simply have no name, which reads as a lookup
/// fault rather than as an old file - and nothing in the tool can tell that it has.
///
/// WHY THE INSTALL AND NOT MEMORY, which is measured rather than preferred. Against a live 0.5.5
/// client (session-2026-09-tables-055.rec) the loader's file table names 153 tables, and FIVE of
/// the eight this needs are not among them: MonsterVarieties, MonsterTypes, BloodTypes, Tags and
/// GrantedEffects. Only MonsterResistances, Mods and Stats are, and reading three out of the
/// install and three out of memory would be two routes to keep in step for nothing. So all eight
/// are read as files, the way UniqueNames and the stat descriptions already are.
///
/// THE COLUMN LAYOUTS ARE VENDORED, from poe-tool-dev/dat-schema, and what checks them is the
/// FILE's own row size - a .dat declares it by where it puts its separator, LoadedTable.Agrees -
/// against a real install at runtime. <see cref="Say"/> reports which way it went per table, so a
/// layout that has stopped fitting says so rather than producing a table of rubbish.
///
/// ALL EIGHT AGREED on a live 0.5.5 client, 2026-09-16 - see data/monster-tables.json, which
/// carries the row counts. That is what the five install-only layouts had been waiting for.
///
/// AND THE SHIPPED EXPORT IS THE OTHER CHECK. Two entirely separate routes to the same monsters -
/// a Python tool over hand-exported CSVs, and this reader over the install's own bytes - agreeing
/// is the strongest evidence available that either is right, which is exactly the argument
/// <see cref="Game.Components.StatDescriptions.Against"/> makes for the stat wordings. On that
/// same run: 2792 monsters here against the export's 2733, with 59 the export never knew about,
/// NONE lost, and 9 of the 2733 shared paths differing on name, type or skill count - the export
/// being out of date rather than either being wrong. <see cref="Against"/> reports it rather than
/// asserting it: this runs on somebody's machine during a league, and an install that has moved on
/// from the export is the expected case.
/// </remarks>
public sealed class MonsterTables
{
    /// <summary>
    /// The modifier row that means "this slot is empty".
    /// </summary>
    /// <remarks>
    /// Mods2 is a fixed-width array and 29% of every modifier reference in the table points at
    /// this one row. Kept, a monster with one real modifier draws five blank lines under it - so
    /// the generator dropped them and so does this, or the two would disagree on 1023 references
    /// for a reason that has nothing to do with the game.
    /// </remarks>
    public const string Filler = "Nothing";

    /// <summary>
    /// How many stat slots a modifier row carries.
    /// </summary>
    /// <remarks>
    /// EIGHT, AND THE EXPORT TOOK FOUR. Mods has Stat1..Stat8 with a matching Stat1Value..Stat8Value,
    /// and scripts/monster-varieties.py reads slots one to four only. Reading all eight cannot be
    /// wrong - an unused slot is a reference to nothing and yields nothing - and where the game does
    /// use the upper four, this is the route that sees them. It is also why <see cref="Against"/>
    /// counts a monster as agreeing on its modifier IDS rather than on their stat lists.
    /// </remarks>
    public const int StatSlots = 8;

    private MonsterTables(MonsterVarieties table, IReadOnlyList<string> said)
    {
        Table = table;
        Say = said;
    }

    /// <summary>What was read, or the shipped table where it could not be.</summary>
    public MonsterVarieties Table { get; }

    /// <summary>Whether <see cref="Table"/> came from the install rather than from the export.</summary>
    public bool FromGame { get; private init; }

    /// <summary>One line per table about where it came from and whether its layout held.</summary>
    public IReadOnlyList<string> Say { get; }

    /// <summary>
    /// Reads the eight tables out of the install and assembles them, or says why it could not.
    /// </summary>
    /// <remarks>
    /// NEVER THROWS AND NEVER HALF-ANSWERS. MonsterVarieties itself is the only table that must
    /// read: without it there is nothing to key anything on, and the shipped export stands. The
    /// other seven are each optional and independent - without one, the numbers it would have
    /// named stay numbers, which is precisely what the export did before those tables were
    /// available.
    /// </remarks>
    /// <param name="files">The opened install, or null where there is none.</param>
    /// <param name="layouts">The vendored column lists - data/monster-tables.json.</param>
    /// <param name="shipped">What stands when this cannot read, and what the result is compared to.</param>
    public static MonsterTables Read(GameFiles? files, QuestTableLayouts? layouts, MonsterVarieties? shipped)
    {
        MonsterVarieties standing = shipped ?? MonsterVarieties.Empty;

        // THE TWO WAYS OF HAVING NOTHING TO READ ARE SAID APART, because this whole reader is
        // diagnosed from these lines and they point at different things: no install is a machine
        // without the game beside it, and no layouts is data/monster-tables.json missing from a
        // build that has one. "No install" printed on a machine that plainly has one is the kind
        // of line that sends somebody looking in the wrong place.
        if (files is null)
        {
            return new MonsterTables(standing, ["monsters: no install to read, so the shipped table stands"]);
        }

        if (layouts is null)
        {
            return new MonsterTables(
                standing,
                ["monsters: data/monster-tables.json did not load, so the shipped table stands"]);
        }

        var said = new List<string>();

        // ARRAY WORD ORDER IS MEASURED OFF Tags, which is the column most likely to be filled:
        // 21133 references across the table. DetectArrays on an empty column decides nothing and
        // leaves every array read pointing at the wrong half of its own header.
        (LoadedTable? monsters, string monstersWhy) = QuestTables.Open(
            files, layouts, "MonsterVarieties", "Tags", "Id");

        said.Add("  MonsterVarieties   " + (monsters?.Say ?? monstersWhy));

        if (monsters is not { Usable: true })
        {
            said.Insert(0, "monsters: MonsterVarieties did not read, so the shipped table stands");
            return new MonsterTables(standing, said);
        }

        (LoadedTable? types, _) = Optional(files, layouts, "MonsterTypes", "MonsterResistances", said);
        (LoadedTable? resistances, _) = Optional(files, layouts, "MonsterResistances", null, said);
        (LoadedTable? blood, _) = Optional(files, layouts, "BloodTypes", null, said);
        (LoadedTable? tags, _) = Optional(files, layouts, "Tags", null, said);
        (LoadedTable? effects, _) = Optional(files, layouts, "GrantedEffects", null, said);

        // NO ARRAY COLUMN MEASURED ON Mods, and that is not an omission: nothing here reads an
        // array out of it - the stat slots and their intervals are all scalars - so the word order
        // it would establish is never used, and establishing it costs a scan of the widest table
        // in the set, sixteen thousand rows of six hundred and ninety-three bytes.
        (LoadedTable? mods, _) = Optional(files, layouts, "Mods", null, said);
        (LoadedTable? stats, _) = Optional(files, layouts, "Stats", null, said);

        var at = new Where(layouts);
        var bounds = new Bounds(
            Types: Count(types),
            Blood: Count(blood),
            Tags: Count(tags),
            Effects: Count(effects),
            Mods: Count(mods),
            Resistances: Count(resistances),
            Stats: Count(stats));

        Dictionary<string, MonsterVariety> byPath = Monsters(monsters.File, at, bounds);

        // NAMED ONLY WHERE SOMETHING POINTS AT IT, the same subsetting the export does: 8347
        // skills exist and 5362 are used, 1327 tags and 185. Every one of these dictionaries is
        // read by row number, so carrying the rows nothing references buys nothing but memory.
        Dictionary<int, string> skillNames = Named(effects, at.Id("GrantedEffects"), Rows(byPath, one => one.Effects));
        Dictionary<int, string> tagNames = Named(tags, at.Id("Tags"), Rows(byPath, one => one.Tags));
        Dictionary<int, string> bloodNames = Named(blood, at.Id("BloodTypes"), byPath.Values.Select(one => one.Blood));

        Dictionary<int, MonsterKind> kinds = Kinds(
            types, at, bounds, byPath.Values.Select(one => one.Type));

        Dictionary<int, string> resistNames = Named(
            resistances,
            at.Id("MonsterResistances"),
            kinds.Values.SelectMany(kind => kind.Resistances ?? []));

        Dictionary<int, ModifierMeaning> meanings = Modifiers(
            mods, stats, at, Rows(byPath, one => one.Mods, one => one.Mods2, one => one.SpecialMods));

        // THE FILLER ROWS GO AFTER the meanings are built rather than before, because which rows
        // are filler is something only the Mods table can say - and it says it by name.
        int dropped = DropFiller(byPath, meanings);

        MonsterVarieties table = MonsterVarieties.From(
            byPath,
            skillNames,
            meanings,
            tagNames,
            kinds,
            bloodNames,
            resistNames,
            $"the install's own tables, read {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}");

        said.Insert(
            0,
            $"monsters: {table.Count} from the install's own tables"
            + $" - {skillNames.Count} skills, {tagNames.Count} tags, {kinds.Count} types,"
            + $" {meanings.Count} modifiers, {bloodNames.Count} blood types, {resistNames.Count} resistance"
            + $" profiles, {dropped} empty modifier slots dropped");

        said.AddRange(Against(table, standing));
        return new MonsterTables(table, said) { FromGame = true };
    }

    /// <summary>
    /// How this table compares with the shipped export, which is the only check there is.
    /// </summary>
    /// <remarks>
    /// REPORTED AND NOT ASSERTED. This runs on somebody's machine, mid-league, against whatever
    /// client they have patched to - so an install that knows monsters the export does not is the
    /// EXPECTED outcome rather than a fault, and a reader that refused to run on a disagreement
    /// would refuse exactly when it is most useful. What a reader wants from these lines is the
    /// shape of the difference: a handful of new paths is a patch, and a thousand rows that share
    /// no path at all is a layout that has moved.
    ///
    /// ON THE MODIFIER IDS AND NOT THEIR STAT LISTS, deliberately: the export reads four of the
    /// eight stat slots (see <see cref="StatSlots"/>), so a monster whose modifier uses the upper
    /// four disagrees here for a reason that is this reader being MORE complete.
    /// </remarks>
    public static IReadOnlyList<string> Against(MonsterVarieties read, MonsterVarieties? shipped)
    {
        ArgumentNullException.ThrowIfNull(read);

        if (shipped is null || shipped.Count == 0)
        {
            return [$"  nothing to compare {read.Count} monsters against"];
        }

        var only = new List<string>();
        var differ = new List<string>();
        var shared = 0;

        foreach ((string path, MonsterVariety one) in read.All)
        {
            if (shipped.Find(path) is not { } had)
            {
                only.Add(path);
                continue;
            }

            shared++;

            // THE FIELDS A MISALIGNED TABLE WOULD BREAK FIRST, rather than every field: a name, a
            // type row and a skill count. An off-by-one in any of the joins moves all three, and
            // a column that has shifted moves the numbers without moving the name.
            if (!string.Equals(one.Name ?? string.Empty, had.Name ?? string.Empty, StringComparison.Ordinal)
                || one.Type != had.Type
                || one.SkillCount != had.SkillCount)
            {
                differ.Add(path);
            }
        }

        var lines = new List<string>
        {
            $"  against data/monster-varieties.json: {shared} of {read.Count} paths in both,"
            + $" {only.Count} only in the install, {shipped.Count - shared} only in the export,"
            + $" {differ.Count} of the shared ones differ on name, type or skill count",
        };

        // EIGHT RATHER THAN FOUR, because four was not enough to see the shape: the first live run
        // reported nine differences and showed four, two of which were the same trailing-space
        // fault. A drift that moved a column would report thousands, and eight is still a line
        // somebody reads rather than a wall they scroll past.
        foreach (string path in differ.Take(8))
        {
            MonsterVariety got = read.Find(path)!;
            MonsterVariety had = shipped.Find(path)!;
            lines.Add(
                $"    {path}: install says {Show(got)}, export says {Show(had)}");
        }

        foreach (string path in only.Take(2))
        {
            lines.Add($"    only in the install: {path}");
        }

        return lines;

        static string Show(MonsterVariety one)
            => $"name {(one.Name is { Length: > 0 } named ? named : "-")}, type {one.Type}, {one.SkillCount} skills";
    }

    private static (LoadedTable? Table, string Why) Optional(
        GameFiles files, QuestTableLayouts layouts, string table, string? arrayColumn, List<string> said)
    {
        (LoadedTable? loaded, string why) = QuestTables.Open(files, layouts, table, arrayColumn, "Id");
        said.Add($"  {table,-18} " + (loaded?.Say ?? why));
        return (loaded is { Usable: true } ? loaded : null, why);
    }

    /// <summary>
    /// How many rows each table a reference can point at actually has.
    /// </summary>
    /// <remarks>
    /// THE BOUND IS WHAT DISAMBIGUATES A REFERENCE. A foreignrow is two words - a row and the
    /// table's own marker - and DatReference.RowIn takes whichever of them is a valid row, so the
    /// wider the bound the more likely it takes the marker. Against the real table's row count a
    /// marker is almost always out of range; against int.MaxValue almost nothing is.
    ///
    /// A MISSING TABLE STILL GETS A NUMBER, because the export kept the row numbers whether or not
    /// it could name them and everything downstream is written that way. Its bound is int.MaxValue,
    /// which is the weakest this can be - and is why <see cref="Row"/> refuses a null first.
    /// </remarks>
    private readonly record struct Bounds(
        long Types, long Blood, long Tags, long Effects, long Mods, long Resistances, long Stats);

    private static long Count(LoadedTable? table) => table?.File.Rows ?? int.MaxValue;

    /// <summary>Every monster row, as the record the rest of the tool is written against.</summary>
    private static Dictionary<string, MonsterVariety> Monsters(DatFile file, Where at, Bounds rows)
    {
        var byPath = new Dictionary<string, MonsterVariety>(file.Rows, StringComparer.Ordinal);

        for (int row = 0; row < file.Rows; row++)
        {
            string id = Words(file, row, at.Of("MonsterVarieties", "Id"));

            // "Any" IS A SENTINEL AND NOT A MONSTER - the one Id in the table that is not a
            // metadata path, and one no entity could ever carry.
            if (!id.StartsWith("Metadata/", StringComparison.Ordinal))
            {
                continue;
            }

            string name = Words(file, row, at.Of("MonsterVarieties", "Name"));
            string built = Words(file, row, at.Of("MonsterVarieties", "BaseMonsterTypeIndex"));

            byPath[id] = new MonsterVariety(

                // A NAME IN [BRACKETS] IS A PLACEHOLDER the game shows to nobody - "[ANY MONSTER]".
                Name: name.StartsWith('[') ? null : Empty(name),
                Type: Row(file.Reference(row, at.Of("MonsterVarieties", "MonsterType")), rows.Types),
                Speed: file.I32(row, at.Of("MonsterVarieties", "MovementSpeed")),
                Size: file.I32(row, at.Of("MonsterVarieties", "ObjectSize")),
                ModelSize: file.I32(row, at.Of("MonsterVarieties", "ModelSizeMultiplier")),
                MinAttack: file.I32(row, at.Of("MonsterVarieties", "MinimumAttackDistance")),
                MaxAttack: file.I32(row, at.Of("MonsterVarieties", "MaximumAttackDistance")),
                MinAggro: file.I32(row, at.Of("MonsterVarieties", "MinAgroRange")),
                MaxAggro: file.I32(row, at.Of("MonsterVarieties", "MaxAgroRange")),
                Xp: file.I32(row, at.Of("MonsterVarieties", "ExperienceMultiplier")),
                Damage: file.I32(row, at.Of("MonsterVarieties", "DamageMultiplier")),
                Life: file.I32(row, at.Of("MonsterVarieties", "LifeMultiplier")),
                AttackSpeed: file.I32(row, at.Of("MonsterVarieties", "AttackSpeed")),

                // BOTH OF THESE ARE FLOATS IN THE FILE, and read as ints they are bit patterns:
                // AttackCrit's 0/1/2 would come back as 0 and 1065353216. Crit is a KIND rather
                // than a chance - 0, 1 or 2 across the whole table - so it is rounded to the
                // number it is rather than carried as a fraction of something.
                Crit: (int)MathF.Round(file.F32(row, at.Of("MonsterVarieties", "AttackCrit"))),
                Blood: Row(file.Reference(row, at.Of("MonsterVarieties", "BloodType")), rows.Blood),

                // QuestFlags IS NOT ONE OF THE EIGHT TABLES READ HERE, so this one is bounded by
                // nothing - the flag's own state is read from the game at runtime by QuestWatch,
                // which is also what refuses a row number that table cannot hold.
                Quest: Row(file.Reference(row, at.Of("MonsterVarieties", "Questflag")), int.MaxValue),
                Poise: file.F32(row, at.Of("MonsterVarieties", "PoiseThreshold")),
                Stance: Empty(Words(file, row, at.Of("MonsterVarieties", "Stance"))),
                Boss: file.Bool(row, at.Of("MonsterVarieties", "BossHealthBar")),

                // A base equal to the monster's own id says nothing, which is why the export
                // drops it: 2191 rows name something else and the rest name themselves.
                Base: built.Length > 0 && !string.Equals(built, id, StringComparison.Ordinal) ? built : null,
                Tags: List(file, row, at.Of("MonsterVarieties", "Tags"), rows.Tags),
                Effects: List(file, row, at.Of("MonsterVarieties", "GrantedEffects"), rows.Effects),
                Mods: List(file, row, at.Of("MonsterVarieties", "Mods"), rows.Mods),
                Mods2: List(file, row, at.Of("MonsterVarieties", "Mods2"), rows.Mods),
                SpecialMods: List(file, row, at.Of("MonsterVarieties", "Special_Mods"), rows.Mods),
                Inherits: Paths(file, row, at.Of("MonsterVarieties", "InheritsFrom")));
        }

        return byPath;
    }

    /// <summary>The defensive block on each type row anything points at.</summary>
    private static Dictionary<int, MonsterKind> Kinds(
        LoadedTable? types, Where at, Bounds rows, IEnumerable<int> wanted)
    {
        var kinds = new Dictionary<int, MonsterKind>();
        if (types is null)
        {
            return kinds;
        }

        foreach (int row in new HashSet<int>(wanted))
        {
            if (row < 0 || row >= types.File.Rows)
            {
                continue;
            }

            string id = Words(types.File, row, at.Of("MonsterTypes", "Id"));
            if (id.Length == 0)
            {
                continue;
            }

            // ROW ZERO IS DROPPED FROM THE RESISTANCE LIST, as the export drops it: it is the
            // table's own "no profile" row, and carrying it names every unprotected monster after
            // whatever happens to sit first.
            int[] resists = [.. types.File
                .References(row, at.Of("MonsterTypes", "MonsterResistances"))
                .Select(one => one.RowIn(rows.Resistances))
                .Where(one => one > 0)];

            kinds[row] = new MonsterKind(
                id,
                types.File.I32(row, at.Of("MonsterTypes", "Armour")),
                types.File.I32(row, at.Of("MonsterTypes", "Evasion")),
                types.File.I32(row, at.Of("MonsterTypes", "EnergyShieldFromLife")),
                types.File.I32(row, at.Of("MonsterTypes", "DamageSpread")),
                types.File.Bool(row, at.Of("MonsterTypes", "IsSummoned")),
                resists.Length > 0 ? resists : null);
        }

        return kinds;
    }

    /// <summary>What each modifier row anything points at is called, and which stats it sets.</summary>
    private static Dictionary<int, ModifierMeaning> Modifiers(
        LoadedTable? mods, LoadedTable? stats, Where at, IEnumerable<int> wanted)
    {
        var meanings = new Dictionary<int, ModifierMeaning>();
        if (mods is null)
        {
            return meanings;
        }

        foreach (int row in new HashSet<int>(wanted))
        {
            if (row < 0 || row >= mods.File.Rows)
            {
                continue;
            }

            string id = Words(mods.File, row, at.Of("Mods", "Id"));
            if (id.Length == 0)
            {
                continue;
            }

            var carried = new List<ModifierStat>(StatSlots);
            for (int slot = 1; slot <= StatSlots && stats is not null; slot++)
            {
                int statRow = mods.File.Reference(row, at.Of("Mods", $"Stat{slot}")).RowIn(stats.File.Rows);
                if (statRow < 0)
                {
                    continue;
                }

                string stat = Words(stats.File, statRow, at.Of("Stats", "Id"));
                if (stat.Length == 0)
                {
                    continue;
                }

                // AN INTERVAL IS TWO i32s AND NOT ONE, which is the rule that made this whole
                // layout come out right: priced as a single column, Mods is thirty-two bytes
                // short and every offset after Stat1Value is wrong. See DatColumn.Interval.
                int values = at.Of("Mods", $"Stat{slot}Value");
                carried.Add(new ModifierStat(stat, mods.File.I32(row, values), mods.File.I32(row, values + 4)));
            }

            meanings[row] = new ModifierMeaning(id, carried.Count > 0 ? carried : null);
        }

        return meanings;
    }

    /// <summary>Takes the empty slots out of every monster's modifier lists. Says how many.</summary>
    private static int DropFiller(
        Dictionary<string, MonsterVariety> byPath, Dictionary<int, ModifierMeaning> meanings)
    {
        var empty = new HashSet<int>(
            meanings.Where(one => one.Value.Id == Filler).Select(one => one.Key));

        if (empty.Count == 0)
        {
            return 0;
        }

        foreach (int row in empty)
        {
            meanings.Remove(row);
        }

        var dropped = 0;
        foreach ((string path, MonsterVariety one) in byPath)
        {
            IReadOnlyList<int>? mods = Without(one.Mods, empty, ref dropped);
            IReadOnlyList<int>? mods2 = Without(one.Mods2, empty, ref dropped);
            IReadOnlyList<int>? special = Without(one.SpecialMods, empty, ref dropped);

            if (!ReferenceEquals(mods, one.Mods)
                || !ReferenceEquals(mods2, one.Mods2)
                || !ReferenceEquals(special, one.SpecialMods))
            {
                byPath[path] = one with { Mods = mods, Mods2 = mods2, SpecialMods = special };
            }
        }

        return dropped;
    }

    private static IReadOnlyList<int>? Without(IReadOnlyList<int>? rows, HashSet<int> empty, ref int dropped)
    {
        if (rows is null || !rows.Any(empty.Contains))
        {
            return rows;
        }

        int[] kept = [.. rows.Where(row => !empty.Contains(row))];
        dropped += rows.Count - kept.Length;
        return kept.Length > 0 ? kept : null;
    }

    /// <summary>Row number to the Id column of whatever table, for the rows something points at.</summary>
    private static Dictionary<int, string> Named(LoadedTable? table, int idAt, IEnumerable<int> wanted)
    {
        var named = new Dictionary<int, string>();
        if (table is null || idAt < 0)
        {
            return named;
        }

        foreach (int row in new HashSet<int>(wanted))
        {
            if (row < 0 || row >= table.File.Rows)
            {
                continue;
            }

            if (Words(table.File, row, idAt) is { Length: > 0 } id)
            {
                named[row] = id;
            }
        }

        return named;
    }

    private static IEnumerable<int> Rows(
        Dictionary<string, MonsterVariety> byPath,
        params Func<MonsterVariety, IReadOnlyList<int>?>[] columns)
        => byPath.Values.SelectMany(one => columns.SelectMany(column => column(one) ?? []));

    /// <summary>An array of foreign references as the row numbers they point at.</summary>
    private static IReadOnlyList<int>? List(DatFile file, int row, int offset, long inTable)
    {
        if (offset < 0)
        {
            return null;
        }

        int[] rows = [.. file.References(row, offset).Select(one => one.RowIn(inTable)).Where(one => one >= 0)];
        return rows.Length > 0 ? rows : null;
    }

    /// <summary>An array of strings - eight bytes each, an offset into the variable section.</summary>
    private static IReadOnlyList<string>? Paths(DatFile file, int row, int offset)
    {
        if (offset < 0)
        {
            return null;
        }

        string[] paths = [.. file
            .References(row, offset, elementWidth: 8)
            .Select(one => file.TextAt(one.First).Trim())
            .Where(one => one.Length > 0)];

        return paths.Length > 0 ? paths : null;
    }

    /// <summary>
    /// A text column, trimmed.
    /// </summary>
    /// <remarks>
    /// THE GAME'S OWN DATA HAS TRAILING SPACES IN IT, which is not a guess: read against a live
    /// 0.5.5 install, two of the nine monsters this route and the export disagree about are
    /// "Gulzal, the Living Furnace " - the difference is one space at the end, and the export's
    /// generator trims where this did not. A trailing space carries nothing and costs plenty: it
    /// is invisible on screen, it breaks a sort, it breaks a search for the name somebody can see,
    /// and it reports a difference between two tables that hold the same name.
    /// </remarks>
    private static string Words(DatFile file, int row, int offset) => file.Text(row, offset).Trim();

    private static string? Empty(string text) => text.Length > 0 ? text : null;

    /// <summary>
    /// A foreign reference as a plain row number, or zero where it points at nothing.
    /// </summary>
    /// <remarks>
    /// MINUS ONE AND NOT ZERO, which is the opposite of what the record's own default suggests
    /// and is forced by the data: row zero of BloodTypes is "Blood" and 1092 monsters carry it,
    /// so a null written as zero is not a missing answer - it is the commonest real one, applied
    /// to a monster that has none. Every lookup this feeds simply misses on -1.
    ///
    /// The export cannot make that distinction at all: a null arrives from the CSV as an empty
    /// cell, the field is left out, and the record defaults to zero. It never bites there because
    /// no monster in the shipped table has a null type or blood - which is a fact about that
    /// export rather than a guarantee about the next one.
    /// </remarks>
    private static int Row(DatReference reference, long inTable) => reference.RowIn(inTable);

    /// <summary>The offsets, resolved once and asked for by name.</summary>
    /// <remarks>
    /// OffsetOf walks the table's column list to add up the widths in front of the one it wants,
    /// so asking it per row per column is a walk of 127 entries two and a half thousand times over.
    /// Every offset here is computed on first use and kept.
    /// </remarks>
    private sealed class Where(QuestTableLayouts layouts)
    {
        private readonly Dictionary<(string Table, string Column), int> _found = [];

        public int Of(string table, string column)
        {
            if (_found.TryGetValue((table, column), out int at))
            {
                return at;
            }

            at = layouts.OffsetOf(table, column);
            _found[(table, column)] = at;
            return at;
        }

        public int Id(string table) => Of(table, "Id");
    }
}
