using System.Buffers.Binary;
using PoEformance.Features;
using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// Turning a set of flags into "what is this character still supposed to do".
/// </summary>
/// <remarks>
/// The join is direct and that is the whole reason it works: QuestStates declares its
/// conditions as references into QuestFlags, and the set read out of the game is a bitset
/// indexed by QuestFlags ROW NUMBER. The foreign key and the bit index are the same number.
///
/// Built against synthetic .dat files rather than the game's, because the game's are 300 MB of
/// install nobody has in CI - and because a synthetic one can be made to hold the awkward
/// cases on purpose: a step with no conditions, a reference that names nothing, a row size
/// that disagrees with the column list.
/// </remarks>
public class QuestProgressTests
{
    /// <summary>Builds a .datc64: row count, rows, the separator, then the variable section.</summary>
    private static byte[] Dat(int rows, int rowSize, Action<byte[], int> fill, byte[] variable)
    {
        var bytes = new byte[4 + (rows * rowSize) + 8 + variable.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)rows);
        for (var row = 0; row < rows; row++)
        {
            fill(bytes, 4 + (row * rowSize));
        }

        for (var i = 0; i < 8; i++)
        {
            bytes[4 + (rows * rowSize) + i] = 0xBB;
        }

        variable.CopyTo(bytes, 4 + (rows * rowSize) + 8);
        return bytes;
    }

    [Fact]
    public void TheRowSizeIsDerivedFromTheFileRatherThanDeclared()
    {
        // The check the whole reader rests on: a .dat says how big its rows are by where it
        // puts the separator, so a column list that disagrees is caught instead of believed.
        byte[] bytes = Dat(5, 12, (_, _) => { }, new byte[16]);

        DatFile? file = DatFile.Parse(bytes);

        Assert.NotNull(file);
        Assert.Equal(5, file.Rows);
        Assert.Equal(12, file.RowSize);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(103)]
    public void ASeparatorIsFoundWhereverThePackedRowsHappenToEnd(int rowSize)
    {
        // WHAT THIS COST, live: the scan stepped four bytes at a time, which found QuestStates
        // (208 per row) and QuestFlags (12) and walked straight past Quest, whose row is 103
        // bytes - so the separator sat at an offset no multiple of four could reach. It
        // reported "in the install but did not parse", which reads like a missing file rather
        // than like a reader that could not see it. Rows are PACKED; nothing is aligned.
        byte[] bytes = Dat(7, rowSize, (_, _) => { }, new byte[16]);

        DatFile? file = DatFile.Parse(bytes);

        Assert.NotNull(file);
        Assert.Equal(7, file.Rows);
        Assert.Equal(rowSize, file.RowSize);
    }

    [Fact]
    public void ASeparatorInsideARowIsSteppedOverRatherThanBelieved()
    {
        // A row is free to hold these bytes as data. The divisibility check is what tells the
        // two apart, and the search has to CARRY ON when it fails rather than give up.
        byte[] bytes = Dat(4, 9, (b, at) =>
        {
            if (at == 4)
            {
                // Row 0 holds an 0xBB run at a position no row boundary could explain.
                for (var i = 0; i < 8; i++)
                {
                    b[at + 1 + i] = 0xBB;
                }
            }
        }, new byte[16]);

        DatFile? file = DatFile.Parse(bytes);

        Assert.NotNull(file);
        Assert.Equal(9, file.RowSize);
    }

    [Fact]
    public void AStringColumnIsAnOffsetIntoTheVariableSection()
    {
        byte[] text = System.Text.Encoding.Unicode.GetBytes("EnteredHideout\0");
        // Offset 8 is the first byte AFTER the separator, because offsets count from it.
        byte[] bytes = Dat(1, 8, (b, at) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(at), 8), text);

        Assert.Equal("EnteredHideout", DatFile.Parse(bytes)!.Text(0, 0));
    }

    [Fact]
    public void AColumnListThatIsRightAboutItsFrontIsCheckedByReading()
    {
        // THE LIVE CASE. Quest.datc64 is 119 bytes where the community schema computes 103 -
        // one 16-byte column added somewhere - but both of that schema's variants agree on the
        // first four columns, and Id, Act and Name are three of them. Rejecting the table over
        // its tail lost every quest's name, so a size mismatch now asks the table whether the
        // fields being read hold what they should.
        byte[] text = System.Text.Encoding.Unicode.GetBytes("The Runeseeker\0");
        byte[] bytes = Dat(8, 24, (b, at) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(at + 12), 8), text);

        DatFile file = DatFile.Parse(bytes)!;

        Assert.True(file.ReadsAsText([12]));        // where the schema says Name is
        Assert.False(file.ReadsAsText([0]));        // and where it is not
    }

    [Fact]
    public void AFailedCheckSaysWhereTheStringsActuallyAre()
    {
        // So a layout that stopped matching reports where its columns went, instead of only
        // that it failed - which is the difference between one round trip and several.
        byte[] text = System.Text.Encoding.Unicode.GetBytes("EnteredHideout\0");
        byte[] bytes = Dat(8, 24, (b, at) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(at + 4), 8), text);

        Assert.Contains(4, DatFile.Parse(bytes)!.TextOffsets());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheArrayWordOrderIsMeasuredRatherThanAssumed(bool countFirst)
    {
        // TWO BELIEFS POINTING OPPOSITE WAYS. This reader was written count-first; the AHK
        // tool's layout table says "8-byte offset + 8-byte count" - and that tool never decodes
        // an array, so its comment is a belief and not a test. Guessing produces empty
        // conditions, which makes every quest step hold, which shows a plausible objective from
        // the wrong step. So the file is asked, and a table written either way must be read.
        const int Count = 3;
        var variable = new byte[8 + (Count * 16)];      // room for the array past the separator
        byte[] bytes = Dat(6, 16, (b, at) =>
        {
            ulong count = Count;
            ulong into = 8;
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(at), countFirst ? count : into);
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(at + 8), countFirst ? into : count);
        }, variable);

        DatFile file = DatFile.Parse(bytes)!;

        Assert.Equal(countFirst ? ArrayOrder.CountFirst : ArrayOrder.OffsetFirst, file.DetectArrays(0));
        Assert.Equal(Count, file.References(0, 0).Count);
    }

    [Fact]
    public void ReadingAnArrayTheWrongWayRoundFindsNothingRatherThanGarbage()
    {
        // The failure this is guarding: an offset read as a count is far too big for the bound,
        // so the array comes back empty - a condition nobody declared, and a step that always
        // holds. Empty is the safe half of the wrong answer; the detection above is the fix.
        var variable = new byte[8 + (3 * 16)];
        byte[] bytes = Dat(6, 16, (b, at) =>
        {
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(at), 8);          // offset first
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(at + 8), 3);      // count second
        }, variable);

        DatFile file = DatFile.Parse(bytes)!;

        Assert.Empty(file.References(0, 0));            // read count-first by default: nothing
        file.DetectArrays(0);
        Assert.Equal(3, file.References(0, 0).Count);   // measured: three
    }

    [Fact]
    public void AReferenceAnswersWithWhicheverHalfIsARow()
    {
        // Which of the two 64-bit words is the row is not written down anywhere this project
        // trusts - the memory layout puts the row second - so it is resolved by asking rather
        // than assumed, and both orders have to work.
        var first = new DatReference(7, DatReference.Nothing);
        var second = new DatReference(0xFFFF_FFFF_FFFF_FFFF, 7);

        Assert.Equal(7, first.RowIn(100));
        Assert.Equal(7, second.RowIn(100));
        Assert.Equal(-1, first.RowIn(3));       // 7 is not a row of a 3-row table
    }

    [Fact]
    public void ColumnOffsetsAreTheSumOfTheWidthsInFront()
    {
        // QuestFlags is the check with an independent witness: Id (string, 8) then HASH32
        // (i32, 4) computes 12, and the schema measured the rows 0x0C apart in real memory.
        List<DatColumn> columns = [new("Id", "string", false), new("HASH32", "i32", false)];

        Assert.Equal(12, DatColumns.Layout(columns));
        Assert.Equal(0, columns[0].Offset);
        Assert.Equal(8, columns[1].Offset);
    }

    [Fact]
    public void AnUnpricedTypeAbandonsTheLayoutRatherThanLeavingAHole()
    {
        // Every offset after an unknown width is a guess, and a guess that looks like an
        // answer is the expensive kind.
        Assert.Equal(0, DatColumns.Layout([new("Id", "string", false), new("X", "somethingnew", false)]));
    }

    [Fact]
    public void TheVendoredLayoutsMatchTheTablesTheyDescribe()
    {
        QuestTableLayouts? layouts = QuestTableLayouts.Load(DataFile("quest-tables.json"));

        Assert.NotNull(layouts);
        Assert.Equal(12, layouts.RowSizeOf("QuestFlags"));
        Assert.True(layouts.RowSizeOf("Quest") > 0);
        Assert.True(layouts.RowSizeOf("QuestStates") > 0);

        // The columns the join needs, by name. A schema refresh that renames one turns the
        // feature off with a note rather than reading the wrong field.
        Assert.True(layouts.OffsetOf("QuestStates", "FlagsPresent") >= 0);
        Assert.True(layouts.OffsetOf("QuestStates", "FlagsMissing") >= 0);
        Assert.True(layouts.OffsetOf("QuestStates", "Text") >= 0);
        Assert.True(layouts.OffsetOf("QuestStates", "Quest") >= 0);
        Assert.True(layouts.OffsetOf("Quest", "Name") >= 0);
    }

    [Fact]
    public void TheStepInForceIsTheLastOneWhoseConditionsHold()
    {
        // Steps are cumulative - an early one asks for flags a later one also has - so the
        // FIRST match is where the character has already been. Taking it would report every
        // quest as being on step one forever.
        QuestStep one = new(1, [10], [], "kill it", string.Empty, []);
        QuestStep two = new(2, [10, 11], [], "come back", string.Empty, []);
        QuestStep three = new(3, [10, 11, 12], [], "done", string.Empty, []);

        var set = new HashSet<int> { 10, 11 };

        Assert.True(one.Holds(set));
        Assert.True(two.Holds(set));
        Assert.False(three.Holds(set));
    }

    [Fact]
    public void AFlagThatMustBeAbsentBlocksTheStep()
    {
        QuestStep step = new(1, [10], [11], "before you talk to him", string.Empty, []);

        Assert.True(step.Holds(new HashSet<int> { 10 }));
        Assert.False(step.Holds(new HashSet<int> { 10, 11 }));
    }

    [Fact]
    public void AWholeQuestResolvesToItsCurrentObjective()
    {
        // ORDER COUNTS DOWN, read off the game: "Finding the Forge" runs order 4 "Speak to
        // Renly in Clearfell Encampment" through order 0 "Quest Complete". So the fixture's
        // order 2 is the first step and order 1 the last, and a character holding only the
        // first flag is on the first step.
        (LoadedTable quests, LoadedTable states, LoadedTable flags, QuestTableLayouts layouts) = Tiny();

        QuestOutlook outlook = QuestProgress.Read(quests, states, flags, layouts, [0]);

        QuestState quest = Assert.Single(outlook.Quests);
        Assert.Equal("The Runeseeker", quest.Name);
        Assert.Equal("Speak to Farrow", quest.Objective);
        Assert.Equal(2, quest.Now?.Order);
        Assert.False(quest.Complete);
        Assert.Equal("Return to town", quest.Next?.Text);
        Assert.Equal(1, quest.Next?.Order);
    }

    [Fact]
    public void MoreThanOneStepHoldingIsReportedRatherThanResolvedQuietly()
    {
        // WHAT THE FIRST LIVE RUN SHOWED. The window offered "Quest Complete" as the current
        // objective with an EARLIER step as "then", and named a step the game's own tracker did
        // not - all three symptoms of several steps holding at once. The data intends exactly
        // one: FlagsMissing rules out the ones already passed. So several holding is a fact
        // about the READ, and hiding it behind a pick makes a wrong answer look like a right
        // one.
        (LoadedTable quests, LoadedTable states, LoadedTable flags, QuestTableLayouts layouts) = Tiny();

        // Both steps ask only for flags this character has, and neither excludes the other.
        QuestOutlook outlook = QuestProgress.Read(quests, states, flags, layouts, [0, 1]);

        QuestState quest = Assert.Single(outlook.Quests);
        Assert.Equal(2, quest.Holding.Count);
        Assert.Single(outlook.Ambiguous);
    }

    [Fact]
    public void OneStepHoldingIsNotAmbiguous()
    {
        (LoadedTable quests, LoadedTable states, LoadedTable flags, QuestTableLayouts layouts) = Tiny();

        QuestOutlook outlook = QuestProgress.Read(quests, states, flags, layouts, [0]);

        Assert.Single(Assert.Single(outlook.Quests).Holding);
        Assert.Empty(outlook.Ambiguous);
    }

    [Fact]
    public void SettingTheNextFlagAdvancesTheQuest()
    {
        (LoadedTable quests, LoadedTable states, LoadedTable flags, QuestTableLayouts layouts) = Tiny();

        QuestOutlook outlook = QuestProgress.Read(quests, states, flags, layouts, [0, 1]);

        QuestState quest = Assert.Single(outlook.Quests);
        Assert.Equal("Return to town", quest.Objective);
        Assert.Equal(1, quest.Now?.Order);       // the LOWER order is the further step
        Assert.True(quest.Complete);
        Assert.Empty(outlook.Active);
    }

    [Fact]
    public void TheShortColumnIsTheObjectiveAndTheLongOneSitsUnderIt()
    {
        // MEASURED, by showing both and holding the window next to the game's own panel:
        // Message matched it word for word - "Find the Red Vale", "Search for the meaning of
        // the Runes etched into the Tree of Souls" - while Text was the longer sentence every
        // time. The schema's names do not say which is which; both are just a string.
        QuestStep step = new(1, [], [], "The Devourer lives underground in a Mud Burrow. Find it.", "Slay the Devourer", []);

        Assert.Equal("Slay the Devourer", step.Line);
        Assert.Equal("The Devourer lives underground in a Mud Burrow. Find it.", step.Detail);
    }

    [Fact]
    public void AStepWithNoShortFormFallsBackToTheLongOne()
    {
        // And says nothing twice: a step whose only text is the long one shows it as the
        // objective, with no detail line repeating it underneath.
        QuestStep step = new(1, [], [], "Find Renly's tools.", string.Empty, []);

        Assert.Equal("Find Renly's tools.", step.Line);
        Assert.Equal(string.Empty, step.Detail);
    }

    [Fact]
    public void TheRouteIsTheCurrentStepAndEverythingAfterIt()
    {
        // "What must be done, step by step" is already in the data - QuestStates carries every
        // state of every quest with its words, in order. What is behind is a count; what is
        // ahead is the list.
        (LoadedTable quests, LoadedTable states, LoadedTable flags, QuestTableLayouts layouts) = Tiny();

        QuestState first = Assert.Single(QuestProgress.Read(quests, states, flags, layouts, [0]).Quests);

        Assert.Equal(0, first.Passed);
        Assert.Equal(["Speak to Farrow", "Return to town"], first.Remaining.Select(s => s.Line));
    }

    [Fact]
    public void TheRouteShortensAsTheQuestAdvances()
    {
        (LoadedTable quests, LoadedTable states, LoadedTable flags, QuestTableLayouts layouts) = Tiny();

        QuestState later = Assert.Single(QuestProgress.Read(quests, states, flags, layouts, [0, 1]).Quests);

        Assert.Equal(1, later.Passed);
        Assert.Equal(["Return to town"], later.Remaining.Select(s => s.Line));
    }

    [Fact]
    public void AQuestNobodyHasStartedOffersItsWholeRoute()
    {
        // No step holds, so there is nothing behind and everything ahead - which is the useful
        // answer for a quest not begun, rather than an empty list.
        (LoadedTable quests, LoadedTable states, LoadedTable flags, QuestTableLayouts layouts) = Tiny();

        QuestState none = Assert.Single(QuestProgress.Read(quests, states, flags, layouts, []).Quests);

        Assert.Null(none.Now);
        Assert.Equal(0, none.Passed);
        Assert.Equal(2, none.Remaining.Count);
    }

    [Fact]
    public void AStepNamesThePlaceItPointsAt()
    {
        // MapPins is the "where" half. It carries no coordinates - Name, WorldArea and Act -
        // so a pin answers WHICH PLACE and not which point, which is what a world-map pin is.
        QuestStep step = new(1, [], [], "Find it.", "Slay the Devourer", ["Mud Burrow", "Clearfell"]);

        Assert.Equal("Mud Burrow, Clearfell", step.Where);
    }

    [Fact]
    public void AStepWithNoPinSaysNothingAboutWhere()
    {
        Assert.Equal(string.Empty, new QuestStep(1, [], [], "x", "y", []).Where);
    }

    [Fact]
    public void WithoutMapPinsTheStepsStillRead()
    {
        // The pins are OPTIONAL on purpose: a missing MapPins costs the "where" of a step and
        // nothing else, so it must not switch the whole feature off.
        (LoadedTable quests, LoadedTable states, LoadedTable flags, QuestTableLayouts layouts) = Tiny();

        QuestState quest = Assert.Single(QuestProgress.Read(quests, states, flags, layouts, [0]).Quests);

        Assert.Equal("Speak to Farrow", quest.Objective);
        Assert.All(quest.Steps, s => Assert.Empty(s.Places));
    }

    [Fact]
    public void ProgressRunsFromTheHighestOrderToTheLowest()
    {
        // WHAT SORTING IT THE OTHER WAY DID, and it was invisible in every synthetic test
        // until the game was held next to the window: a FINISHED quest reported its completion
        // state as the current objective with an earlier step as "then", and a quest genuinely
        // in progress reported itself finished and was hidden - which is why two quests the
        // game's own tracker listed were missing from this window entirely.
        (LoadedTable quests, LoadedTable states, LoadedTable flags, QuestTableLayouts layouts) = Tiny();

        QuestOutlook outlook = QuestProgress.Read(quests, states, flags, layouts, [0]);
        QuestState quest = Assert.Single(outlook.Quests);

        Assert.Equal([2, 1], quest.Steps.Select(s => s.Order));
        Assert.Single(outlook.Active);
    }

    [Fact]
    public void ConsecutiveStatesWordedTheSameWayFoldIntoOneLeg()
    {
        // THE RUNESEEKER'S 87. Most of them are the same sentence for the different regions the
        // quest can be done in, so listed one per line the route is a wall of one sentence.
        QuestStep[] steps =
        [
            new(3, [], [], string.Empty, "Search the region for more Runestones", ["The Mire"]),
            new(3, [], [], string.Empty, "Search the region for more Runestones", ["The Bluff"]),
            new(3, [], [], string.Empty, "Search the region for more Runestones", ["The Vale"]),
            new(2, [], [], string.Empty, "Return to Farrow", ["Clearfell"]),
        ];

        IReadOnlyList<QuestLeg> route = QuestState.Fold(steps);

        Assert.Equal(2, route.Count);
        Assert.Equal(3, route[0].States);
        Assert.True(route[0].Branches);
        Assert.Equal(1, route[1].States);
        Assert.False(route[1].Branches);
    }

    [Fact]
    public void TheFoldKeepsEveryPlaceBecauseThePlaceIsWhatDiffered()
    {
        // The whole reason folding says MORE and not less: the sentence never changed, the
        // region did. Gathering them onto one line is shorter than the wall AND carries the only
        // part the wall was varying.
        QuestStep[] steps =
        [
            new(3, [], [], string.Empty, "Search the region", ["The Mire"]),
            new(3, [], [], string.Empty, "Search the region", ["The Bluff"]),
            new(3, [], [], string.Empty, "Search the region", ["The Mire"]),
        ];

        QuestLeg leg = Assert.Single(QuestState.Fold(steps));

        Assert.Equal("The Mire, The Bluff", leg.Where);
    }

    [Fact]
    public void TwoStretchesOfTheSameSentenceWithSomethingBetweenStayTwo()
    {
        // Only CONSECUTIVE states fold, so the route is never rearranged to make the folding
        // look tidier. A quest that really does come back to something says so.
        QuestStep[] steps =
        [
            new(3, [], [], string.Empty, "Search the region", []),
            new(2, [], [], string.Empty, "Speak to Farrow", []),
            new(1, [], [], string.Empty, "Search the region", []),
        ];

        Assert.Equal(3, QuestState.Fold(steps).Count);
    }

    [Fact]
    public void AStateWithNoWordsIsDroppedRatherThanSplittingARun()
    {
        // Left in, it would break a run in two and the route would say the same sentence twice
        // in a row - which is exactly the thing the fold exists to stop.
        QuestStep[] steps =
        [
            new(3, [], [], string.Empty, "Search the region", []),
            new(3, [], [], string.Empty, string.Empty, []),
            new(3, [], [], string.Empty, "Search the region", []),
        ];

        QuestLeg leg = Assert.Single(QuestState.Fold(steps));

        Assert.Equal(2, leg.States);
    }

    [Fact]
    public void TheLongFormComesFromTheFirstStateThatHasOne()
    {
        // Branch states routinely leave Text empty on some of their number and fill it on
        // others, so taking the first state's would lose it for the whole leg.
        QuestStep[] steps =
        [
            new(3, [], [], string.Empty, "Search the region", []),
            new(3, [], [], "Runestones are scattered across the region.", "Search the region", []),
        ];

        Assert.Equal("Runestones are scattered across the region.", QuestState.Fold(steps).Single().Detail);
    }

    [Fact]
    public void TheRouteIsWhatIsLeftRatherThanTheWholeQuest()
    {
        (LoadedTable quests, LoadedTable states, LoadedTable flags, QuestTableLayouts layouts) = Tiny();

        QuestState quest = Assert.Single(QuestProgress.Read(quests, states, flags, layouts, [0]).Quests);

        Assert.Equal(["Speak to Farrow", "Return to town"], quest.Route.Select(l => l.Line));
    }

    [Fact]
    public void StatesThatTieOnOrderComeOutInTheSameOrderEveryTime()
    {
        // NOT A STYLE POINT. Branch states share an Order, and List.Sort is an introsort, which
        // puts ties in an arbitrary relative order. Two things depend on that order being the
        // same every read: which of several holding steps is picked as current, and which states
        // end up adjacent for the route to fold.
        (LoadedTable quests, LoadedTable states, LoadedTable flags, QuestTableLayouts layouts) = Tiny();

        IReadOnlyList<QuestStep> first = QuestProgress.Read(quests, states, flags, layouts, [0]).Quests[0].Steps;
        IReadOnlyList<QuestStep> again = QuestProgress.Read(quests, states, flags, layouts, [0]).Quests[0].Steps;

        Assert.Equal(first.Select(s => s.Text), again.Select(s => s.Text));
    }

    /// <summary>
    /// A two-step quest with two flags, as three synthetic tables.
    /// </summary>
    /// <remarks>
    /// Laid out by the same arithmetic the real thing uses, so a width correction moves these
    /// too - the fixture cannot quietly stop describing what the reader reads.
    /// </remarks>
    private static (LoadedTable Quests, LoadedTable States, LoadedTable Flags, QuestTableLayouts Layouts) Tiny()
    {
        var layouts = new QuestTableLayouts
        {
            Tables =
            {
                ["Quest"] = [new("Id", "string", false), new("Act", "i32", false), new("Name", "string", false)],
                ["QuestFlags"] = [new("Id", "string", false), new("HASH32", "i32", false)],
                ["QuestStates"] =
                [
                    new("Quest", "foreignrow", false),
                    new("Order", "i32", false),
                    new("FlagsPresent", "foreignrow", true),
                    new("FlagsMissing", "foreignrow", true),
                    new("Text", "string", false),
                    new("Message", "string", false),
                ],
            },
        };

        int questSize = layouts.RowSizeOf("Quest");
        int stateSize = layouts.RowSizeOf("QuestStates");
        int flagSize = layouts.RowSizeOf("QuestFlags");

        // Variable section: strings at known offsets, then the two one-element flag arrays.
        // Offsets into the variable-length section are relative to the SEPARATOR, not to the
        // first byte after it - so everything written here is eight bytes further on than its
        // position in the blob. Getting that wrong reads four characters into the string
        // before the one you wanted, which looks like a real string and is not.
        const int Magic = 8;
        var blob = new List<byte>();
        int Put(string text)
        {
            int at = blob.Count;
            blob.AddRange(System.Text.Encoding.Unicode.GetBytes(text + "\0"));
            return at + Magic;
        }

        int questId = Put("a1q3");
        int questName = Put("The Runeseeker");
        int step1 = Put("Speak to Farrow");
        int step2 = Put("Return to town");
        int flag0 = Put("a1q3-Started");
        int flag1 = Put("a1q3-SpokeToFarrow");

        int at0 = blob.Count;                          // one reference to flag row 0
        blob.AddRange(new byte[16]);
        int at1 = blob.Count;                          // one reference to flag row 1
        blob.AddRange(new byte[16]);
        Span<byte> span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(blob);
        BinaryPrimitives.WriteUInt64LittleEndian(span[at0..], 0);
        BinaryPrimitives.WriteUInt64LittleEndian(span[(at0 + 8)..], DatReference.Nothing);
        BinaryPrimitives.WriteUInt64LittleEndian(span[at1..], 1);
        BinaryPrimitives.WriteUInt64LittleEndian(span[(at1 + 8)..], DatReference.Nothing);
        int array0 = at0 + Magic;
        int array1 = at1 + Magic;
        byte[] variable = [.. blob];

        byte[] questBytes = Dat(1, questSize, (b, at) =>
        {
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(at + layouts.OffsetOf("Quest", "Id")), (ulong)questId);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(at + layouts.OffsetOf("Quest", "Act")), 1);
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(at + layouts.OffsetOf("Quest", "Name")), (ulong)questName);
        }, variable);

        byte[] flagBytes = Dat(2, flagSize, (_, _) => { }, variable);
        {
            // Row 0 and row 1 of QuestFlags, named.
            BinaryPrimitives.WriteUInt64LittleEndian(flagBytes.AsSpan(4), (ulong)flag0);
            BinaryPrimitives.WriteUInt64LittleEndian(flagBytes.AsSpan(4 + flagSize), (ulong)flag1);
        }

        var order = 0;
        byte[] stateBytes = Dat(2, stateSize, (b, at) =>
        {
            order++;
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(at + layouts.OffsetOf("QuestStates", "Quest")), 0);
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(at + layouts.OffsetOf("QuestStates", "Quest") + 8), DatReference.Nothing);

            // ORDER COUNTS DOWN, so the first step of this two-step quest is order 2 and the
            // last is order 1. Numbering them 1 then 2 modelled the game backwards, and every
            // test here passed against it - which is exactly how the real direction went
            // unnoticed until the window was held next to the game's own tracker.
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(at + layouts.OffsetOf("QuestStates", "Order")), 3 - order);

            int present = layouts.OffsetOf("QuestStates", "FlagsPresent");
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(at + present), 1);
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(at + present + 8), (ulong)(order == 1 ? array0 : array1));

            BinaryPrimitives.WriteUInt64LittleEndian(
                b.AsSpan(at + layouts.OffsetOf("QuestStates", "Text")), (ulong)(order == 1 ? step1 : step2));
        }, variable);

        return (
            new LoadedTable(DatFile.Parse(questBytes)!, "quest", questSize),
            new LoadedTable(DatFile.Parse(stateBytes)!, "states", stateSize),
            new LoadedTable(DatFile.Parse(flagBytes)!, "flags", flagSize),
            layouts);
    }

    private static string DataFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "data", name)))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "data", name);
    }
}
