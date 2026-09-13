using System.Text;
using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// The game's table of contents, and the hash everything in it is filed under.
/// </summary>
/// <remarks>
/// GETTING THE HASH WRONG FINDS NOTHING, quietly. There is no error to notice: a wrong hash is
/// simply a key that is not in the dictionary, so every icon comes back missing and it looks
/// like the install is unreadable. So the hashes are pinned against KNOWN VALUES rather than
/// against a round trip - a round trip through one implementation agrees with itself no matter
/// how wrong it is.
///
/// The values below come from MurmurHash64A and FNV-1a as their authors wrote them, checked
/// against a second implementation written differently, so they are the algorithm's answers
/// rather than this reader's.
/// </remarks>
public class BundleIndexTests
{
    /// <summary>
    /// A directory record: a hash and three numbers, and twenty bytes of them.
    /// </summary>
    /// <remarks>
    /// It said twenty-FOUR here until the reader learned to read the paths, on the belief that
    /// the file carries the padding a C# struct of those four fields would have. It does not:
    /// the format write-up and a second implementation that reads real installs both say twenty,
    /// and what a reference appeared to say came from its reading the array as a raw span of a
    /// padded struct. Nothing depended on it while the paths went unread, which is how a wrong
    /// number sat in a comment for months. The reader no longer takes anyone's word for it - see
    /// <see cref="BundleIndex.Stride"/> - and this is what <see cref="Packed.Index"/> writes.
    /// </remarks>
    private const int RootRecord = 20;

    /// <summary>A file record: a hash and three numbers.</summary>
    private const int FileRecord = 20;

    private static readonly Packed.Entry[] Some =
    [
        new("Art/2DItems/Weapons/Bow.dds", 0, 0, 1234),
        new("Art/2DItems/Rings/Ring1.dds", 1, 4096, 900),
        new("Data/Mods.dat64", 0, 8192, 77),
    ];

    private static BundleIndex Built(bool murmur = true)
    {
        BundleIndex? index = BundleIndex.Parse(Packed.Index(["bundle-a", "bundle-b"], Some, murmur));
        Assert.NotNull(index);
        return index!;
    }

    [Theory]
    [InlineData("art/2ditems/weapons/bow.dds", 0x0BBDAB7CBE7AE67B)]
    [InlineData("metadata/items/rings/ring1.dds", 0xE215515D8E915C8A)]
    [InlineData("data/mods.dat64", 0x091B0B71071156A0)]
    [InlineData("a", 0xF8D232A19E90F23C)]
    [InlineData("abcdefg", 0xEB68892CBBDC7D94)]
    [InlineData("abcdefgh", 0xC0FD347668F580D7)]
    [InlineData("abcdefghi", 0x91D17EA59EAD7C80)]
    [InlineData("", 0xF42A94E69CFF42FE)]
    public void APATHHashesToWhatMurmurSaysItDoes(string path, ulong expected)
        => Assert.Equal(expected, BundleIndex.Murmur(Encoding.UTF8.GetBytes(path)));

    [Fact]
    public void ANDTheLeftoverBytesAreFoldedInTheSameWayForEveryLength()
    {
        // The tail is where this reader differs in SHAPE from the reference: the reference reads
        // one word past the end of the string and masks it, which is fast and not allowed here,
        // so the bytes are assembled instead. Same number, every length, or nothing whose path
        // is not a multiple of eight bytes long is ever found.
        var seen = new HashSet<ulong>();
        for (int length = 1; length <= 24; length++)
        {
            ulong hash = BundleIndex.Murmur(Encoding.UTF8.GetBytes(new string('a', length)));
            Assert.True(seen.Add(hash), $"length {length} collided");
        }
    }

    [Theory]
    [InlineData("", 0x07E47507B4A92E53)]
    [InlineData("art/2ditems/weapons/bow.dds", 0x54BCC986AD752967)]
    [InlineData("Art/2DItems/Weapons/Bow.dds", 0x54BCC986AD752967)]
    [InlineData("data/mods.dat64", 0x572679A36BF3F6B4)]
    public void ANDWhatFNVSaysItDoes(string path, ulong expected)
        => Assert.Equal(expected, BundleIndex.Fnv(Encoding.UTF8.GetBytes(path)));

