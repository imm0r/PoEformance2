using System.Buffers.Binary;
using PoEformance.Features;
using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// Joining a unique's name out of the install's three tables.
/// </summary>
/// <remarks>
/// BUILT AGAINST SYNTHETIC .dat FILES, the same choice QuestProgressTests made and for the same
/// two reasons: the game's are three hundred megabytes nobody has in CI, and a synthetic one can
/// be made to hold the awkward case on purpose - here, two layout rows naming one art, one of them
/// alternate, which is the tie the extractor breaks and the reason this is not a plain dictionary
/// fill.
///
/// WHAT THESE CANNOT SAY, and it is worth being plain about: they check the JOIN, not the LAYOUT.
/// None of the three tables is in the loader's file table, so no capture can confirm their column
/// offsets the way the client confirmed BaseItemTypes and Mods. What confirms them is the FILE's
/// own row size at runtime, against a real install - LoadedTable.Agrees - and UniqueNames.Say
/// reports it per table so a layout that stopped fitting says so rather than going quiet.
/// </remarks>
public class UniqueNamesTests
{
    private sealed class Held(Dictionary<string, byte[]> files) : IGameArchive
    {
        public bool Ready => true;

        public string Describe => "a made-up install";

        public IEnumerable<string> Paths => files.Keys;

        public bool Has(string path) => files.ContainsKey(path);

        public byte[]? Read(string path) => files.GetValueOrDefault(path);

        public byte[]? Read(string path, int at, int length)
            => Read(path) is { } bytes && at >= 0 && length >= 0 && at + (long)length <= bytes.Length
                ? bytes[at..(at + length)]
                : null;
    }

    private static GameFiles Install(params (string Path, byte[] Content)[] contents)
    {
        var content = new List<byte>();
        var entries = new List<Packed.Entry>();

        foreach ((string path, byte[] bytes) in contents)
        {
            entries.Add(new Packed.Entry(path, 0, content.Count, bytes.Length));
            content.AddRange(bytes);
        }

        var archive = new Held(new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["data.bundle.bin"] = Packed.Bundle([.. content], chunkSize: 64),
            ["_.index.bin"] = Packed.Bundle(Packed.Index(["data"], [.. entries]), chunkSize: 64),
        });

