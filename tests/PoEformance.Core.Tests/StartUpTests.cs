using System.Text.RegularExpressions;
using PoEformance.Core.Schema;
using PoEformance.Features;
using PoEformance.Game.Entities;
using PoEformance.Game.Items;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// That the tool can be built at all against the schema it ships with.
/// </summary>
/// <remarks>
/// WHAT THIS IS FOR, and it is not hypothetical. The item reader asked the schema for a struct
/// called "StdVector" that had never been in the file. The lookup is in its CONSTRUCTOR, the
/// constructor runs on the way into the overlay, and nothing catches it there - so the tool died
/// with a message box before the overlay appeared, on every run, for everybody, from the commit
/// that first read an item's mods.
///
/// Every reader was tested. The schema was tested. What nobody checked was the JOIN between them:
/// that the questions the code asks are questions the shipped file answers. The other tests all
/// build a schema of their own to exercise a reader against, which is right for testing the
/// reader and is exactly why this got through.
///
/// So both halves are checked here against the REAL file: the readers the tool builds on its way
/// up, and - because a reader added tomorrow will not be in that list - every struct name the
/// source asks for anywhere.
/// </remarks>
public class StartUpTests
{
    private static DirectoryInfo Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "schema", "poe2.offsets.json")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return dir;
        }
    }

    private static OffsetSchema Shipped()
        => SchemaJson.Load(Path.Combine(Root.FullName, "schema", "poe2.offsets.json"));

    [Fact]
    public void EVERYReaderTheToolBuildsOnItsWayUpFindsWhatItAsksTheSchemaFor()
    {
        OffsetSchema schema = Shipped();
        var memory = new FakeMemoryReader();
        const ulong gameStates = 0x1400000000;

        // No memory is read here - these constructors only resolve offsets - so an empty reader
        // is enough, and a throw is a schema question with no answer rather than a bad address.
        _ = new EntityReader(memory, schema);
        _ = new ItemReader(memory, schema);
        _ = new StashReader(memory, schema);
        _ = new StashInspector(memory, schema, gameStates);
        _ = new UiElementReader(memory, schema);
        _ = new RitualLineReader(memory, schema, new UiElementReader(memory, schema));
        _ = new PreloadReader(memory, schema);
        _ = new EntityInspector(memory, schema);
        _ = new UiTreeInspector(memory, schema, gameStates);
        _ = new StructureInspector(memory, schema, gameStates);
        _ = new AtlasWatch(memory, schema, gameStates);
        _ = new WorldReader(memory, schema);
    }

    [Fact]
    public void ANDEveryStructTheSourceAsksForIsInTheShippedFile()
    {
        // The general half. A reader written next month will not be in the list above, and the
        // failure it would have is the same one: a name in code that is not a name in the file.
        IReadOnlyDictionary<string, StructDef> structs = Shipped().Structs;
        var asked = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (string file in Directory.EnumerateFiles(
                     Path.Combine(Root.FullName, "src"), "*.cs", SearchOption.AllDirectories))
        {
            foreach (Match found in Regex.Matches(File.ReadAllText(file), """Structs\["([^"]+)"\]"""))
            {
                asked[found.Groups[1].Value] = Path.GetFileName(file);
            }
        }

        // Not a small number by accident: if this ever reads as a handful, the search stopped
        // finding the source rather than the source stopping asking.
        Assert.True(asked.Count > 40, $"only found {asked.Count} struct lookups - did the source move?");

        string[] missing = [.. asked.Where(one => !structs.ContainsKey(one.Key))
            .Select(one => $"{one.Key} (asked for in {one.Value})")];

        Assert.Empty(missing);
    }
}
