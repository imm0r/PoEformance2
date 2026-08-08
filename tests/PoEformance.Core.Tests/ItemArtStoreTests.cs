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
    private static void Settle(ItemArtStore store)
    {
        for (int i = 0; i < 200 && store.Pending > 0; i++)
        {
            Thread.Sleep(10);
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
            Assert.Equal((1, 0), store.Tally);
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
            Assert.Equal((0, 1), store.Tally);
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
