using PoEformance.Game.Components;

namespace PoEformance.Core.Tests;

/// <summary>
/// Addressing the game's animation table directly, and refusing to until it is safe.
/// </summary>
/// <remarks>
/// THE WHOLE RISK IS IN THE BASE. One sighting of one row gives an address that computes
/// perfectly, and if that row pointer were ever something else - a different table, a stale read,
/// an offset that drifted a patch ago - the base would still compute, every row would still
/// "read", and the output would be a complete table of confident nonsense that a person would
/// then commit over a working one.
///
/// So two DIFFERENT animations have to agree before the base is used, and these tests are what
/// says that rule holds - including the cases that look like agreement and are not.
/// </remarks>
public class AnimationTableTests
{
    /// <summary>A plausible heap address to hang a table off.</summary>
    private const ulong Base = 0x1_4000_0000;

    private static ulong Row(int id) => Base + (ulong)(id * AnimationTable.RowStride);

    [Fact]
    public void OneSightingIsNeverEnough()
    {
        var table = new AnimationTable();

        Assert.False(table.Observe(299, Row(299)));
        Assert.False(table.IsConfirmed);
        Assert.Equal(1, table.Observations);
    }

    [Fact]
    public void TwoDifferentAnimationsThatAgreeConfirmIt()
    {
        var table = new AnimationTable();

        Assert.False(table.Observe(299, Row(299)));
        Assert.True(table.Observe(889, Row(889)));

        Assert.True(table.IsConfirmed);
        Assert.Equal(Base, table.Base);
        Assert.Equal((299, 889), table.ConfirmedBy);
    }

    [Fact]
    public void TheSameAnimationTwiceIsNotTwoObservations()
    {
        // It agrees by arithmetic rather than by evidence: the same id and the same row give the
        // same base whatever the row actually is, so it checks nothing at all.
        var table = new AnimationTable();

        Assert.False(table.Observe(299, Row(299)));
        Assert.False(table.Observe(299, Row(299)));
        Assert.False(table.Observe(299, Row(299)));
        Assert.False(table.IsConfirmed);
    }

    [Fact]
    public void SightingsThatDisagreeConfirmNothingAndTheNEWEROneIsKept()
    {
        // At least one of the two is wrong and there is no way to tell which, so neither is
        // trusted. Keeping the NEWER one matters: holding the older would let one bad sighting at
        // the start of a session refuse every good one after it.
        var table = new AnimationTable();

        Assert.False(table.Observe(299, Row(299) + 8));  // a wrong row, off by a field
        Assert.False(table.Observe(889, Row(889)));      // good, but disagrees with the above
        Assert.False(table.IsConfirmed);

        // A third sighting agreeing with the second settles it.
        Assert.True(table.Observe(472, Row(472)));
        Assert.Equal(Base, table.Base);
        Assert.Equal((889, 472), table.ConfirmedBy);
    }

    [Fact]
    public void NonsenseSightingsAreNotCounted()
    {
        var table = new AnimationTable();

        Assert.False(table.Observe(299, 0));                      // no row
        Assert.False(table.Observe(-1, Row(1)));                  // no such animation
        Assert.False(table.Observe(AnimationTable.MostIds + 1, Row(1)));
        Assert.Equal(0, table.Observations);
    }

    [Fact]
    public void RowsAreAddressedByStrideOnceConfirmed()
    {
        var table = new AnimationTable();
        table.Observe(299, Row(299));
        table.Observe(889, Row(889));

        Assert.Equal(Base, table.RowOf(0));
        Assert.Equal(Base + 106, table.RowOf(1));
        Assert.Equal(Row(1083), table.RowOf(1083));
    }

    [Fact]
    public void NothingIsReadBeforeTheBaseIsConfirmed()
    {
        // The refusal that matters. An unconfirmed table asked for a name must answer null rather
        // than reading from an address it has not earned.
        var memory = new FakeMemoryReader();
        var table = new AnimationTable();
        table.Observe(299, Row(299));

        Assert.Null(table.NameOf(memory, 299));
        Assert.Empty(table.ReadAll(memory));
    }

    [Fact]
    public void AConfirmedTableReadsEveryRowItCan()
    {
        // A stand-in table: each row's first field points at its name. Three rows, then nothing -
        // so the walk also has to stop rather than running to the id ceiling.
        var memory = new FakeMemoryReader();
        string[] names = ["Idle", "FixedRun", "DodgeRoll"];
        for (int id = 0; id < names.Length; id++)
        {
            ulong text = 0x2_0000_0000 + (ulong)(id * 0x100);
            var page = new byte[128];
            System.Text.Encoding.Unicode.GetBytes(names[id], page);
            memory.Place(text, page);
            memory.Place(Row(id), BitConverter.GetBytes(text));
        }

        var table = new AnimationTable();
        table.Observe(1, Row(1));
        table.Observe(2, Row(2));
        Assert.True(table.IsConfirmed);

        IReadOnlyDictionary<int, string> read = table.ReadAll(memory);
        Assert.Equal(3, read.Count);
        Assert.Equal("Idle", read[0]);
        Assert.Equal("DodgeRoll", read[2]);
        Assert.Equal("FixedRun", table.NameOf(memory, 1));
        Assert.Null(table.NameOf(memory, 3));
    }
}
