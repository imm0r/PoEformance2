using PoEformance.Features;

namespace PoEformance.Core.Tests;

/// <summary>
/// Getting each item's own picture, and keeping it.
/// </summary>
/// <remarks>
/// The fetch is handed in, so all of this runs without a network - which is the point of it
/// being handed in. What is checked is the part that decides how often the outside world is
/// asked: that a picture is fetched ONCE, that a path which came back missing is not asked for
/// again, and that nothing at all goes out until somebody turns it on.
///
/// Those matter more than they look. A stash of two thousand items is drawn sixty times a
/// second, so "ask again next frame" is not a small mistake - it is a hundred thousand requests
/// a minute at somebody else's server.
/// </remarks>
public class ItemArtStoreTests
{
    private static string Somewhere() => Path.Combine(Path.GetTempPath(), $"art-{Guid.NewGuid():N}");

    /// <summary>Waits for the fetches to settle, so a test never depends on a race.</summary>
    /// <summary>Waits for the store's background work to finish.</summary>
    /// <remarks>
    /// POLLS rather than waiting a fixed time, so the normal case costs only as long as the
    /// work really takes and the ceiling is paid only when something is wrong. The ceiling is
    /// generous because it is a figure for a SHARED CI RUNNER rather than for this machine:
    /// the previous two seconds passed locally for months and then failed on a loaded runner,
    /// which is the definition of a flake.
    ///
    /// And it ASSERTS, which the previous version did not. A silent timeout left the test to
    /// fail on whatever the unfinished work had not produced - an ArgumentException about an
    /// empty path, thrown three lines later - so a slow runner reported itself as a bug in
    /// the code under test.
    /// </remarks>
    private static void Settle(ItemArtStore store)
    {
        for (int i = 0; i < 3_000 && store.Pending > 0; i++)
        {
            Thread.Sleep(10);
        }

        Assert.True(store.Pending == 0, $"the store never settled - {store.Pending} still pending");
    }

