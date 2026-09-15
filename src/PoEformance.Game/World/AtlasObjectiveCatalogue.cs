using PoEformance.Core.Memory;
using PoEformance.Core.Schema;

namespace PoEformance.Game.World;

/// <summary>What a node asks of a player, and the picture the game puts on it.</summary>
/// <param name="Id">The engine key - <c>Ritual</c>, <c>Breach2</c>, <c>AbyssDepths</c>.</param>
/// <param name="Words">The objective as a player reads it, markup stripped.</param>
/// <param name="Icon">The art NAME, last part of the path the row carries.</param>
public readonly record struct AtlasObjective(string Id, string Words, string Icon);

/// <summary>
/// The league mechanic on a node, read from the game rather than matched to a token.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS, and it is a fault this project caused and then had to answer for. The atlas
/// says which mechanic a node hosts through a content TOKEN - 26741 is
/// <c>map_atlas_node_has_ritual</c> - and data/atlas-content.json used to supply a picture for
/// those tokens. Its ids were two rows short, so a Delirium node wore a Ritual symbol, and when
/// the file was dropped behind the game (2026-09-15) the wrong picture became NO picture: the
/// game's stat description is a sentence and carries no art.
///
/// NO PICTURE IS HONEST BUT IT IS NOT THE ANSWER, and the game has one. A node points at its
/// EndgameMapAtlas row, that row points at an EndgameMapObjectives row, and THAT row carries the
/// whole art path beside the sentence:
///
/// <code>
///   Ritual  "Complete all [ContainsRitual|Ritual Altars]"
///           Art/2DArt/UIImages/InGame/AtlasScreen/AtlasIconContent/AtlasIconContentRitual
/// </code>
///
/// So nothing here matches a token to a picture. The game's own link is followed, which is right
/// on whatever client is running and cannot drift the way a table of ids does.
///
/// THE NAME LANDS WHERE THE INSTALL WALK ALREADY LOOKS. The path's last part is the art name
/// every other content in this project carries, and the shipped badges already name these same
/// six - AtlasIconContentRitual, ...Breach, ...Delirium, ...Abyss, ...Incursion, ...Expedition -
/// so they are in the set the index walk asks about before any of this runs. No ordering to get
/// right, and no second walk.
///
/// NINETEEN ROWS, EIGHT IN USE, so the read is cached by the row's address and a node costs three
/// pointer reads and a dictionary hit. Measured on the committed captures: 148 of 599 atlas rows
/// carry an objective.
/// </remarks>
public sealed class AtlasObjectiveCatalogue
{
    /// <summary>
    /// How many characters a column is read as.
    /// </summary>
    /// <remarks>
    /// NINETY-SIX FOR A REASON THAT IS NOT THE LENGTH. ReadUnicodeString HALVES its request until
    /// one succeeds, so against a recording a bigger ask returns FEWER characters - it only
    /// answers the sizes that were actually recorded. This is the size AtlasRowProbe reads at, so
    /// every capture this project has taken can be replayed against this reader. It is also ample:
    /// the longest art path here is 74 characters and the longest objective well under that.
    /// </remarks>
    private const int MostChars = 96;

    private readonly IMemoryReader _reader;
    private readonly Dictionary<ulong, AtlasObjective> _rows = [];

    private readonly int _dataStorage;
    private readonly int _data;
    private readonly int _atlasRow;
    private readonly int _objective;
    private readonly int _id;
    private readonly int _text;
    private readonly int _icon;

    public AtlasObjectiveCatalogue(IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;

        StructDef node = schema.Structs["AtlasNode"];
        StructDef data = schema.Structs["AtlasNodeData"];
        StructDef row = schema.Structs["EndgameMapAtlasRow"];
        StructDef objective = schema.Structs["EndgameMapObjectivesRow"];

        _dataStorage = (int)node.Constants["DataStoragePtr"];
        _data = (int)node.Constants["DataPtr"];
        _atlasRow = data.OffsetOf("AtlasRowPtr");
        _objective = row.OffsetOf("MapObjectiveRef");
        _id = objective.OffsetOf("IdPtr");
        _text = objective.OffsetOf("ObjectiveTextPtr");
        _icon = objective.OffsetOf("ContentIconPtr");
    }

    /// <summary>How many distinct objectives have been read, for a line that reports coverage.</summary>
    public int Known => _rows.Count;

    /// <summary>
    /// Every art name met so far, so the install can be asked where those pictures live.
    /// </summary>
    /// <remarks>
    /// These names come from a table that is not a content table, so nothing else knows about
    /// them - and a name the index walk is never asked for is a picture that never arrives. See
    /// <see cref="AtlasContentNames.LearnArt"/>, which is where they go.
    /// </remarks>
    public IEnumerable<string> Art()
    {
        foreach (AtlasObjective one in _rows.Values)
        {
            if (one.Icon.Length > 0)
            {
                yield return one.Icon;
            }
        }
    }

    /// <summary>
    /// The objective a node hosts, or null where it hosts none.
    /// </summary>
    /// <remarks>
    /// MOST NODES HAVE NONE and that is ordinary rather than a failure: an atlas position only
    /// carries one where the game has put a mechanic on it. Null is the common answer.
    ///
    /// Called from the reader thread during a study pass, once per node.
    /// </remarks>
    public AtlasObjective? For(ulong nodeAddress)
    {
        if (nodeAddress == 0
            || !_reader.TryRead(nodeAddress + (ulong)_dataStorage, out ulong storage) || storage == 0
            || !_reader.TryRead(storage + (ulong)_data, out ulong body) || body == 0
            || !_reader.TryRead(body + (ulong)_atlasRow, out ulong row) || row == 0
            || !_reader.TryRead(row + (ulong)_objective, out ulong at) || at == 0)
        {
            return null;
        }

        if (_rows.TryGetValue(at, out AtlasObjective known))
        {
            return known.Id.Length > 0 ? known : null;
        }

        var read = new AtlasObjective(Text(at + (ulong)_id), Say(at + (ulong)_text), Art(at + (ulong)_icon));

        // REMEMBERED EVEN WHEN IT READ AS NOTHING, so a row that will not answer is not re-read
        // once per node per study. An empty id is the marker for that, and For hands back null.
        _rows[at] = read;
        return read.Id.Length > 0 ? read : null;
    }

    /// <summary>The string a slot points at, or empty where it points at nothing readable.</summary>
    private string Text(ulong slot)
    {
        ulong at = _reader.ReadPointer(slot);
        return MemoryReaderExtensions.IsPlausiblePointer(at) ? _reader.ReadUnicodeString(at, MostChars) : string.Empty;
    }

    /// <summary>The objective as a player reads it: the game's <c>[Code|Display]</c> markup stripped.</summary>
    private string Say(ulong slot) => EndgameMapContentCatalogue.AsTheFileWouldWriteIt(Text(slot));

    /// <summary>The art NAME out of the whole path the row carries.</summary>
    private string Art(ulong slot)
    {
        string path = Text(slot);
        return path.Length > 0 ? EndgameMapContentCatalogue.ArtName(path) : string.Empty;
    }
}