        GameFiles? files = GameFiles.Open(archive, Packed.AsIs);
        Assert.NotNull(files);
        return files!;
    }

    /// <summary>A .datc64: row count, packed rows, the separator, then the variable section.</summary>
    private static byte[] Dat(int rows, int rowSize, Action<byte[], int> fill, byte[] variable)
    {
        var bytes = new byte[4 + (rows * rowSize) + 8 + variable.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)rows);
        for (int row = 0; row < rows; row++)
        {
            fill(bytes, 4 + (row * rowSize));
        }

        for (int i = 0; i < 8; i++)
        {
            bytes[4 + (rows * rowSize) + i] = 0xBB;
        }

        variable.CopyTo(bytes, 4 + (rows * rowSize) + 8);
        return bytes;
    }

    /// <summary>The vendored layouts, so the offsets under test are the shipped ones.</summary>
    private static QuestTableLayouts Layouts()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "data", "item-tables.json")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Assert.IsType<QuestTableLayouts>(
            QuestTableLayouts.Load(Path.Combine(dir!.FullName, "data", "item-tables.json")));
    }

    /// <summary>Strings at known offsets into the variable section, NUL-terminated as the game writes them.</summary>
    private static (byte[] Bytes, int[] At) Variable(params string[] texts)
    {
        var bytes = new List<byte>();
        var at = new int[texts.Length];

        for (int index = 0; index < texts.Length; index++)
        {
            // OFFSETS COUNT FROM THE SEPARATOR, which is eight bytes long - so byte zero of this
            // section is offset eight, not zero. Prepending the eight instead of adding them is
            // how this fixture first pointed every string at the one before it.
            at[index] = 8 + bytes.Count;
            bytes.AddRange(System.Text.Encoding.Unicode.GetBytes(texts[index]));
            bytes.AddRange([0, 0]);
        }

        return ([.. bytes], at);
    }

    private static GameFiles ThreeTables(QuestTableLayouts layouts, params (int Words, int Art, bool Alternate)[] rows)
    {
        int layoutSize = layouts.RowSizeOf("UniqueStashLayout");
        int wordsSize = layouts.RowSizeOf("Words");
        int artSize = layouts.RowSizeOf("ItemVisualIdentity");

        Assert.True(layoutSize > 0 && wordsSize > 0 && artSize > 0, "a vendored layout computes nothing");

        (byte[] names, int[] namesAt) = Variable("Kaom's Heart", "Tabula Rasa");
        (byte[] ids, int[] idsAt) = Variable("FourBodyStrUnique1", "FourBodyStrDexInt1");

        int wordsTextAt = layouts.OffsetOf("Words", "Text");
        int artIdAt = layouts.OffsetOf("ItemVisualIdentity", "Id");
        int keyWordsAt = layouts.OffsetOf("UniqueStashLayout", "WordsKey");
        int keyArtAt = layouts.OffsetOf("UniqueStashLayout", "ItemVisualIdentityKey");
        int alternateAt = layouts.OffsetOf("UniqueStashLayout", "IsAlternateArt");

        var order = 0;
        return Install(
            ("data/uniquestashlayout.datc64", Dat(rows.Length, layoutSize, (b, at) =>
            {
                (int words, int art, bool alternate) = rows[order++];

                // A foreign reference is two words; which half is the row is resolved by asking,
                // so the row goes in the first and the table's own marker in the second.
                BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(at + keyWordsAt), (ulong)words);
                BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(at + keyWordsAt + 8), 0);
                BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(at + keyArtAt), (ulong)art);
                BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(at + keyArtAt + 8), 0);
                b[at + alternateAt] = alternate ? (byte)1 : (byte)0;
            }, [])),
            ("data/words.datc64", Dat(namesAt.Length, wordsSize, (b, at) =>
            {
                int row = (at - 4) / wordsSize;
                BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(at + wordsTextAt), (ulong)namesAt[row]);
            }, names)),
            ("data/itemvisualidentity.datc64", Dat(idsAt.Length, artSize, (b, at) =>
            {
                int row = (at - 4) / artSize;
                BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(at + artIdAt), (ulong)idsAt[row]);
            }, ids)));
    }

    [Fact]
    public void AUNIQUEISNamedByItsArtRatherThanByItsBaseType()
    {
        QuestTableLayouts layouts = Layouts();
        UniqueNames joined = UniqueNames.Read(ThreeTables(layouts, (0, 0, false), (1, 1, false)), layouts);

        Assert.Equal(2, joined.ByArt.Count);
        Assert.Equal("Kaom's Heart", joined.ByArt["FourBodyStrUnique1"]);
        Assert.Equal("Tabula Rasa", joined.ByArt["FourBodyStrDexInt1"]);
        Assert.Contains(joined.Say, line => line.Contains("2 from the install", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ANDPlainArtWinsOverAlternateWhicheverRowComesFirst(bool alternateFirst)
    {
        // THE TIE THIS EXISTS TO BREAK. Several uniques have an alternate-art layout row naming
        // the same id as their ordinary one, so taking whichever came last would make the answer
        // depend on row order - and the answer is a name somebody sees on an item.
        QuestTableLayouts layouts = Layouts();

        UniqueNames joined = UniqueNames.Read(
            ThreeTables(
                layouts,
                alternateFirst ? (1, 0, true) : (0, 0, false),
                alternateFirst ? (0, 0, false) : (1, 0, true)),
            layouts);

        Assert.Equal("Kaom's Heart", joined.ByArt["FourBodyStrUnique1"]);
    }

    [Fact]
    public void ANDNoInstallLeavesTheShippedMapStanding()
    {
        UniqueNames joined = UniqueNames.Read(null, null);

        Assert.Empty(joined.ByArt);
        Assert.Contains(joined.Say, line => line.Contains("no install", StringComparison.Ordinal));
    }

    [Fact]
    public void ANDATableThatIsNotThereSaysSoRatherThanAnsweringHalfway()
    {
        // Two of the three is not two thirds of an answer: without ItemVisualIdentity there is no
        // key to file a name under at all, so the shipped map has to stay in force.
        QuestTableLayouts layouts = Layouts();
        UniqueNames joined = UniqueNames.Read(
            Install(("data/words.datc64", Dat(1, layouts.RowSizeOf("Words"), (_, _) => { }, new byte[16]))),
            layouts);

        Assert.Empty(joined.ByArt);
        Assert.Contains(joined.Say, line => line.Contains("did not read", StringComparison.Ordinal));
    }
}