    [Fact]
    public void APICTUREAppearsUnderItsOwnNameOnlyOnceItIsWhole()
    {
        // WHO THE READER IS decides this: the thread that draws, asking for the file the
        // instant it exists. File.Exists is true from the moment a write begins, so writing
        // straight to the name hands the renderer a file still open for writing - or half a
        // picture - and the renderer remembers an icon it could not read as one it never can.
        // So the bytes go to a name nothing looks for and are renamed into place.
        string folder = Somewhere();

        try
        {
            using var store = new ItemArtStore(folder, (path, token) => Task.FromResult<byte[]?>([1, 2, 3, 4]))
            {
                Enabled = true,
            };

            // A leftover from a run that died mid-write. It has to be gone afterwards, which is
            // what says the bytes really did go through it rather than straight to the name.
            string destination = store.FileFor(ItemArtStore.Normalise("Art/2DItems/Currency/Thing.dds"));
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(destination + ItemArtStore.WhileWriting, [9, 9]);

            store.Local("Art/2DItems/Currency/Thing.dds");
            Settle(store);

            string file = store.Local("Art/2DItems/Currency/Thing.dds");

            Assert.NotEqual(string.Empty, file);
            Assert.Equal<byte[]>([1, 2, 3, 4], File.ReadAllBytes(file));

            // And nothing half-written is left lying about under either name.
            Assert.Empty(Directory.GetFiles(folder, "*" + ItemArtStore.WhileWriting));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ANDAPictureThatCannotBePutInPlaceLeavesNothingBehind()
    {
        // The half-written file is named so that nothing looks for it, which means nothing
        // would ever clean it up either - it would just sit there being wondered about.
        string folder = Somewhere();

        try
        {
            using var store = new ItemArtStore(folder, (path, token) => Task.FromResult<byte[]?>([1, 2, 3, 4]))
            {
                Enabled = true,
            };

            // A directory where the picture wants to be: the write succeeds, the rename cannot.
            Directory.CreateDirectory(store.FileFor(ItemArtStore.Normalise("Art/2DItems/Currency/Thing.dds")));

            store.Local("Art/2DItems/Currency/Thing.dds");
            Settle(store);

            Assert.Empty(Directory.GetFiles(folder, "*" + ItemArtStore.WhileWriting));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void APICTUREIsFetchedOnceAndReadFromDiskAfterwards()
    {
        string folder = Somewhere();
        int asked = 0;

        try
        {
            using var store = new ItemArtStore(folder, (path, token) =>
            {
                Interlocked.Increment(ref asked);
                return Task.FromResult<byte[]?>([1, 2, 3]);
            })
            {
                Enabled = true,
            };

            // The first look answers with nothing and starts the fetch - it is called while
            // drawing a frame, so waiting here would stall the whole overlay.
            Assert.Equal(string.Empty, store.Local("Art/2DItems/Weapons/Bow.dds"));
            Settle(store);

            string file = store.Local("Art/2DItems/Weapons/Bow.dds");
            Assert.NotEqual(string.Empty, file);
            Assert.True(File.Exists(file));

            // Asked for a hundred more times, as a drawn frame would.
            for (int i = 0; i < 100; i++)
            {
                Assert.Equal(file, store.Local("Art/2DItems/Weapons/Bow.dds"));
            }

            Assert.Equal(1, asked);
            Assert.Equal((0, 1, 0), store.Tally);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public void ANDOneThatIsNotThereIsNotAskedForAgain()
    {
        // A path the server does not have is drawn as often as one it does. Without this it is
        // re-asked every frame, forever, for an answer that will not change.
        string folder = Somewhere();
        int asked = 0;

        try
        {
            using var store = new ItemArtStore(folder, (path, token) =>
            {
                Interlocked.Increment(ref asked);
                return Task.FromResult<byte[]?>(null);
            })
            {
                Enabled = true,
            };

            store.Local("Art/2DItems/NewThisLeague.dds");
            Settle(store);

            for (int i = 0; i < 50; i++)
            {
                Assert.Equal(string.Empty, store.Local("Art/2DItems/NewThisLeague.dds"));
            }

            Assert.Equal(1, asked);
            Assert.Equal((0, 0, 1), store.Tally);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public void THEINSTALLIsAskedFirstAndTheWebIsNotAskedAtAll()
    {
        // The install is where the art actually is - local, complete, and not somebody else's
        // server. Going to the web for something already on the disk is the bug this prevents.
        string folder = Somewhere();
        int asked = 0;
        var unpacked = new List<string>();

        try
        {
            using var store = new ItemArtStore(folder, (path, token) =>
            {
                Interlocked.Increment(ref asked);
                return Task.FromResult<byte[]?>([9, 9, 9]);
            })
            {
                Enabled = true,
                Install = path =>
                {
                    unpacked.Add(path);
                    return [1, 2, 3];
                },
            };

            store.Local("Art/2DItems/Weapons/Bow.dds");
            Settle(store);

            string file = store.Local("Art/2DItems/Weapons/Bow.dds");
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(file));
            Assert.Equal(0, asked);
            Assert.Equal((1, 0, 0), store.Tally);

            // And it is handed the path SPELLED AS THE ITEM SPELLS IT. The cache and the picture
            // server both want the extension off, and the install's index does not know that
            // name - dropping it here finds nothing and falls back to the web for everything.
            Assert.Equal(["Art/2DItems/Weapons/Bow.dds"], unpacked);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public void ANDPicturesComeOutOfItWithoutAnybodyTurningAnythingOn()
    {
        // Reading a file the player already has is not a decision to put to them. The switch is
        // for the web, so leaving it off must not mean no pictures when the game is right there.
        string folder = Somewhere();

        try
        {
            using var store = new ItemArtStore(folder, (path, token) => Task.FromResult<byte[]?>([9]))
            {
                Install = path => [4, 5, 6],
            };

            Assert.False(store.Enabled);
            Assert.True(store.Working);

            store.Local("Art/2DItems/Weapons/Bow.dds");
            Settle(store);

            Assert.NotEqual(string.Empty, store.Local("Art/2DItems/Weapons/Bow.dds"));
            Assert.Equal((1, 0, 0), store.Tally);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public void ANDTheWebPicksUpWhatTheInstallDoesNotHave()
    {
        // A texture the install will not give up - a format that will not decode, or a file that
        // is not there - still has a picture on poe2db, so one source failing is not the end of it.
        string folder = Somewhere();

        try
        {
            using var store = new ItemArtStore(folder, (path, token) => Task.FromResult<byte[]?>([7, 7]))
            {
                Enabled = true,
                Install = path => path.Contains("Bow", StringComparison.Ordinal) ? [1] : null,
            };

            store.Local("Art/2DItems/Weapons/Bow.dds");
            store.Local("Art/2DItems/Rings/Ring1.dds");
            Settle(store);

            Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(store.Local("Art/2DItems/Weapons/Bow.dds")));
            Assert.Equal(new byte[] { 7, 7 }, File.ReadAllBytes(store.Local("Art/2DItems/Rings/Ring1.dds")));
            Assert.Equal((1, 1, 0), store.Tally);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public void ANDWithNeitherOneNothingIsAskedOfAnybody()
    {
        using var store = new ItemArtStore(Somewhere(), (path, token) => Task.FromResult<byte[]?>([1]));

        Assert.False(store.Working);
        Assert.Equal(string.Empty, store.Local("Art/2DItems/Weapons/Bow.dds"));
        Assert.Equal(0, store.Pending);
    }

    [Fact]
    public void NOTHINGGoesOutUntilSomebodyTurnsItOn()
    {
        // The one thing here that talks to the outside world, so it is a decision rather than
        // something to discover.
        string folder = Somewhere();
        int asked = 0;

        try
        {
            using var store = new ItemArtStore(folder, (path, token) =>
            {
                Interlocked.Increment(ref asked);
                return Task.FromResult<byte[]?>([1]);
            });

            for (int i = 0; i < 20; i++)
            {
                Assert.Equal(string.Empty, store.Local("Art/2DItems/Weapons/Bow.dds"));
            }

            Settle(store);
            Assert.Equal(0, asked);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public void ANDNothingIsAskedForAPathThatSaysNothing()
    {
        using var store = new ItemArtStore(Somewhere(), (path, token) => Task.FromResult<byte[]?>([1]))
        {
            Enabled = true,
        };

        Assert.Equal(string.Empty, store.Local(null));
        Assert.Equal(string.Empty, store.Local(string.Empty));
        Assert.Equal(string.Empty, store.Local("   "));
        Assert.Equal(0, store.Pending);
    }

    [Theory]
    [InlineData("Art/2DItems/Weapons/Bow.dds", "Art/2DItems/Weapons/Bow")]
    [InlineData("Art\\2DItems\\Weapons\\Bow.dds", "Art/2DItems/Weapons/Bow")]
    [InlineData("Art/2DItems/Weapons/Bow.DDS", "Art/2DItems/Weapons/Bow")]
    [InlineData("  Art/2DItems/Bow.dds  ", "Art/2DItems/Bow")]
    [InlineData("Art/2DItems/Bow", "Art/2DItems/Bow")]
    public void APATHIsSpeltTheWayThePictureServerSpellsIt(string given, string expected)
        => Assert.Equal(expected, ItemArtStore.Normalise(given));

    [Fact]
    public void APICTUREIsNamedByAHashRatherThanByItsPath()
    {
        // The paths are deep, hold characters a file name may not, and run past the length
        // Windows accepts - all of which turn into a picture that silently never caches.
        using var store = new ItemArtStore(Somewhere());

        string file = store.FileFor("Art/2DItems/Armours/BodyArmours/BodyStrDex/BodyStrDex1.dds");
        string name = Path.GetFileName(file);

        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain('\\', name);
        Assert.True(name.Length < 40, name);

        // The same path always lands on the same file, or nothing would ever be found again.
        Assert.Equal(file, store.FileFor("Art/2DItems/Armours/BodyArmours/BodyStrDex/BodyStrDex1.dds"));
        Assert.NotEqual(file, store.FileFor("Art/2DItems/Armours/BodyArmours/BodyStrDex/BodyStrDex2.dds"));
    }
}