    [Fact]
    public void APATHIsFoundByItsHashRatherThanBySpelling()
    {
        BundleIndex index = Built();

        Assert.Equal(3, index.Count);
        Assert.Equal(["bundle-a", "bundle-b"], index.Bundles);

        FileSpot? bow = index.Find("Art/2DItems/Weapons/Bow.dds");
        Assert.NotNull(bow);
        Assert.Equal(new FileSpot(0, 0, 1234), bow!.Value);

        Assert.Equal(new FileSpot(1, 4096, 900), index.Find("Art/2DItems/Rings/Ring1.dds")!.Value);
    }

    [Fact]
    public void ANDCaseAndSlashesDoNotMatter()
    {
        // The game writes art paths in mixed case with either slash, and hashes them lowercased
        // with forward ones - so a path used exactly as it was read out of memory finds nothing.
        BundleIndex index = Built();

        Assert.Equal(new FileSpot(0, 0, 1234), index.Find("art/2ditems/weapons/bow.dds")!.Value);
        Assert.Equal(new FileSpot(0, 0, 1234), index.Find("ART/2DITEMS/WEAPONS/BOW.DDS")!.Value);
        Assert.Equal(new FileSpot(0, 0, 1234), index.Find(@"Art\2DItems\Weapons\Bow.dds")!.Value);
        Assert.Equal(new FileSpot(0, 0, 1234), index.Find("  Art/2DItems/Weapons/Bow.dds  ")!.Value);
    }

    [Fact]
    public void THEIndexSaysWhichHashHashedIt()
    {
        // It does not have to be guessed. The first directory record is the root, whose path is
        // empty, so its stored hash is whichever function's value for the empty string.
        Assert.Equal("Murmur2-64A", Built(murmur: true).Hashing);
        Assert.Equal("FNV-1a", Built(murmur: false).Hashing);

        // And an index built the old way is read the old way, rather than found to be empty.
        Assert.Equal(new FileSpot(0, 0, 1234), Built(murmur: false).Find("Art/2DItems/Weapons/Bow.dds")!.Value);
    }

    [Fact]
    public void ANDOneHashedWithSomethingElseIsRefusedRatherThanReadAsEmpty()
    {
        // A future patch changing the hash again should say so, not look like an install with no
        // files in it.
        byte[] content = Packed.Index(["bundle-a"], Some);
        int marker = content.Length - RootRecord;

        Assert.Equal(BundleIndex.MurmurMarker, BitConverter.ToUInt64(content, marker));
        BitConverter.GetBytes(0x1234567890ABCDEFul).CopyTo(content, marker);

        Assert.Null(BundleIndex.Parse(content));
    }

    [Fact]
    public void APATHThatIsNotThereIsNotThere()
    {
        BundleIndex index = Built();

        Assert.Null(index.Find("Art/2DItems/NotAThing.dds"));
        Assert.Null(index.Find(""));
        Assert.Null(index.Find(null));
        Assert.Null(index.Find("   "));
    }

    [Fact]
    public void SOMETHINGThatIsNotAnIndexIsRefusedRatherThanParsed()
    {
        Assert.Null(BundleIndex.Parse(null));
        Assert.Null(BundleIndex.Parse([]));
        Assert.Null(BundleIndex.Parse(new byte[64]));
        Assert.Null(BundleIndex.Parse([.. Enumerable.Repeat((byte)0xFF, 4096)]));

        // And one that is cut off part way through, which is what a half-written file looks like.
        // Up to the end of the root record's hash, which is the last thing this reads - past
        // that is the spelled-out paths, which it never looks at, so a cut there is not its
        // business to notice.
        byte[] content = Packed.Index(["bundle-a"], Some);
        int reads = content.Length - RootRecord + 8;

        for (int cut = 1; cut < reads; cut++)
        {
            Assert.Null(BundleIndex.Parse(content[..cut]));
        }

        Assert.NotNull(BundleIndex.Parse(content[..reads]));
    }

    [Fact]
    public void ANDAFileNamingABundleThatIsNotThereIsLeftOutRatherThanKept()
    {
        // A record pointing at bundle seventeen of two is a broken index, and following it later
        // is an exception in the middle of drawing.
        byte[] content = Packed.Index(["bundle-a"], [new("Art/Thing.dds", 0, 0, 10)]);

        // The one file record sits before the directory count and the root record; its bundle
        // number is the first thing after its hash.
        int naming = content.Length - RootRecord - 4 - FileRecord + 8;
        BitConverter.GetBytes(17).CopyTo(content, naming);

        BundleIndex? index = BundleIndex.Parse(content);
        Assert.NotNull(index);
        Assert.Equal(0, index!.Count);
        Assert.Null(index.Find("Art/Thing.dds"));
    }

