using PoEformance.Features;

namespace PoEformance.Core.Tests;

/// <summary>
/// What the entity browser leaves out, and why it is keyed the way it is.
/// </summary>
/// <remarks>
/// The hazard this guards is a QUIET one: a list that leaves out the wrong row looks exactly
/// like a list of everything there is. Nothing on screen says a row is missing, so the rules
/// deciding what goes have to be settled here rather than noticed later.
/// </remarks>
public class EntityHidingTests
{
    private const string Doodad = "Metadata/MiscellaneousObjects/DoodadNoBlocking";
    private const string Chest = "Metadata/Chests/StrongBoxes/Arcanist";

    [Fact]
    public void AHiddenKindGoesWhereverItStands()
    {
        var hiding = new EntityHiding();
        hiding.HideKind(Doodad);

        Assert.True(hiding.Hides(Doodad, 0f, 0f));
        Assert.True(hiding.Hides(Doodad, 4000f, -900f));

        // And nothing else does. A kind is a path, not a prefix - hiding the scenery must not
        // take the strongbox with it.
        Assert.False(hiding.Hides(Chest, 0f, 0f));
    }

    [Fact]
    public void AHiddenOneGoesOnlyWhereItStood()
    {
        var hiding = new EntityHiding();
        hiding.HideOne(Doodad, 120f, 340f);

        Assert.True(hiding.Hides(Doodad, 120f, 340f));

        // The same kind elsewhere is a different thing and stays listed - that is the whole
        // difference between this button and the other one.
        Assert.False(hiding.Hides(Doodad, 400f, 340f));

        // A different kind on the same spot stays too: a place alone would hide whatever
        // later stands there.
        Assert.False(hiding.Hides(Chest, 120f, 340f));
    }

    [Fact]
    public void ASmallShiftIsStillTheSameThing()
    {
        // The position is a float the reader rounds, and a doodad can be re-placed a hair off
        // between loads - so an exact match would let a hidden thing come back and look like
        // the setting had been forgotten.
        var hiding = new EntityHiding();
        hiding.HideOne(Doodad, 120.4f, 339.6f);

        Assert.True(hiding.Hides(Doodad, 122f, 341f));
        Assert.False(hiding.Hides(Doodad, 120f + EntitySpot.Tolerance + 2f, 340f));
    }

    [Fact]
    public void HidingTheSameOneTwiceLeavesOneRecord()
    {
        // Pressing the button again is what somebody does when a row does not vanish
        // instantly. A second entry would only ever be found later, in the undo list, as a
        // duplicate row nobody can tell apart from the first.
        var hiding = new EntityHiding();
        hiding.HideOne(Doodad, 120f, 340f);
        hiding.HideOne(Doodad, 121f, 341f);

        Assert.Single(hiding.Spots);
    }

    [Fact]
    public void EverythingHiddenCanBeUnhidden()
    {
        var hiding = new EntityHiding();
        hiding.HideKind(Doodad);
        hiding.HideOne(Chest, 10f, 20f);
        Assert.Equal(2, hiding.Count);

        hiding.ShowKind(Doodad);
        Assert.False(hiding.Hides(Doodad, 0f, 0f));

        hiding.ShowOne(hiding.Spots[0]);
        Assert.False(hiding.Hides(Chest, 10f, 20f));
        Assert.False(hiding.Any);
    }

    [Fact]
    public void ShowEverythingEmptiesBothLists()
    {
        var hiding = new EntityHiding();
        hiding.HideKind(Doodad);
        hiding.HideOne(Chest, 10f, 20f);

        hiding.ShowEverything();

        Assert.False(hiding.Any);
        Assert.Empty(hiding.Kinds);
        Assert.Empty(hiding.Spots);
    }

    [Fact]
    public void OnlyRealChangesAskToBeSavedAgain()
    {
        // The save is a file write. Hiding what is already hidden, or showing what was never
        // hidden, must not spend one - and applying a settings file must not either, or every
        // launch rewrites the file it just read.
        var counted = 0;
        var hiding = new EntityHiding { Changed = () => counted++ };

        hiding.HideKind(Doodad);
        Assert.Equal(1, counted);

        hiding.HideKind(Doodad);
        hiding.ShowKind(Chest);
        Assert.Equal(1, counted);

        hiding.Use([Doodad], [new EntitySpot(Chest, 1, 2)]);
        Assert.Equal(1, counted);
    }

    [Fact]
    public void WhatWasSavedComesBackAsWhatIsHidden()
    {
        var hiding = new EntityHiding();
        hiding.Use([Doodad], [new EntitySpot(Chest, 10, 20)]);

        Assert.True(hiding.Hides(Doodad, 999f, 999f));
        Assert.True(hiding.Hides(Chest, 10f, 20f));

        // Replaces rather than adds: applying settings twice - which happens when the
        // configuration window saves - must not double the list.
        hiding.Use([Doodad], [new EntitySpot(Chest, 10, 20)]);
        Assert.Single(hiding.Kinds);
        Assert.Single(hiding.Spots);
    }

    [Fact]
    public void TheUndoListNamesTheKindAndThePlace()
    {
        // The row somebody reads in the hidden list months later. A metadata path is too long
        // to scan, and coordinates alone say nothing about what stood there.
        var spot = new EntitySpot(Doodad, 120, 340);
        Assert.Equal("DoodadNoBlocking  at 120, 340", spot.Describe());
    }
}
