using PoEformance.Features;

namespace PoEformance.Core.Tests;

/// <summary>
/// Finding the picture the game draws for a thing a map contains.
/// </summary>
/// <remarks>
/// The drawing is in the overlay and out of reach here, so what is checked is the part that
/// decides ANYTHING IS DRAWN AT ALL: which file a name resolves to, that a folder filled after
/// the first look is noticed when asked, and that a name nothing has comes back empty rather
/// than as a path that will fail one layer further on.
///
/// The install half is not reachable without a copy of the game, and it is deliberately the
/// half that can do nothing on its own: with no folders proposed it never asks, which is what
/// the shipped data leaves it doing.
/// </remarks>
public class AtlasArtTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "poeformance-atlas-art", Guid.NewGuid().ToString("N"));

    public AtlasArtTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is not worth failing a test run over.
        }
    }

    private AtlasArt Art() => new() { Folder = _folder };

    private string Drop(string name)
    {
        string file = Path.Combine(_folder, name);

        // Not a real picture: nothing here decodes one. What is being checked is which FILE the
        // name resolves to, and the decoding is the icon cache's business.
        File.WriteAllBytes(file, [0x89, 0x50, 0x4E, 0x47]);
        return file;
    }

    [Fact]
    public void APictureInTheFolderIsWhatAContentDrawsWith()
    {
        string dropped = Drop("AtlasIconContentBreach.png");

        Assert.Equal(dropped, Art().File("AtlasIconContentBreach"));
    }

    [Fact]
    public void ANDTheNameIsMatchedHoweverTheFileSpellsIt()
    {
        // The art names are written as the game's own table writes them, while a folder filled
        // by hand or by somebody else's extractor is as likely to be all lower case. On Windows
        // the difference does not exist; on the machines this is built on it does, which is the
        // kind of thing that works everywhere except where it is used.
        string dropped = Drop("atlasiconcontentbreach.png");

        Assert.Equal(dropped, Art().File("AtlasIconContentBreach"));
    }

    [Fact]
    public void ANameNothingHasComesBackWithNothing()
    {
        // Rather than with a path that does not exist, which would be a file error a layer
        // further on for every content on the atlas, every frame.
        Assert.Equal(string.Empty, Art().File("AtlasIconContentBreach"));
        Assert.Equal(string.Empty, Art().File(null));
        Assert.Equal(string.Empty, Art().File("   "));
    }

    [Fact]
    public void SomethingThatIsNotAPictureIsNotOfferedAsOne()
    {
        Drop("AtlasIconContentBreach.txt");

        Assert.Equal(string.Empty, Art().File("AtlasIconContentBreach"));
    }

    [Fact]
    public void THEFolderIsReadOnceAndReadAgainWhenAsked()
    {
        // The point of asking: the folder is empty on the first look for most people, because
        // filling it is the thing they are about to do. Without a way to look again, dropping
        // the files in appears to do nothing until the tool is restarted.
        AtlasArt art = Art();
        Assert.Equal(string.Empty, art.File("AtlasIconContentBreach"));

        string dropped = Drop("AtlasIconContentBreach.png");
        Assert.Equal(string.Empty, art.File("AtlasIconContentBreach"));

        art.LookAgain();
        Assert.Equal(dropped, art.File("AtlasIconContentBreach"));
    }

    [Fact]
    public void ITSaysHowManyOfWhatWasAskedForItHas()
    {
        // Because a content drawn as words looks exactly like the setting not working, and the
        // settings page is where that gets said out loud.
        AtlasArt art = Art();
        Assert.Equal((0, 0), art.Tally);

        Drop("AtlasIconContentBreach.png");
        art.File("AtlasIconContentBreach");
        art.File("AtlasIconContentRitual");

        Assert.Equal((1, 2), art.Tally);

        // Asking twice for the same name is one name, not two.
        art.File("AtlasIconContentRitual");
        Assert.Equal((1, 2), art.Tally);
    }

    [Fact]
    public void WITHNoFoldersProposedTheInstallIsNeverAsked()
    {
        // The shipped data proposes none, because the path the game keeps this art under is not
        // in any published copy of the table - only the name is. So the install half sits there
        // doing nothing until somebody fills them in, and doing nothing must not cost anything.
        var asked = new List<string>();
        using var store = new ItemArtStore(
            folder: _folder,
            fetch: (path, _) =>
            {
                asked.Add(path);
                return Task.FromResult<byte[]?>(null);
            });

        var art = new AtlasArt { Folder = Path.Combine(_folder, "empty"), Store = store };

        Assert.Equal(string.Empty, art.File("AtlasIconContentBreach"));
        Assert.Empty(asked);
    }
}