    [Fact]
    public void ANINDEXBiggerThanAnyNumberSomebodyPickedIsStillAnIndex()
    {
        // What this was: a ceiling of two million files, written when that was a lot, and the
        // game outgrew it. Every count past it was refused as "not an index" - so an install
        // that had simply grown looked exactly like a corrupt one, and the only symptom was
        // that the item pictures came from a website instead.
        //
        // The bound is the FILE now: a count needing more bytes than are left cannot be right,
        // which is as strict against a wrong offset and cannot go stale.
        const int many = 2_000_000 + 1;

        using var stream = new MemoryStream();
        using var write = new BinaryWriter(stream);

        write.Write(0);            // no bundles
        write.Write(many);         // and more files than the old ceiling allowed
        for (var i = 0; i < many; i++)
        {
            write.Write((ulong)i);
            write.Write(0);        // bundle 0, which does not exist - the records are skipped
            write.Write(0);
            write.Write(0);
        }

        write.Write(1);
        write.Write(BundleIndex.MurmurMarker);
        write.Write(0L);
        write.Write(0L);

        BundleIndex.Parsed read = BundleIndex.Read(stream.ToArray());

        Assert.NotNull(read.Index);
        Assert.Contains("Murmur2-64A", read.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void ANDACountThatWillNotFitInTheFileIsStillRefused()
    {
        // The bound that replaced the ceiling has to be a real one: a wrong offset lands on a
        // number that says "four billion files", and allocating from it is the failure the
        // ceiling was there to prevent.
        using var stream = new MemoryStream();
        using var write = new BinaryWriter(stream);

        write.Write(0);
        write.Write(int.MaxValue);
        write.Write(0L);

        BundleIndex.Parsed read = BundleIndex.Read(stream.ToArray());

        Assert.Null(read.Index);
        Assert.Contains("2147483647 files", read.Why, StringComparison.Ordinal);
        Assert.Contains("needs", read.Why, StringComparison.Ordinal);
        Assert.Contains("left", read.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void ANDAHashItDoesNotKnowSaysWhichHashRatherThanNothing()
    {
        // Not "is not an index". The root's hash IS the index saying how it was built, so an
        // unknown one is news - it would mean the game changed its hashing again, the way it
        // did at 3.21.2 - and the value is what somebody would go looking for.
        using var stream = new MemoryStream();
        using var write = new BinaryWriter(stream);

        write.Write(0);
        write.Write(0);
        write.Write(1);
        write.Write(0xDEADBEEFDEADBEEFul);
        write.Write(0L);
        write.Write(0L);

        BundleIndex.Parsed read = BundleIndex.Read(stream.ToArray());

        Assert.Null(read.Index);
        Assert.Contains("DEADBEEFDEADBEEF", read.Why, StringComparison.Ordinal);
        Assert.Contains("neither Murmur2-64A nor FNV-1a", read.Why, StringComparison.Ordinal);
    }

    /// <summary>
    /// An index carrying the spelled-out paths, at whichever directory-record stride is asked for.
    /// </summary>
    /// <remarks>
    /// The paths are written with a shared prefix on purpose: that is how the game stores them
    /// and it is the half of the encoding that a reader can get wrong while still producing
    /// plausible-looking strings.
    /// </remarks>
    private static BundleIndex WithNames(int stride)
    {
        byte[] paths = Packed.Paths(
            ["art/2ditems/weapons/", "art/2dart/uiimages/ingame/"],
            [
                (0, "bow.dds"),
                (0, "quiver.dds"),
                (1, "atlasiconcontentbreach.dds"),
                (-1, "data/mods.dat64"),
            ]);

        BundleIndex.Parsed read = BundleIndex.Read(
            Packed.Index(["bundle-a"], Some, murmur: true, paths: paths, stride: stride));

        Assert.NotNull(read.Index);
        return read.Index!;
    }

    [Theory]
    [InlineData(20)]
    [InlineData(24)]
    public void THENamesAreReadWhicheverSizeTheDirectoryRecordsTurnOutToBe(int stride)
    {
        // WHICH ONE THE GAME USES IS NOT SETTLED BY READING ABOUT IT. The format write-up says
        // twenty; a reference reading the array as a span of a padded struct says twenty-four.
        // So the reader tries both and keeps whichever leaves a readable bundle behind, and
        // this pins that it really does work either way rather than by luck on one of them.
        BundleIndex index = WithNames(stride);

        Assert.True(index.Named);
        Assert.Equal(stride, index.Stride);

        Dictionary<string, string> found = index.Look(Packed.AsIs, ["AtlasIconContentBreach"]);

        Assert.Equal(
            "art/2dart/uiimages/ingame/atlasiconcontentbreach.dds",
            found["AtlasIconContentBreach"]);
    }

    [Fact]
    public void ANDANameIsFoundWhateverCaseItIsAskedFor()
    {
        // The data spells these the way the game's own table does - "AtlasIconContentBreach" -
        // while the paths in the index are all lower case. Matching them literally finds none.
        // A fresh index per question, because one index answers once - see below.
        Assert.Single(WithNames(20).Look(Packed.AsIs, ["ATLASICONCONTENTBREACH"]));
        Assert.Single(WithNames(20).Look(Packed.AsIs, ["atlasiconcontentbreach"]));
    }

    [Fact]
    public void ONEIndexAnswersOnceAndThenLetsTheBlobGo()
    {
        // On a real install the spelled-out paths are tens of megabytes of compressed text that
        // only this walk ever wants - held for a session that has had its answer, they are tens
        // of megabytes of nothing. So the walk releases them, which is the contract the method
        // already asked for written into the type: everything that needs a name asks in the same
        // call.
        BundleIndex index = WithNames(20);
        Assert.True(index.Named);
        Assert.Contains("packed", index.Says, StringComparison.Ordinal);

        Assert.Single(index.Look(Packed.AsIs, ["AtlasIconContentBreach"]));

        Assert.False(index.Named);
        Assert.Empty(index.Look(Packed.AsIs, ["Bow"]));

        // And it says so rather than reading like an index that never had names at all - those
        // are two different states and only one of them is worth investigating.
        Assert.Contains("read and released", index.Says, StringComparison.Ordinal);
        Assert.DoesNotContain("no names", index.Says, StringComparison.Ordinal);
    }

    [Fact]
    public void ANDAskingForNothingDoesNotSpendTheOneWalk()
    {
        // A session that never opens the atlas asks for no names, and must not thereby lose the
        // ability to answer when it finally does.
        BundleIndex index = WithNames(20);

        Assert.Empty(index.Look(Packed.AsIs, []));
        Assert.True(index.Named);

        Assert.Single(index.Look(Packed.AsIs, ["AtlasIconContentBreach"]));
    }

    [Fact]
    public void ANDTheWholeListIsAnsweredInOneWalk()
    {
        // Asked one at a time this would decompress tens of megabytes per name, which is why
        // the whole list goes in at once.
        BundleIndex index = WithNames(20);

        Dictionary<string, string> found = index.Look(
            Packed.AsIs, ["Bow", "Quiver", "Mods", "NothingLikeThis"]);

        Assert.Equal("art/2ditems/weapons/bow.dds", found["Bow"]);
        Assert.Equal("art/2ditems/weapons/quiver.dds", found["Quiver"]);

        // A path with no prefix reads the same as one with, and the name is what is left after
        // the last slash and before the last dot.
        Assert.Equal("data/mods.dat64", found["Mods"]);

        // A name the install does not have is ABSENT rather than empty: the caller can then
        // tell "there is no such art" from "here it is", and say so.
        Assert.False(found.ContainsKey("NothingLikeThis"));
    }

    [Fact]
    public void ANDAnIndexWithNoNamesInItStillOpens()
    {
        // Which is every index this project read until now: nothing looked at the paths, so an
        // index cut off after the directory records was perfectly usable. It still is - the
        // names are an extra, and asking for one answers nothing rather than throwing.
        BundleIndex index = Built();

        Assert.False(index.Named);
        Assert.Empty(index.Look(Packed.AsIs, ["Bow"]));
    }

    [Fact]
    public void ANDNothingIsWalkedForAnEmptyRequest()
    {
        // The walk is the expensive thing here, and "no names wanted" is what every session
        // that never opens the atlas asks for.
        BundleIndex index = WithNames(20);

        Assert.Empty(index.Look(Packed.AsIs, []));
        Assert.Empty(index.Look(Packed.AsIs, null));
        Assert.Empty(index.Look(Packed.AsIs, ["", "   "]));
    }
}
