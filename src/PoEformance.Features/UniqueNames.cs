using PoEformance.Game.Files;

namespace PoEformance.Features;

/// <summary>
/// What a unique item is called, joined out of the install's own three tables.
/// </summary>
/// <remarks>
/// WHAT THIS RETIRES. data/unique_ivi_name_map.tsv is 400-odd pairs of "ItemVisualIdentity id,
/// unique name", extracted from these very tables by the AHK tool's scripts. Like the base types
/// it goes stale by OMISSION rather than by pointing at the wrong thing - the key is the art's own
/// id, which the game does not renumber - so a league's new uniques simply keep their base name
/// until somebody re-exports.
///
/// WHY THE INSTALL RATHER THAN MEMORY, which is not a preference. Measured against a live 0.5.5
/// client (session-2026-09-tables-055.rec): the loader's file table names 153 tables and NONE of
/// these three is among them - not UniqueStashLayout, not Words, not ItemVisualIdentity. The route
/// that reached BaseItemTypes and Mods does not reach here at all, so the files are read instead,
/// the way QuestTables and the stat descriptions already do.
///
/// THE JOIN, AND WHY IT GOES THIS WAY ROUND. A unique's name is not on the item and not on its
/// base type: UniqueStashLayout has a row per unique with a WordsKey and an ItemVisualIdentityKey,
/// Words holds the name as Text, and ItemVisualIdentity's Id is what the game exposes on the item
/// itself. So the art is the key. That is also what tells apart two uniques sharing one base -
/// Morior Invictus and Tabula Rasa are the same path - and what keeps a localised client pricing
/// in English, because an Id is an engine identifier and a name is translated.
///
/// WHAT IS NOT CHECKED AGAINST THE GAME, said plainly: no client row size can confirm these
/// layouts, because the walk does not name the tables. The check is the FILE's own row size, which
/// a .dat declares by where it puts its separator - LoadedTable.Agrees - and it happens at runtime
/// against a real install. <see cref="Say"/> reports which way it went for each of the three.
/// </remarks>
public sealed class UniqueNames
{
    /// <summary>The wordlist a unique's name is in. Words holds every kind of name in one table.</summary>
    /// <remarks>
    /// SIX, and it is the extractor's own number rather than a guess: build-item-names counts
    /// "Wordlist 6 (unique names)" and builds the shipped map from exactly those rows. It is not
    /// used to FILTER here - the layout rows point at their own words - but it is what a reader
    /// checks against when the count comes out wrong.
    /// </remarks>
    public const int UniqueWordlist = 6;

    private UniqueNames(IReadOnlyDictionary<string, string> byArt, IReadOnlyList<string> said)
    {
        ByArt = byArt;
        Say = said;
    }

    /// <summary>Art id to unique name, which is the pair the shipped file carries.</summary>
    public IReadOnlyDictionary<string, string> ByArt { get; }

    /// <summary>One line per table about where it came from and whether its layout held.</summary>
    public IReadOnlyList<string> Say { get; }

    /// <summary>
    /// Reads the three tables out of the install and joins them, or says why it could not.
    /// </summary>
    /// <remarks>
    /// NEVER THROWS AND NEVER HALF-ANSWERS. A missing install, a table that will not parse or a
    /// layout that no longer fits all end the same way - an empty map and a line saying so - and
    /// the caller keeps the shipped file, which is what it had before.
    /// </remarks>
    public static UniqueNames Read(GameFiles? files, QuestTableLayouts? layouts)
    {
        if (files is null || layouts is null)
        {
            return new UniqueNames(
                new Dictionary<string, string>(StringComparer.Ordinal),
                ["unique names: no install to read, so the shipped map stands"]);
        }

        var said = new List<string>();

        (LoadedTable? layout, string layoutWhy) = QuestTables.Open(
            files, layouts, "UniqueStashLayout", null);
        (LoadedTable? words, string wordsWhy) = QuestTables.Open(
            files, layouts, "Words", null, "Text");
        (LoadedTable? art, string artWhy) = QuestTables.Open(
            files, layouts, "ItemVisualIdentity", null, "Id");

        said.Add("  UniqueStashLayout  " + (layout?.Say ?? layoutWhy));
        said.Add("  Words              " + (words?.Say ?? wordsWhy));
        said.Add("  ItemVisualIdentity " + (art?.Say ?? artWhy));

        if (layout is not { Usable: true } || words is not { Usable: true } || art is not { Usable: true })
        {
            said.Insert(0, "unique names: one of the three tables did not read, so the shipped map stands");
            return new UniqueNames(new Dictionary<string, string>(StringComparer.Ordinal), said);
        }

        int wordsAt = layouts.OffsetOf("UniqueStashLayout", "WordsKey");
        int artAt = layouts.OffsetOf("UniqueStashLayout", "ItemVisualIdentityKey");
        int alternateAt = layouts.OffsetOf("UniqueStashLayout", "IsAlternateArt");
        int textAt = layouts.OffsetOf("Words", "Text");
        int idAt = layouts.OffsetOf("ItemVisualIdentity", "Id");

        if (wordsAt < 0 || artAt < 0 || alternateAt < 0 || textAt < 0 || idAt < 0)
        {
            said.Insert(0, "unique names: a column the join needs is not in the vendored layout");
            return new UniqueNames(new Dictionary<string, string>(StringComparer.Ordinal), said);
        }

        var found = new Dictionary<string, (string Name, bool Alternate)>(StringComparer.Ordinal);

        for (int row = 0; row < layout.File.Rows; row++)
        {
            int wordRow = layout.File.Reference(row, wordsAt).RowIn(words.File.Rows);
            int artRow = layout.File.Reference(row, artAt).RowIn(art.File.Rows);
            if (wordRow < 0 || artRow < 0)
            {
                continue;
            }

            string name = words.File.Text(wordRow, textAt);
            string id = art.File.Text(artRow, idAt);
            if (name.Length == 0 || id.Length == 0)
            {
                continue;
            }

            // FIRST WINS, EXCEPT THAT PLAIN ART REPLACES ALTERNATE - the extractor's rule, stated
            // the way it states it. Several uniques have an alternate-art layout row naming the
            // same id, and taking whichever came last would make the answer depend on row order.
            bool alternate = layout.File.Bool(row, alternateAt);
            if (found.TryGetValue(id, out (string Name, bool Alternate) had) && !(had.Alternate && !alternate))
            {
                continue;
            }

            found[id] = (name, alternate);
        }

        var byArt = new Dictionary<string, string>(found.Count, StringComparer.Ordinal);
        foreach ((string id, (string name, bool _)) in found)
        {
            byArt[id] = name;
        }

        said.Insert(0, $"unique names: {byArt.Count} from the install's own tables");
        return new UniqueNames(byArt, said);
    }
}
